using System.Text;
using HoverTranslate.Core.TextSource;

namespace HoverTranslate.Core.Translation;

/// <summary>
/// Длинный текст -> абзацы (по переводам строк) -> предложения: Marian переводит предложения заметно лучше
/// и быстрее, чем абзац целиком, и каждое предложение отдельно попадает в кэш. Слишком длинные
/// «предложения» без точек (списки, интерфейс игры) режутся по словам.
/// </summary>
public static class TextSegmenter
{
    /// <summary>Предел слов в одном куске для модели: дальше качество greedy-перевода падает.</summary>
    public const int MaxWordsPerChunk = 60;

    /// <summary>Абзацы, в каждом — предложения (или куски длинного предложения).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Split(string text)
    {
        var paragraphs = new List<IReadOnlyList<string>>();
        if (string.IsNullOrWhiteSpace(text)) return paragraphs;
        foreach (string paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(paragraph)) continue;
            var chunks = new List<string>();
            foreach (string sentence in SentenceFinder.Split(paragraph))
                chunks.AddRange(ChunkByWords(sentence));
            if (chunks.Count > 0) paragraphs.Add(chunks);
        }
        return paragraphs;
    }

    /// <summary>Склейка переводов: предложения через пробел, абзацы через перевод строки.</summary>
    public static string Join(IEnumerable<IEnumerable<string>> paragraphs)
    {
        var sb = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            string line = string.Join(' ', paragraph.Where(s => s.Length > 0));
            if (line.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }

    /// <summary>Нормализованная строка для ключа кэша: схлопнутые пробелы, без внешних пробелов.</summary>
    public static string Normalize(string sentence) =>
        string.Join(' ', sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static IEnumerable<string> ChunkByWords(string sentence)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= MaxWordsPerChunk)
        {
            yield return sentence;
            yield break;
        }
        // Режем поровну, чтобы не оставлять хвост из пары слов.
        int parts = (words.Length + MaxWordsPerChunk - 1) / MaxWordsPerChunk;
        int size = (words.Length + parts - 1) / parts;
        for (int i = 0; i < words.Length; i += size)
            yield return string.Join(' ', words.Skip(i).Take(size));
    }
}
