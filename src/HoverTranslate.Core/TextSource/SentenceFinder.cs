namespace HoverTranslate.Core.TextSource;

/// <summary>Выделение предложения, содержащего слово, по границам «.», «?», «!», «…» и переводам строк.</summary>
public static class SentenceFinder
{
    /// <summary>Предложения длиннее обрезаются вокруг слова (код, списки без точек).</summary>
    public const int MaxSentenceLength = 600;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e", "u.s", "u.k", "a.m", "p.m",
        "no", "fig", "inc", "ltd", "co", "corp", "approx", "dept", "est", "vol", "jan", "feb", "mar", "apr", "jun",
        "jul", "aug", "sep", "sept", "oct", "nov", "dec", "mt", "ft", "lt", "gen", "col", "sgt", "capt", "rev",
    };

    private const string Terminators = ".?!…";
    private const string Closers = "\"')]}”’»";
    private const string Openers = "\"'([{“‘«";

    /// <summary>
    /// Возвращает предложение, содержащее слово, которое начинается с индекса <paramref name="start"/>,
    /// без внешних пробелов. Если границ нет — весь текст (с обрезкой до <see cref="MaxSentenceLength"/>).
    /// </summary>
    public static string Find(string text, int start) => Find(text, start, out _);

    /// <param name="wordOffset">Смещение слова в возвращённом предложении (для поиска фраз вокруг него).</param>
    public static string Find(string text, int start, out int wordOffset)
    {
        ArgumentNullException.ThrowIfNull(text);
        wordOffset = 0;
        if (text.Length == 0) return string.Empty;
        start = Math.Clamp(start, 0, text.Length);

        int sentenceStart = 0;
        for (int i = start - 1; i >= 0; i--)
        {
            int boundaryEnd = BoundaryEndingAt(text, i);
            if (boundaryEnd >= 0 && boundaryEnd <= start)
            {
                sentenceStart = boundaryEnd;
                break;
            }
        }

        // Ищем с начала слова: у слова из OCR точка бывает приклеена («home.»), и это тоже граница.
        int sentenceEnd = text.Length;
        for (int i = Math.Max(start, sentenceStart); i < text.Length; i++)
        {
            int boundaryEnd = BoundaryEndingAt(text, i);
            if (boundaryEnd >= 0)
            {
                sentenceEnd = boundaryEnd;
                break;
            }
        }

        string sentence = text[sentenceStart..sentenceEnd];
        int wordInSentence = start - sentenceStart;
        string collapsed = CollapseWhitespace(sentence, ref wordInSentence);
        return Trim(collapsed, wordInSentence, out wordOffset);
    }

    /// <summary>Все предложения текста по тем же границам, что и <see cref="Find(string, int)"/>; пробелы схлопнуты.</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int next = BoundaryEndingAt(text, i);
            if (next < 0) continue;
            AddSentence(result, text[start..next]);
            start = next;
            i = Math.Max(i, next - 1);
        }
        AddSentence(result, text[start..]);
        return result;
    }

    private static void AddSentence(List<string> list, string sentence)
    {
        int keep = 0;
        string s = CollapseWhitespace(sentence, ref keep).Trim();
        if (s.Length > 0) list.Add(s);
    }

    /// <summary>
    /// Если в позиции <paramref name="i"/> начинается граница предложения, возвращает индекс начала
    /// следующего предложения (после терминатора, закрывающих кавычек и пробелов), иначе -1.
    /// </summary>
    private static int BoundaryEndingAt(string text, int i)
    {
        char c = text[i];
        if (c is '\n' or '\r')
            return SkipSpaces(text, i + 1);
        if (!Terminators.Contains(c))
            return -1;

        int j = i + 1;
        while (j < text.Length && (Terminators.Contains(text[j]) || Closers.Contains(text[j]))) j++; // «?!», «.”»
        if (j < text.Length && !char.IsWhiteSpace(text[j]))
            return -1; // «3.14», «e.g.», «U.S.A»
        int next = SkipSpaces(text, j);
        int runStart = i; // BoundaryEndingAt зовут и для 2-й, 3-й точки многоточия — берём всю серию
        while (runStart > 0 && text[runStart - 1] == '.') runStart--;
        if (next < text.Length && IsEllipsis(text.AsSpan(runStart, j - runStart)) && !Openers.Contains(text[next]))
            return -1; // «mine... What should we do?»: многоточие внутри реплики не конец предложения
        if (next < text.Length)
        {
            char n = text[next];
            // Следующее предложение начинается с заглавной, цифры или открывающей кавычки.
            if (!(char.IsUpper(n) || char.IsDigit(n) || Openers.Contains(n)))
                return -1; // «"What?" she asked.»
        }
        if (c == '.' && IsAbbreviation(text, i))
            return -1;
        return next;
    }

    /// <summary>
    /// Многоточие из трёх точек («...») — пауза внутри реплики, а не конец предложения. Одна-две точки — обычная точка
    /// (OCR нередко читает лишнюю: «notice..»). Один символ «…» остаётся границей («Loading… Please wait»);
    /// «?...» и «!..» — обычный конец вопроса или восклицания.
    /// </summary>
    private static bool IsEllipsis(ReadOnlySpan<char> run)
    {
        if (run.Contains('?') || run.Contains('!') || run.Contains('…')) return false;
        return run.Contains("...", StringComparison.Ordinal);
    }

    private static bool IsAbbreviation(string text, int dot)
    {
        int s = dot;
        while (s > 0 && (char.IsLetter(text[s - 1]) || text[s - 1] == '.')) s--;
        string token = text[s..dot];
        if (token.Length == 0) return false;
        if (token.Length == 1 && char.IsUpper(token[0])) return true; // инициалы: «J. K. Rowling»
        return Abbreviations.Contains(token);
    }

    private static int SkipSpaces(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    private static string CollapseWhitespace(string s, ref int keepIndex)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        int newKeep = keepIndex;
        bool prevSpace = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (i == keepIndex) newKeep = sb.Length;
            if (char.IsWhiteSpace(s[i]))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(s[i]);
                prevSpace = false;
            }
        }
        if (keepIndex >= s.Length) newKeep = sb.Length;
        keepIndex = newKeep;
        return sb.ToString();
    }

    private static string Trim(string sentence, int wordIndex, out int wordOffset)
    {
        int leading = sentence.Length - sentence.TrimStart().Length;
        sentence = sentence.Trim();
        wordIndex = Math.Clamp(wordIndex - leading, 0, sentence.Length);
        wordOffset = wordIndex;
        if (sentence.Length <= MaxSentenceLength) return sentence;

        // Окно вокруг слова, обрезанное по пробелам.
        int half = MaxSentenceLength / 2;
        int from = Math.Clamp(wordIndex - half, 0, sentence.Length - MaxSentenceLength);
        int to = from + MaxSentenceLength;
        if (from > 0)
        {
            int sp = sentence.IndexOf(' ', from);
            if (sp >= 0 && sp < wordIndex) from = sp + 1;
        }
        if (to < sentence.Length)
        {
            int sp = sentence.LastIndexOf(' ', to);
            if (sp > wordIndex) to = sp;
        }
        string window = sentence[from..to];
        int lead = window.Length - window.TrimStart().Length;
        string prefix = from > 0 ? "…" : "";
        wordOffset = Math.Max(0, wordIndex - from - lead) + prefix.Length;
        return prefix + window.Trim() + (to < sentence.Length ? "…" : "");
    }
}
