using System.Runtime.InteropServices;
using HoverTranslate.Core.Models;
using Interop.UIAutomationClient;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.Core.TextSource;

/// <summary>Итог попытки получить текст: слово или причина отказа (для лога и отладочного окна).</summary>
public sealed record TextAttempt(TextHit? Hit, string? Note)
{
    public static TextAttempt Fail(string note) => new(null, note);
}

public interface IUiaTextSource
{
    Task<TextAttempt> TryGetAsync(ScreenPoint point, CancellationToken ct);

    /// <summary>Создаёт UIA-клиент заранее, чтобы первое нажатие не ждало инициализации COM.</summary>
    void WarmUp();
}

/// <summary>
/// Текст под курсором через UI Automation (TextPattern.RangeFromPoint) — без OCR, мгновенно и точно
/// там, где приложение публикует текст (браузеры, редакторы, Office, PDF в Edge).
/// </summary>
public sealed class UiaTextSource : IUiaTextSource
{
    private const int UIA_TextPatternId = 10014;
    private const int MaxAncestorDepth = 10;
    private const int MaxParagraphChars = 4000;
    /// <summary>Допуск при проверке, что курсор действительно над словом (в физических px).</summary>
    private const double HitSlack = 3;

    private readonly ILogger<UiaTextSource> _log;
    private readonly Lazy<IUIAutomation> _uia;

    /// <summary>Сколько ждать UIA, прежде чем перейти к OCR. Вызов COM не прерывается — он просто бросается.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(150);

    public UiaTextSource(ILogger<UiaTextSource> log)
    {
        _log = log;
        _uia = new Lazy<IUIAutomation>(CreateAutomation, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private static IUIAutomation CreateAutomation()
    {
        var uia = new CUIAutomation8();
        if (uia is IUIAutomation2 uia2)
        {
            // Зависший провайдер не должен навсегда занимать поток пула.
            uia2.ConnectionTimeout = 1000;
            uia2.TransactionTimeout = 1000;
        }
        return uia;
    }

    public void WarmUp() => _ = _uia.Value;

    public async Task<TextAttempt> TryGetAsync(ScreenPoint point, CancellationToken ct)
    {
        // Потоки пула — MTA, что и нужно UIA-клиенту; UI-поток не блокируется.
        var task = Task.Run(() => Probe(point), CancellationToken.None);
        try
        {
            return await task.WaitAsync(Timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Брошенная задача доработает в фоне; её исключения не должны всплыть как необработанные.
            _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return TextAttempt.Fail($"таймаут UIA ({Timeout.TotalMilliseconds:0} мс)");
        }
    }

    internal TextAttempt Probe(ScreenPoint point)
    {
        try
        {
            var uia = _uia.Value;
            var pt = new tagPOINT { x = point.X, y = point.Y };
            var element = uia.ElementFromPoint(pt);
            var pattern = FindTextPattern(uia, element);
            if (pattern is null) return TextAttempt.Fail("нет TextPattern");

            var word = pattern.RangeFromPoint(pt);
            if (word is null) return TextAttempt.Fail("RangeFromPoint вернул null");
            word.ExpandToEnclosingUnit(TextUnit.TextUnit_Word);
            string raw = word.GetText(64) ?? "";
            string normalized = WordNormalizer.Normalize(raw);
            if (!WordNormalizer.IsPlausibleWord(normalized))
                return TextAttempt.Fail($"не похоже на слово: «{Shorten(raw)}»");

            // RangeFromPoint возвращает ближайший текст, даже если курсор над пустым местом.
            var bounds = GetBounds(word);
            if (bounds is not { } b || b.DistanceTo(point.X, point.Y) > HitSlack)
                return TextAttempt.Fail($"курсор не над словом «{normalized}»");

            string? sentence = GetSentence(word, out int offset);
            return new TextAttempt(new TextHit(normalized, raw.Trim(), sentence, b, TextSourceKind.Uia, sentence is null ? -1 : offset), null);
        }
        catch (COMException ex)
        {
            return TextAttempt.Fail($"COM 0x{ex.HResult:X8}");
        }
        catch (Exception ex) when (ex is InvalidCastException or ArgumentException or NotImplementedException)
        {
            _log.LogDebug(ex, "UIA: ошибка провайдера");
            return TextAttempt.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IUIAutomationTextPattern? FindTextPattern(IUIAutomation uia, IUIAutomationElement? element)
    {
        // TextPattern обычно у документа/поля ввода, а под курсором — дочерний текстовый узел.
        var walker = uia.RawViewWalker;
        for (int depth = 0; element is not null && depth < MaxAncestorDepth; depth++)
        {
            if (element.GetCurrentPattern(UIA_TextPatternId) is IUIAutomationTextPattern pattern)
                return pattern;
            element = walker.GetParentElement(element);
        }
        return null;
    }

    private static ScreenRect? GetBounds(IUIAutomationTextRange range)
    {
        double[]? rects = range.GetBoundingRectangles();
        if (rects is null || rects.Length < 4) return null;
        ScreenRect union = default;
        for (int i = 0; i + 3 < rects.Length; i += 4)
            union = union.Union(new ScreenRect(rects[i], rects[i + 1], rects[i + 2], rects[i + 3]));
        return union.IsEmpty ? null : union;
    }

    /// <summary>Предложение: абзац (или строка, если абзац огромный) + поиск границ вокруг слова.</summary>
    private static string? GetSentence(IUIAutomationTextRange word, out int wordOffset)
    {
        wordOffset = -1;
        foreach (var unit in new[] { TextUnit.TextUnit_Paragraph, TextUnit.TextUnit_Line })
        {
            var block = word.Clone();
            block.ExpandToEnclosingUnit(unit);
            string text = block.GetText(MaxParagraphChars + 1) ?? "";
            if (text.Length > MaxParagraphChars || string.IsNullOrWhiteSpace(text)) continue;

            // Смещение слова в абзаце: длина текста от начала абзаца до начала слова.
            var prefix = block.Clone();
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, word,
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            int offset = (prefix.GetText(MaxParagraphChars + 1) ?? "").Length;
            if (offset > text.Length) continue;
            string sentence = SentenceFinder.Find(text, offset, out wordOffset);
            return sentence.Length > 0 ? sentence : null;
        }
        return null;
    }

    private static string Shorten(string s) => s.Length <= 30 ? s : s[..30] + "…";
}
