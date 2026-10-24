using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Dictionary;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Storage;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace HoverTranslate.Core.TextSource;

public interface IOcrTextSource
{
    /// <summary>Установлен ли OCR для английского (en-US) в системе.</summary>
    bool IsAvailable { get; }

    Task<TextAttempt> TryGetAsync(ScreenPoint point, CancellationToken ct);

    /// <summary>Весь текст в прямоугольнике (физические px): абзацы через перевод строки; пустая строка — текста нет.</summary>
    Task<string> RecognizeAreaAsync(ScreenRect area, CancellationToken ct);

    /// <summary>Прогрев: D3D-устройство, сессия захвата и движок OCR (первый вызов иначе ~300 мс).</summary>
    Task WarmUpAsync(CancellationToken ct);
}

/// <summary>Чем сделан снимок для OCR: способ, пустой ли кадр и почему не сработал основной способ.</summary>
public readonly record struct CaptureReport(string? Method, bool Blank, string? Skipped)
{
    public override string ToString() => Method is null ? "—"
        : Method + (Blank ? ", кадр пустой" : "") + (Skipped is null ? "" : $"; {Skipped}");
}

/// <summary>
/// Текст под курсором через снимок экрана + Windows.Media.Ocr (en-US). Работает везде, где текст
/// виден на экране: игры в оконном режиме, игровые движки с собственным интерфейсом, приложения без UIA.
/// </summary>
public sealed class OcrTextSource : IOcrTextSource
{
    /// <summary>Область снимка вокруг курсора в логических пикселях.</summary>
    public const double RegionLogicalWidth = 800, RegionLogicalHeight = 240;
    /// <summary>Увеличение снимка перед OCR: мелкий экранный шрифт распознаётся заметно лучше.</summary>
    public const double OcrScale = 2.0;
    /// <summary>Допуск «ближайшего слова» в логических пикселях.</summary>
    public const double HitToleranceLogical = 10;

    private readonly GraphicsCaptureService _wgc;
    private readonly GdiScreenCapture _gdi;
    private readonly bool _wgcSupported = GraphicsCaptureService.IsSupported;
    private readonly ILogger<OcrTextSource> _log;
    private readonly OcrEngine? _engine;
    private readonly OcrDebugDump? _dump;
    private readonly OcrCorrector? _corrector;
    private readonly AppSettings? _settings;
    private readonly Lazy<PaddleOcr?> _fallback;
    private bool _primaryBroken;

    public bool IsAvailable => _engine is not null;

    /// <summary>Основной способ захвата: WGC (по умолчанию) или BitBlt; второй — запасной.</summary>
    public bool PreferGdiCapture { get; set; }

    /// <summary>Длительность последнего захвата и распознавания (для замеров).</summary>
    public (long CaptureMs, long OcrMs) LastTimings { get; private set; }

    /// <summary>Каким способом сделан последний снимок и не пустой ли он.</summary>
    public CaptureReport LastCapture { get; private set; }

    /// <summary>Чем прочитан последний снимок: «Windows OCR», «Windows OCR ×1» или «PaddleOCR … мс».</summary>
    public string LastEngine { get; private set; } = "";

    /// <param name="dictionary">Словарь для исправления ошибок OCR (<see cref="OcrCorrector"/>); без него слова остаются как распознаны.</param>
    /// <param name="dump">Отладочный дамп (только в режиме --debug), иначе null.</param>
    /// <param name="settings">Настройки: запасной OCR можно выключить; без них он включён, если есть модели.</param>
    public OcrTextSource(GraphicsCaptureService wgc, GdiScreenCapture gdi, ILogger<OcrTextSource> log, IDictionaryService? dictionary = null, OcrDebugDump? dump = null, AppSettings? settings = null)
    {
        _log = log;
        _dump = dump;
        _settings = settings;
        _fallback = new Lazy<PaddleOcr?>(LoadFallback, LazyThreadSafetyMode.ExecutionAndPublication);
        if (dictionary is { IsAvailable: true }) _corrector = new OcrCorrector(dictionary.Zipf);
        if (dump is not null) _log.LogInformation("Отладка OCR: снимки и сырой текст сохраняются в {Dir}", dump.Folder);
        _wgc = wgc;
        _gdi = gdi;
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        if (_engine is null)
            _log.LogError("OCR для en-US не установлен. Параметры → Время и язык → Язык → English (United States) → Распознавание текста");
    }

    /// <summary>Запасной OCR включён в настройках (и есть словарь, по которому видно, что Windows OCR прочитал плохо).</summary>
    private bool FallbackEnabled => _corrector is not null && (_settings?.FallbackOcrEnabled ?? true);

    /// <summary>Запасной движок или null (выключен, нет моделей). Модели грузятся при первом обращении, а не при каждом OCR.</summary>
    private PaddleOcr? Fallback() => FallbackEnabled ? _fallback.Value : null;

    private PaddleOcr? LoadFallback()
    {
        if (!PaddleOcr.ModelsExist(PaddleOcr.ModelDir))
        {
            _log.LogInformation("Запасной OCR (PaddleOCR) выключен: нет моделей в {Dir} (tools/DataBuilder/export_ppocr.py)", PaddleOcr.ModelDir);
            return null;
        }
        try
        {
            var sw = Stopwatch.StartNew();
            var engine = new PaddleOcr(PaddleOcr.ModelDir);
            _log.LogInformation("Запасной OCR (PaddleOCR) загружен за {Ms} мс", sw.ElapsedMilliseconds);
            return engine;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Запасной OCR (PaddleOCR) не загрузился, работает только Windows OCR");
            return null;
        }
    }

    public async Task<TextAttempt> TryGetAsync(ScreenPoint point, CancellationToken ct)
    {
        if (_engine is null) return TextAttempt.Fail("OCR en-US не установлен в Windows");

        var monitor = ScreenGeometry.MonitorFromPoint(point);
        var region = ScreenGeometry.CaptureRegionAround(point, monitor, RegionLogicalWidth, RegionLogicalHeight);
        var windowMode = WindowInfo.DescribeMode(point);
        double tolerance = HitToleranceLogical * monitor.Scale;
        var lines = await RecognizeAsync(_engine, region, monitor, windowMode, ct).ConfigureAwait(false);
        var (captureMs, ocrMs) = LastTimings;

        // Строка со словом упирается в край снимка (предложение оборвано) — снимаем ещё раз на всю ширину монитора.
        string widened = "";
        if (OcrLayout.WiderRegion(lines, point, region, monitor.Bounds, tolerance) is { } wide)
        {
            var wideLines = await RecognizeAsync(_engine, wide, monitor, windowMode, ct).ConfigureAwait(false);
            captureMs += LastTimings.CaptureMs;
            ocrMs += LastTimings.OcrMs;
            lines = wideLines;
            widened = ", область расширена";
        }

        string timing = $"захват {captureMs} мс ({LastCapture}), OCR {ocrMs} мс ({LastEngine}), строк {lines.Length}{widened}; окно: {windowMode}";
        return PickWord(lines, point, tolerance, timing);
    }

    /// <summary>Слово под точкой и его предложение по результату OCR; ошибка — если текста нет или это не слово.</summary>
    internal static TextAttempt PickWord(IReadOnlyList<OcrLineBox> lines, ScreenPoint point, double tolerance, string timing)
    {
        var position = OcrLayout.FindWord(lines, point.X, point.Y, tolerance);
        if (position is not { } pos)
            return TextAttempt.Fail($"под курсором нет текста ({timing})");

        var word = lines[pos.Line].Words[pos.Word];
        string normalized = WordNormalizer.Normalize(word.Text);
        if (!WordNormalizer.IsPlausibleWord(normalized))
            return TextAttempt.Fail($"не похоже на слово: «{word.Text}» ({timing})");

        string sentence = OcrLayout.FindSentence(lines, pos, out int offset);
        var hit = sentence.Length > 0
            ? new TextHit(normalized, word.Text, sentence, word.Bounds, TextSourceKind.Ocr, offset)
            : new TextHit(normalized, word.Text, null, word.Bounds, TextSourceKind.Ocr);
        return new TextAttempt(hit, timing);
    }

    public async Task<string> RecognizeAreaAsync(ScreenRect area, CancellationToken ct)
    {
        if (_engine is null) throw new InvalidOperationException("OCR en-US не установлен в Windows");
        // Захват — в пределах одного монитора (того, где центр области).
        var monitor = ScreenGeometry.MonitorFromPoint(new ScreenPoint((int)area.CenterX, (int)area.CenterY));
        var b = monitor.Bounds;
        double x = Math.Max(area.X, b.X), y = Math.Max(area.Y, b.Y);
        var region = new ScreenRect(Math.Round(x), Math.Round(y),
            Math.Round(Math.Min(area.Right, b.Right) - x), Math.Round(Math.Min(area.Bottom, b.Bottom) - y));
        if (region.Width < 4 || region.Height < 4) return "";
        var windowMode = WindowInfo.DescribeMode(new ScreenPoint((int)region.CenterX, (int)region.CenterY));
        var lines = await RecognizeAsync(_engine, region, monitor, windowMode, ct).ConfigureAwait(false);
        return OcrLayout.BuildText(lines);
    }

    /// <summary>Снимок области, OCR и строки со словами в физических координатах экрана.</summary>
    private async Task<OcrLineBox[]> RecognizeAsync(OcrEngine engine, ScreenRect region, MonitorInfo monitor, WindowModeInfo windowMode, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var image = await CaptureAsync(region, monitor, windowMode, ct).ConfigureAwait(false);
        long captureMs = sw.ElapsedMilliseconds;
        string? dumpHeader = _dump is null ? null : DumpHeader(region, monitor, windowMode);
        var pass = await RecognizeWithRetryAsync(engine, image, region, ct, _dump, dumpHeader, _corrector, _log, FallbackEnabled ? Fallback : null).ConfigureAwait(false);
        LastTimings = (captureMs, sw.ElapsedMilliseconds - captureMs);
        LastEngine = pass.Engine;
        return pass.Lines;
    }

    /// <summary>Мало слов — по словарю судить нельзя (кнопки, подписи, имена).</summary>
    internal const int MinWordsForQuality = 4;
    /// <summary>Масштаб второго прохода: у нестандартных (пиксельных) шрифтов без увеличения читается заметно лучше.</summary>
    internal const double RetryScale = 1.0;
    /// <summary>
    /// Доля словарных слов лучшего результата Windows OCR, ниже которой запускается запасной движок. Порог низкий:
    /// запасной проход занимает 0,5–1,5 с, а имена собственные и опечатки в тексте игры сами дают 10–30% несловарных слов.
    /// </summary>
    internal const double FallbackShare = 0.75;

    /// <summary>Результат распознавания снимка и чем он получен (для лога и замеров).</summary>
    internal readonly record struct OcrPass(OcrLineBox[] Lines, string Engine);

    /// <summary>
    /// OCR снимка. Если после исправления по словарю остались несловарные слова («ur-lur» вместо «your»), он
    /// повторяется без увеличения (в 2–4 раза быстрее первого). Если и тогда словарных слов меньше
    /// <see cref="FallbackShare"/> (текст поверх картинок, стилизованный шрифт), снимок читает запасной
    /// <paramref name="fallback"/> (PaddleOCR). Из результатов берётся тот, где «словарные минус несловарные» больше;
    /// при равенстве — более ранний. Имена и опечатки в тексте игры дают лишний проход, но результат не меняют.
    /// Замер на пиксельном шрифте: 90% слов при ×2 против 98% после прохода ×1; на заставке с текстом поверх
    /// иллюстраций Windows OCR читает около половины слов, PaddleOCR — 90%.
    /// </summary>
    internal static async Task<OcrPass> RecognizeWithRetryAsync(OcrEngine engine, BgraImage image, ScreenRect region, CancellationToken ct,
        OcrDebugDump? dump = null, string? dumpHeader = null, OcrCorrector? corrector = null, ILogger? log = null, Func<PaddleOcr?>? fallback = null)
    {
        var best = await RecognizeImageAsync(engine, image, region, ct, dump, dumpHeader, OcrScale, corrector, log).ConfigureAwait(false);
        string engineName = "Windows OCR";
        if (corrector is null) return new OcrPass(best, engineName);
        var (share, total) = corrector.KnownShare(best);
        if (total < MinWordsForQuality || share >= 1) return new OcrPass(best, engineName);

        var again = await RecognizeImageAsync(engine, image, region, ct, dump, dumpHeader + $"\nповторный проход ×{RetryScale}", RetryScale, corrector, log).ConfigureAwait(false);
        var (shareAgain, totalAgain) = corrector.KnownShare(again);
        log?.LogDebug("OCR: словарных слов {Share:P0} из {Total}, повтор ×{Scale}: {ShareAgain:P0} из {TotalAgain}", share, total, RetryScale, shareAgain, totalAgain);
        if (Net(shareAgain, totalAgain) > Net(share, total) + 0.5)
            (best, share, total, engineName) = (again, shareAgain, totalAgain, $"Windows OCR ×{RetryScale}");

        if (fallback is null || share >= FallbackShare) return new OcrPass(best, engineName);
        try
        {
            var raw = await Task.Run<(PaddleOcr Engine, IReadOnlyList<OcrLineBox> Lines)?>(() =>
            {
                var engine = fallback(); // первый вызов грузит модели
                return engine is null ? null : (engine, engine.Recognize(image, region, ct));
            }, ct).ConfigureAwait(false);
            if (raw is not { } done) return new OcrPass(best, engineName);
            var fallbackEngine = done.Engine;
            var viaPaddle = corrector.Correct(done.Lines, out var changes);
            var (sharePaddle, totalPaddle) = corrector.KnownShare(viaPaddle);
            var (detMs, recMs) = fallbackEngine.LastTimings;
            log?.LogDebug("OCR: словарных слов {Share:P0} из {Total}, запасной PaddleOCR (детектор {DetMs} мс, распознавание {RecMs} мс): {SharePaddle:P0} из {TotalPaddle}",
                share, total, detMs, recMs, sharePaddle, totalPaddle);
            string header = dumpHeader + $"\nзапасной PaddleOCR: детектор {detMs} мс, распознавание {recMs} мс"
                + (changes.Count > 0 ? "\nисправлено по словарю: " + string.Join(", ", changes.Select(c => $"{c.From}→{c.To}")) : "");
            if (dump is not null)
                await dump.SaveAsync(image, ImageOps.ToGrayScaled(image, 1), OcrLayout.BuildText(viaPaddle), viaPaddle, header).ConfigureAwait(false);
            if (Net(sharePaddle, totalPaddle) > Net(share, total) + 0.5)
                return new OcrPass([.. viaPaddle], $"PaddleOCR {detMs + recMs} мс");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "Запасной OCR (PaddleOCR) не сработал, остаётся результат Windows OCR");
        }
        return new OcrPass(best, engineName);
    }

    /// <summary>«Словарные минус несловарные»: узкий результат из трёх слов не должен побеждать широкий.</summary>
    private static double Net(double share, int total) => total * (2 * share - 1);

    private string DumpHeader(ScreenRect region, MonitorInfo monitor, WindowModeInfo windowMode) =>
        FormattableString.Invariant($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}\nобласть: {region.X:0},{region.Y:0} {region.Width:0}×{region.Height:0}; монитор {monitor.Bounds.Width:0}×{monitor.Bounds.Height:0}, масштаб {monitor.Scale:0.##}")
        + $"\nокно: {windowMode}\nзахват: {LastCapture}";

    /// <summary>OCR готового снимка области <paramref name="region"/>; слова — в физических координатах экрана.</summary>
    /// <param name="dump">Если задан, снимок и результат сохраняются в отладочную папку с заголовком <paramref name="dumpHeader"/>.</param>
    /// <param name="corrector">Если задан, слова, которых нет в словаре, исправляются по таблице путаниц OCR.</param>
    internal static async Task<OcrLineBox[]> RecognizeImageAsync(OcrEngine engine, BgraImage image, ScreenRect region, CancellationToken ct,
        OcrDebugDump? dump = null, string? dumpHeader = null, double ocrScale = OcrScale, OcrCorrector? corrector = null, ILogger? log = null)
    {
        double scale = Math.Min(ocrScale, OcrEngine.MaxImageDimension / (double)Math.Max(image.Width, image.Height));
        var gray = ImageOps.ToGrayScaled(image, scale);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(gray.Pixels.AsBuffer(), BitmapPixelFormat.Gray8, gray.Width, gray.Height);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);

        // Координаты слов: из увеличенного снимка обратно в физические пиксели экрана.
        double kx = image.Width / (double)gray.Width, ky = image.Height / (double)gray.Height;
        var lines = result.Lines
            .Select(l => new OcrLineBox(OcrLayout.SplitGluedWords(l.Words
                .Select(w => new OcrWordBox(w.Text, new ScreenRect(
                    region.X + w.BoundingRect.X * kx, region.Y + w.BoundingRect.Y * ky,
                    w.BoundingRect.Width * kx, w.BoundingRect.Height * ky)))
                .ToArray())))
            .Where(l => l.Words.Count > 0)
            .ToArray();
        if (corrector is not null)
        {
            lines = [.. corrector.Correct(lines, out var changes)];
            if (changes.Count > 0)
            {
                string list = string.Join(", ", changes.Select(c => $"{c.From}→{c.To}"));
                log?.LogDebug("OCR исправлен по словарю: {Changes}", list);
                dumpHeader += $"\nисправлено по словарю: {list}";
            }
        }
        if (dump is not null)
            await dump.SaveAsync(image, gray, result.Text, lines, dumpHeader ?? "").ConfigureAwait(false);
        return lines;
    }

    private async Task<BgraImage?> TryCaptureWindowAsync(ScreenRect region, CancellationToken ct)
    {
        nint hwnd = WindowInfo.RootWindowFromPoint(new ScreenPoint((int)region.CenterX, (int)region.CenterY));
        if (hwnd == 0) return null;
        try
        {
            var image = await _wgc.CaptureWindowAsync(hwnd, region, ct).ConfigureAwait(false);
            if (!ImageOps.IsBlank(image)) return image;
            _log.LogDebug("WGC по окну {Hwnd}: тоже пустой кадр", hwnd);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "WGC по окну {Hwnd} не удался", hwnd);
        }
        return null;
    }

    public async Task WarmUpAsync(CancellationToken ct)
    {
        if (_engine is null) return;
        var monitor = ScreenGeometry.MonitorFromPoint(new ScreenPoint(0, 0));
        var region = new ScreenRect(monitor.Bounds.X, monitor.Bounds.Y, 64, 64);
        var image = await CaptureAsync(region, monitor, default, ct).ConfigureAwait(false);
        var gray = ImageOps.ToGrayScaled(image, OcrScale);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(gray.Pixels.AsBuffer(), BitmapPixelFormat.Gray8, gray.Width, gray.Height);
        await _engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
        // Модели запасного движка грузятся в фоне: первый запасной проход иначе ждал бы их загрузки.
        if (FallbackEnabled) _ = Task.Run(() => _fallback.Value, CancellationToken.None);
    }

    private async Task<BgraImage> CaptureAsync(ScreenRect region, MonitorInfo monitor, WindowModeInfo windowMode, CancellationToken ct)
    {
        IScreenCapture primary = PreferGdiCapture || !_wgcSupported ? _gdi : _wgc;
        IScreenCapture fallback = primary == _gdi ? _wgc : _gdi;
        if (!_wgcSupported) fallback = _gdi;
        string? skipped = null; // почему основной способ не дал кадр
        if (!_primaryBroken && primary != fallback)
        {
            try
            {
                var image = await primary.CaptureAsync(region, monitor, ct).ConfigureAwait(false);
                if (!ImageOps.IsBlank(image))
                {
                    LastCapture = new CaptureReport(primary.Name, Blank: false, null);
                    return image;
                }
                skipped = $"{primary.Name}: пустой кадр";
                _log.LogDebug("{Capture}: пустой кадр, пробуем {Fallback}", primary.Name, fallback.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _primaryBroken = true; // не тратим время на заведомо неработающий способ
                skipped = $"{primary.Name}: ошибка";
                _log.LogWarning(ex, "{Capture} не работает, переключаемся на {Fallback}", primary.Name, fallback.Name);
            }
        }
        else if (_primaryBroken)
        {
            skipped = $"{primary.Name} отключён после ошибки";
        }

        var result = await fallback.CaptureAsync(region, monitor, ct).ConfigureAwait(false);
        bool blank = ImageOps.IsBlank(result);
        if (blank && _wgcSupported && windowMode.Mode is WindowMode.Borderless or WindowMode.Exclusive)
        {
            // Окно на весь экран, а кадр монитора пуст: пробуем снять само окно (WGC по окну).
            skipped = (skipped is null ? "" : skipped + "; ") + $"{fallback.Name}: пустой кадр";
            var byWindow = await TryCaptureWindowAsync(region, ct).ConfigureAwait(false);
            if (byWindow is not null)
            {
                LastCapture = new CaptureReport("WGC по окну", Blank: false, skipped);
                return byWindow;
            }
        }
        LastCapture = new CaptureReport(fallback.Name, blank, skipped);
        if (blank)
            _log.LogWarning("Кадр пустой: {Capture} (область {X:0},{Y:0} {W:0}×{H:0}){Skipped}",
                fallback.Name, region.X, region.Y, region.Width, region.Height, skipped is null ? "" : "; " + skipped);
        return result;
    }
}
