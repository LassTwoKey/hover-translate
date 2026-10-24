using HoverTranslate.Core.Models;
using HoverTranslate.Core.TextSource;

namespace HoverTranslate.Core.Tests;

/// <summary>Построение искусственных результатов OCR: моноширинная «вёрстка» строк текста.</summary>
internal static class OcrFixtures
{
    public const double CharWidth = 10, LineHeight = 20, LineSpacing = 8;

    /// <summary>Строка из слов, начиная с (x, y); каждый символ шириной <see cref="CharWidth"/>.</summary>
    public static OcrLineBox Line(double x, double y, string text, double charWidth = CharWidth, double height = LineHeight)
    {
        var words = new List<OcrWordBox>();
        double cx = x;
        foreach (string w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(new OcrWordBox(w, new ScreenRect(cx, y, w.Length * charWidth, height)));
            cx += (w.Length + 1) * charWidth;
        }
        return new OcrLineBox(words);
    }

    /// <summary>Абзац: строки одна под другой с обычным межстрочным интервалом.</summary>
    public static List<OcrLineBox> Paragraph(double x, double y, params string[] lines) =>
        lines.Select((t, i) => Line(x, y + i * (LineHeight + LineSpacing), t)).ToList();

    /// <summary>Центр слова <paramref name="word"/> (первое вхождение) — туда «наводим курсор».</summary>
    public static (double X, double Y) CenterOf(IReadOnlyList<OcrLineBox> lines, string word)
    {
        foreach (var line in lines)
            foreach (var w in line.Words)
                if (w.Text == word)
                    return (w.Bounds.CenterX, w.Bounds.CenterY);
        throw new ArgumentException($"Слово {word} не найдено");
    }

    public static string WordAt(IReadOnlyList<OcrLineBox> lines, WordPosition p) => lines[p.Line].Words[p.Word].Text;
}
