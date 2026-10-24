using System.Diagnostics;
using HoverTranslate.Core.Models;

namespace HoverTranslate.Core.TextSource;

/// <summary>Какие источники текста пробовать.</summary>
public enum TextSourceMode
{
    /// <summary>UIA, при неудаче — OCR.</summary>
    Auto,
    UiaOnly,
    OcrOnly,
}

public interface ITextLookupService
{
    /// <param name="startTimestamp">Момент нажатия (Stopwatch.GetTimestamp) — время считается от него.</param>
    Task<CaptureOutcome> CaptureAsync(ScreenPoint cursor, long startTimestamp, TextSourceMode mode, CancellationToken ct);
}

/// <summary>Слово и предложение под курсором: UIA, при неудаче — OCR.</summary>
public sealed class TextLookupService(IUiaTextSource uia, IOcrTextSource ocr) : ITextLookupService
{
    public async Task<CaptureOutcome> CaptureAsync(ScreenPoint cursor, long startTimestamp, TextSourceMode mode, CancellationToken ct)
    {
        string app = WindowInfo.ProcessNameFromPoint(cursor);
        string? uiaNote = null;

        if (mode != TextSourceMode.OcrOnly)
        {
            var attempt = await uia.TryGetAsync(cursor, ct).ConfigureAwait(false);
            if (attempt.Hit is not null)
                return Done(attempt.Hit, null, null);
            uiaNote = attempt.Note;
            if (mode == TextSourceMode.UiaOnly)
                return Done(null, uiaNote, null);
        }

        var ocrAttempt = await ocr.TryGetAsync(cursor, ct).ConfigureAwait(false);
        string? note = uiaNote is null ? ocrAttempt.Note : $"UIA: {uiaNote}; OCR: {ocrAttempt.Note}";
        return Done(ocrAttempt.Hit, note, ocrAttempt.Hit is null ? "Слово под курсором не найдено" : null);

        CaptureOutcome Done(TextHit? hit, string? n, string? error) =>
            new(DateTimeOffset.Now, cursor, app, hit, Stopwatch.GetElapsedTime(startTimestamp), n, error);
    }
}
