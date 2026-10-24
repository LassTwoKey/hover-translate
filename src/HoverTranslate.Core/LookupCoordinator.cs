using HoverTranslate.Core.Dictionary;
using HoverTranslate.Core.Hotkeys;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Native;
using HoverTranslate.Core.Storage;
using HoverTranslate.Core.TextSource;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.Core;

/// <summary>
/// Связывает горячие клавиши с поиском: регистрирует сочетания из настроек, на каждое нажатие
/// запускает поиск текста и словаря в фоне (повторное нажатие отменяет предыдущий) и сообщает итог.
/// Вторая клавиша (Alt+W) — выделение области мышью, OCR всего текста в ней и <see cref="AreaCompleted"/>.
/// Ни одно исключение не выходит за пределы обработчика: ошибка -> лог + Error в итоге.
/// </summary>
public sealed class LookupCoordinator : IDisposable
{
    private readonly IHotkeyService _hotkeys;
    private readonly ITextLookupService _lookup;
    private readonly IDictionaryService _dictionary;
    private readonly IOcrTextSource _ocr;
    private readonly AreaSelector _selector;
    private readonly MouseButtonHotkeys _mouse;
    private readonly ISettingsStore _settings;
    private readonly ILogger<LookupCoordinator> _log;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;

    /// <summary>Итог обработки нажатия. Вызывается в фоновом потоке.</summary>
    public event EventHandler<LookupOutcome>? Completed;

    /// <summary>
    /// Начинается выделение области — вызывается на потоке служебного окна до показа затемнения.
    /// Карточка должна закрыться и снять свои хуки ввода (одновременно активен один набор хуков).
    /// </summary>
    public event EventHandler? AreaSelectionStarting;

    /// <summary>Текст области распознан (или нет). Вызывается в фоновом потоке.</summary>
    public event EventHandler<AreaOutcome>? AreaCompleted;

    /// <summary>
    /// Начался поиск слова/предложения под курсором (до результата) — карточка может сразу показать загрузку.
    /// Результат придёт в <see cref="Completed"/> или <see cref="AreaCompleted"/>, если поиск не отменят. Фоновый поток.
    /// </summary>
    public event EventHandler<ScreenPoint>? LookupStarted;

    /// <summary>Область выбрана, начинается распознавание (до <see cref="AreaCompleted"/>). Фоновый поток.</summary>
    public event EventHandler<ScreenRect>? AreaRecognizing;

    /// <summary>Горячую клавишу не удалось зарегистрировать (занята другой программой).</summary>
    public event EventHandler<Hotkey>? RegistrationFailed;

    /// <summary>Действующее сочетание изменилось (смена в настройках).</summary>
    public event EventHandler? HotkeyChanged;

    /// <summary>
    /// Вызывается на потоке служебного окна до поиска; true — нажатие обработано (например, popup
    /// закрыт повторным нажатием) и искать не нужно. Должен работать быстро.
    /// </summary>
    public Func<ScreenPoint, bool>? PressInterceptor { get; set; }

    public TextSourceMode Mode { get; set; } = TextSourceMode.Auto;

    public LookupCoordinator(IHotkeyService hotkeys, ITextLookupService lookup, IDictionaryService dictionary,
        IOcrTextSource ocr, AreaSelector selector, MouseButtonHotkeys mouse, ISettingsStore settings, ILogger<LookupCoordinator> log)
    {
        _hotkeys = hotkeys;
        _lookup = lookup;
        _dictionary = dictionary;
        _ocr = ocr;
        _selector = selector;
        _mouse = mouse;
        _settings = settings;
        _log = log;
        _hotkeys.Pressed += OnPressed;
        _mouse.Pressed += OnPressed;
        _settings.Changed += OnSettingChanged;
    }

    /// <summary>Сочетание из настроек (по умолчанию Alt+Q).</summary>
    public Hotkey LookupHotkey =>
        Hotkey.TryParse(_settings.Get(SettingKeys.LookupHotkey), out var h) ? h.Value : Hotkey.DefaultLookup;

    /// <summary>Действительно зарегистрированное сочетание или null, если регистрация не удалась.</summary>
    public Hotkey? RegisteredLookupHotkey => _hotkeys.GetRegistered(HotkeyAction.Lookup);

    /// <summary>Сочетание перевода области из настроек (по умолчанию Alt+W).</summary>
    public Hotkey AreaHotkey =>
        Hotkey.TryParse(_settings.Get(SettingKeys.AreaHotkey), out var h) ? h.Value : Hotkey.DefaultAreaTranslate;

    public Hotkey? RegisteredAreaHotkey => _hotkeys.GetRegistered(HotkeyAction.AreaTranslate);

    /// <summary>Регистрирует горячие клавиши из настроек.</summary>
    public void Start()
    {
        RegisterHotkey(HotkeyAction.Lookup, LookupHotkey);
        RegisterHotkey(HotkeyAction.AreaTranslate, AreaHotkey);
        ApplyMouseBindings();
    }

    /// <summary>Действие на боковой кнопке мыши из настроек; по умолчанию 4 — предложение, 5 — слово.</summary>
    public HotkeyAction? MouseBinding(MouseButton button)
    {
        string? value = _settings.Get(button == MouseButton.X1 ? SettingKeys.MouseX1 : SettingKeys.MouseX2);
        return MouseButtonHotkeys.ParseAction(value ?? MouseButtonHotkeys.DefaultActionValue(button));
    }

    private void ApplyMouseBindings()
    {
        _mouse.SetBindings(MouseBinding(MouseButton.X1), MouseBinding(MouseButton.X2));
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RegisterHotkey(HotkeyAction action, Hotkey hotkey)
    {
        if (!_hotkeys.Register(action, hotkey))
            RegistrationFailed?.Invoke(this, hotkey);
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingChanged(object? sender, string key)
    {
        // смена сочетания без перезапуска
        if (key == SettingKeys.LookupHotkey) RegisterHotkey(HotkeyAction.Lookup, LookupHotkey);
        else if (key == SettingKeys.AreaHotkey) RegisterHotkey(HotkeyAction.AreaTranslate, AreaHotkey);
        else if (key is SettingKeys.MouseX1 or SettingKeys.MouseX2) ApplyMouseBindings();
    }

    private void OnPressed(object? sender, HotkeyPressedEventArgs e)
    {
        if (e.Action == HotkeyAction.AreaTranslate)
        {
            OnAreaPressed();
            return;
        }
        if (e.Action is not (HotkeyAction.Lookup or HotkeyAction.Sentence)) return;
        if (_selector.IsActive) _selector.Cancel();
        try
        {
            if (PressInterceptor?.Invoke(e.Cursor) == true) return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка в обработчике повторного нажатия");
        }

        var cts = Restart();
        RaiseSafe(() => LookupStarted?.Invoke(this, e.Cursor));
        // Поток служебного окна отпускаем сразу: OCR и UIA — в пуле потоков.
        _ = e.Action == HotkeyAction.Sentence
            ? Task.Run(() => RunSentenceAsync(e, cts.Token), CancellationToken.None)
            : Task.Run(() => RunAsync(e, cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Только перевод предложения под курсором: тот же захват текста, что у слова, но карточка — как у области
    /// (перевод сверху, оригинал ниже). Если предложения нет, переводится само слово.
    /// </summary>
    private async Task RunSentenceAsync(HotkeyPressedEventArgs e, CancellationToken ct)
    {
        try
        {
            var capture = await _lookup.CaptureAsync(e.Cursor, e.Timestamp, Mode, ct).ConfigureAwait(false);
            var anchor = capture.Hit?.WordBounds ?? new ScreenRect(e.Cursor.X - 1, e.Cursor.Y - 1, 2, 2);
            string? text = capture.Hit is { } hit ? hit.Sentence ?? hit.Word : null;
            // Без предложения «оригиналом» служит само слово — подсвечивать в нём нечего.
            bool hasSentence = capture.Hit?.Sentence is not null;
            var outcome = new AreaOutcome(DateTimeOffset.Now, anchor, text, capture.Elapsed,
                text is null ? capture.Error ?? "Текст под курсором не найден" : null,
                hasSentence ? capture.Hit!.Word : null, hasSentence ? capture.Hit!.WordOffset : -1);
            _log.LogInformation("Предложение под курсором за {Ms:0} мс в {App}: {Text}{Error}", capture.Elapsed.TotalMilliseconds,
                capture.SourceApp, text ?? "—", outcome.Error is null ? "" : " — " + outcome.Error);
            if (ct.IsCancellationRequested) return; // карточку закрыли, пока шёл поиск
            AreaCompleted?.Invoke(this, outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // новое нажатие
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка при переводе предложения под курсором {Cursor}", e.Cursor);
            if (ct.IsCancellationRequested) return;
            var anchor = new ScreenRect(e.Cursor.X - 1, e.Cursor.Y - 1, 2, 2);
            RaiseSafe(() => AreaCompleted?.Invoke(this, new AreaOutcome(DateTimeOffset.Now, anchor, null,
                System.Diagnostics.Stopwatch.GetElapsedTime(e.Timestamp), "Не удалось получить текст: " + ex.Message)));
        }
    }

    /// <summary>Отменяет поиск или распознавание, которые ещё идут (карточка с загрузкой закрыта): результата не будет.</summary>
    public void CancelPending()
    {
        lock (_gate) _current?.Cancel();
    }

    private void RaiseSafe(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка в обработчике начала поиска");
        }
    }

    /// <summary>Отменяет текущую операцию (поиск слова или области) и начинает новую.</summary>
    private CancellationTokenSource Restart()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            return _current = new CancellationTokenSource();
        }
    }

    /// <summary>Alt+W: повторное нажатие во время выделения — отмена, иначе — затемнение и выделение области.</summary>
    private void OnAreaPressed()
    {
        if (_selector.IsActive)
        {
            _selector.Cancel();
            return;
        }
        var cts = Restart();
        try
        {
            AreaSelectionStarting?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка в обработчике начала выделения");
        }
        _ = RunAreaAsync(cts.Token);
    }

    private async Task RunAreaAsync(CancellationToken ct)
    {
        try
        {
            var area = await _selector.SelectAsync().ConfigureAwait(false);
            if (area is not { } rect || ct.IsCancellationRequested) return;

            RaiseSafe(() => AreaRecognizing?.Invoke(this, rect));
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            AreaOutcome outcome;
            try
            {
                if (!_ocr.IsAvailable) throw new InvalidOperationException("OCR en-US не установлен в Windows");
                string text = await _ocr.RecognizeAreaAsync(rect, ct).ConfigureAwait(false);
                outcome = new AreaOutcome(DateTimeOffset.Now, rect, text.Length > 0 ? text : null,
                    System.Diagnostics.Stopwatch.GetElapsedTime(start), text.Length > 0 ? null : "В выделенной области не найден текст");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка распознавания области {Area}", rect);
                outcome = new AreaOutcome(DateTimeOffset.Now, rect, null, System.Diagnostics.Stopwatch.GetElapsedTime(start),
                    "Не удалось распознать текст: " + ex.Message);
            }
            _log.LogInformation("Область {W:0}×{H:0} @ {X:0},{Y:0}: {Chars} символов за {Ms:0} мс{Error}",
                rect.Width, rect.Height, rect.X, rect.Y, outcome.Text?.Length ?? 0, outcome.Elapsed.TotalMilliseconds,
                outcome.Error is null ? "" : " — " + outcome.Error);
            if (ct.IsCancellationRequested) return; // карточку закрыли, пока шло распознавание
            AreaCompleted?.Invoke(this, outcome);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка при переводе области");
        }
    }

    private async Task RunAsync(HotkeyPressedEventArgs e, CancellationToken ct)
    {
        CaptureOutcome capture;
        LookupResult? result = null;
        try
        {
            capture = await _lookup.CaptureAsync(e.Cursor, e.Timestamp, Mode, ct).ConfigureAwait(false);
            if (capture.Hit is { } hit)
            {
                var matches = _dictionary.Lookup(hit.Word, hit.Sentence, hit.WordOffset);
                var primary = matches.Count > 0 ? matches[0] : null;
                result = new LookupResult(hit.Word, primary?.Lemma ?? hit.Word.ToLowerInvariant(), hit.Sentence, null,
                    primary?.Entry, capture.SourceApp, hit.Source)
                {
                    Entries = matches.Select(m => m.Entry).ToList(),
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // пользователь нажал ещё раз — результат уже не нужен
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка при поиске слова под курсором {Cursor}", e.Cursor);
            capture = new CaptureOutcome(DateTimeOffset.Now, e.Cursor, "?", null,
                System.Diagnostics.Stopwatch.GetElapsedTime(e.Timestamp), null, "Не удалось получить текст: " + ex.Message);
        }

        Log(capture, result);
        if (ct.IsCancellationRequested) return; // карточку закрыли, пока шёл поиск
        try
        {
            Completed?.Invoke(this, new LookupOutcome(capture, result));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка в обработчике результата");
        }
    }

    private void Log(CaptureOutcome o, LookupResult? r)
    {
        if (o.Hit is { } hit)
            _log.LogInformation("[{Source}] «{Word}» -> {Lemmas}{Missing} за {Ms:0} мс в {App} @ {X},{Y} | {Sentence}{Note}",
                hit.Source, hit.Word, r is null ? null : string.Join(" | ", r.Entries.Select(e => e.Lemma)),
                r?.Entry is null ? " (нет в словаре)" : "", o.Elapsed.TotalMilliseconds,
                o.SourceApp, o.Cursor.X, o.Cursor.Y, hit.Sentence ?? "—", o.UiaNote is null ? "" : $" ({o.UiaNote})");
        else
            _log.LogWarning("Слово не найдено за {Ms:0} мс в {App} @ {X},{Y}: {Error} ({Note})",
                o.Elapsed.TotalMilliseconds, o.SourceApp, o.Cursor.X, o.Cursor.Y, o.Error, o.UiaNote);
    }

    public void Dispose()
    {
        _hotkeys.Pressed -= OnPressed;
        _mouse.Pressed -= OnPressed;
        _settings.Changed -= OnSettingChanged;
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
        }
    }
}
