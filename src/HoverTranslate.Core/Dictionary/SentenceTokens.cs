using HoverTranslate.Core.TextSource;

namespace HoverTranslate.Core.Dictionary;

/// <summary>Слово предложения: нормализованный текст, позиция и есть ли перед ним пунктуация.</summary>
/// <param name="BreakBefore">Между предыдущим словом и этим не только пробелы (запятая, тире, скобка) —
/// фраза через такую границу не ищется.</param>
public readonly record struct Token(string Text, int Start, int Length, bool BreakBefore)
{
    public string Lower => Text.ToLowerInvariant();
    public int End => Start + Length;
}

/// <summary>Разбиение предложения на слова и поиск слова под курсором среди них.</summary>
public static class SentenceTokens
{
    public static IReadOnlyList<Token> Tokenize(string? sentence)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(sentence)) return tokens;
        int i = 0, prevEnd = 0;
        while (i < sentence.Length)
        {
            if (!char.IsLetterOrDigit(sentence[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < sentence.Length && (char.IsLetterOrDigit(sentence[i]) || IsInnerJoiner(sentence, i))) i++;
            string word = WordNormalizer.Normalize(sentence[start..i]);
            bool breakBefore = tokens.Count > 0 && !IsWhitespace(sentence, prevEnd, start);
            if (word.Length > 0)
                tokens.Add(new Token(word, start, i - start, breakBefore));
            prevEnd = i;
        }
        return tokens;
    }

    /// <summary>
    /// Индекс слова под курсором: токен, содержащий <paramref name="offset"/>; если смещение неизвестно
    /// или не попадает — первое совпадение по тексту. -1, если слова в предложении нет.
    /// </summary>
    public static int IndexOf(IReadOnlyList<Token> tokens, string word, int offset)
    {
        if (offset >= 0)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                // Диапазон слова UIA бывает с открывающей кавычкой: «“Hello» — допускаем пару символов до начала.
                if (offset >= tokens[i].Start - 2 && offset < tokens[i].End && Same(tokens[i].Text, word))
                    return i;
            }
        }
        for (int i = 0; i < tokens.Count; i++)
        {
            if (Same(tokens[i].Text, word)) return i;
        }
        return -1;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Апостроф или дефис между буквами — часть слова («don't», «well-known»).</summary>
    private static bool IsInnerJoiner(string s, int i) =>
        s[i] is '\'' or '’' or '-' or '‐' or '‑' && i > 0 && i + 1 < s.Length
        && char.IsLetterOrDigit(s[i - 1]) && char.IsLetterOrDigit(s[i + 1]);

    private static bool IsWhitespace(string s, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            if (!char.IsWhiteSpace(s[i])) return false;
        }
        return true;
    }
}
