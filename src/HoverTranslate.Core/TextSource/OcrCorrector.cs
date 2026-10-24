using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace HoverTranslate.Core.TextSource;

/// <summary>
/// Исправляет типичные ошибки Windows OCR по словарю. Нестандартные (пиксельные) шрифты читаются системно неверно:
/// «y» как «u»/«g» («historu», «holidau», «gour»), «v» как «u», «e» как «a» («Gardan»), «v» как «w» («vaguelw»), «l» как «I».
/// Правится только слово, которого нет в словаре, и только если замена букв из таблицы путаниц даёт словарное слово.
/// Длина слова не меняется, поэтому рамки слов и смещение слова в предложении остаются верными.
/// </summary>
public sealed partial class OcrCorrector(Func<string, double?> zipf)
{
    /// <summary>Что прочитал OCR -> чем это могло быть на самом деле (регистр букв переносится на замену).</summary>
    private static readonly Dictionary<char, string> Confusions = new()
    {
        ['u'] = "yv", ['g'] = "yq", ['q'] = "g", ['a'] = "e", ['e'] = "a", ['w'] = "v", ['v'] = "w", ['y'] = "g",
        ['I'] = "l", ['l'] = "I", ['0'] = "o", ['1'] = "l",
    };

    private const int MaxPositions = 7;       // позиций с возможной путаницей в слове: больше — слово не трогаем
    private const int MaxChanges = 3;         // сколько букв можно заменить в одном слове («weguelu» -> «vaguely»)
    private const double MinZipfOneChar = 2.5; // порог частотности исправленного слова при замене одной буквы
    private const double MinZipfMoreChars = 2.2; // и нескольких: чем больше замен, тем строже

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>Строки с исправленными словами; <paramref name="changes"/> — пары «было → стало» для лога.</summary>
    public IReadOnlyList<OcrLineBox> Correct(IReadOnlyList<OcrLineBox> lines, out IReadOnlyList<(string From, string To)> changes)
    {
        var done = new List<(string, string)>();
        var result = new List<OcrLineBox>(lines.Count);
        foreach (var line in lines)
        {
            List<OcrWordBox>? words = null;
            for (int i = 0; i < line.Words.Count; i++)
            {
                string fixedText = CorrectToken(line.Words[i].Text);
                if (fixedText == line.Words[i].Text)
                {
                    words?.Add(line.Words[i]);
                    continue;
                }
                words ??= [.. line.Words.Take(i)];
                words.Add(line.Words[i] with { Text = fixedText });
                done.Add((line.Words[i].Text, fixedText));
            }
            result.Add(words is null ? line : new OcrLineBox(words));
        }
        changes = done;
        return result;
    }

    /// <summary>
    /// Доля словарных слов среди всех слов с буквами (после исправления) и их число; обрывки из одной буквы и
    /// «слова» с посторонними знаками считаются несловарными. По ней видно, что OCR «не прочитал» текст:
    /// у хорошо распознанной реплики доля близка к 1, у мусора — низкая.
    /// </summary>
    public (double Share, int Total) KnownShare(IReadOnlyList<OcrLineBox> lines)
    {
        int total = 0, known = 0;
        foreach (var word in lines.SelectMany(l => l.Words))
        {
            string core = WordNormalizer.Normalize(word.Text);
            if (!core.Any(char.IsLetter) || Ordinal().IsMatch(core)) continue;
            total++;
            // Одна буква — слово, только если это «I» или «a»: остальные («i», «s», «v») — обрывки слов.
            if (core.Length == 1)
            {
                if (core is "I" or "a" or "A") known++;
                continue;
            }
            // «l.deiqht», «SS.O», «H2O»: буквы вперемешку со знаками — не слово, в словаре их не ищем.
            if (core.All(c => char.IsLetter(c) || c is '\'' or '-') && IsKnownToken(core)) known++;
        }
        return (total == 0 ? 1 : (double)known / total, total);
    }

    [GeneratedRegex(@"^\d+(st|nd|rd|th)$", RegexOptions.IgnoreCase)]
    private static partial Regex Ordinal();

    /// <summary>
    /// Слово из словаря. С дефисом — только если все части из трёх и более букв словарные: «ur-lur», «ur-II-Ir» —
    /// склеенный OCR-мусор вместо «your», а «well-known» и «broad-shouldered» — слова. Целиком такой токен словарь
    /// не проверяет: для «ur-II-Ir» он находит «Ir» (лемматизатор берёт последнюю часть составного слова).
    /// </summary>
    private bool IsKnownToken(string core)
    {
        if (core.Contains('-'))
            return core.Split('-').All(p => p.Length >= 3 && zipf(p) is not null);
        if (zipf(core) is not null) return true;
        return core.Split('\'').All(p => p.Length < 2 || zipf(p) is not null); // «Dana's»: «s» не проверяем
    }

    /// <summary>Слово как распознано (с пунктуацией по краям) -> исправленное; без исправления возвращается то же.</summary>
    public string CorrectToken(string token)
    {
        int start = 0, end = token.Length;
        while (start < end && !char.IsLetterOrDigit(token[start])) start++;
        while (end > start && !char.IsLetterOrDigit(token[end - 1])) end--;
        if (start == end) return token;

        string core = token[start..end];
        string fixedCore = _cache.GetOrAdd(core, CorrectCore);
        return fixedCore == core ? token : token[..start] + fixedCore + token[end..];
    }

    private string CorrectCore(string core)
    {
        if (_cache.Count > 20_000) _cache.Clear();
        if (core.Contains('\'') || core.Contains('’')) return core; // «don't», «Dana's»: части слишком короткие для проверки
        if (core.Contains('-'))
            return string.Join('-', core.Split('-').Select(CorrectPart));
        return CorrectPart(core);
    }

    private string CorrectPart(string part)
    {
        // Отдельная «R» в тексте игры — это «A» («R long walk»): в этом шрифте «A» читается как «R».
        if (part == "R") return "A";
        if (part.Length < 3 || !part.Any(char.IsLetter) || part.All(char.IsDigit)) return part;

        // «gaRden» -> «garden»: смешанный регистр внутри слова (не «Capitalized» и не «CAPS»). Словарь ищет без учёта
        // регистра и «gaRden» нашёл бы как есть, поэтому проверяется до него.
        string lower = part.ToLowerInvariant();
        bool mixedCase = part.Skip(1).Any(char.IsUpper) && part.Any(char.IsLower);
        if (mixedCase && zipf(lower) is not null)
            return char.IsUpper(part[0]) ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower;
        if (zipf(part) is not null) return part;

        var positions = new List<int>();
        for (int i = 0; i < part.Length; i++)
            if (Replacements(part[i]) is not null) positions.Add(i);
        if (positions.Count == 0 || positions.Count > MaxPositions) return part;

        string? best = null;
        double bestScore = double.MinValue;
        foreach (var (candidate, changed) in Candidates(part, part, positions, 0, 0))
        {
            if (zipf(candidate) is not { } z) continue;
            if (z < (changed == 1 ? MinZipfOneChar : MinZipfMoreChars)) continue;
            double score = z - changed * 1.5;
            if (score > bestScore) (best, bestScore) = (candidate, score);
        }
        return best ?? part;
    }

    /// <summary>Варианты слова с заменами (до <see cref="MaxChanges"/>) в позициях из <paramref name="positions"/>, по возрастанию позиции.</summary>
    private static IEnumerable<(string Word, int Changed)> Candidates(string word, string original, List<int> positions, int from, int changed)
    {
        if (changed == MaxChanges) yield break;
        for (int a = from; a < positions.Count; a++)
        {
            foreach (char r in Replacements(original[positions[a]])!)
            {
                string next = Replace(word, positions[a], r);
                yield return (next, changed + 1);
                foreach (var deeper in Candidates(next, original, positions, a + 1, changed + 1))
                    yield return deeper;
            }
        }
    }

    private static string Replace(string s, int index, char c) =>
        new StringBuilder(s) { [index] = c }.ToString();

    private static IEnumerable<char>? Replacements(char c)
    {
        if (Confusions.TryGetValue(c, out string? exact)) return exact;
        if (!char.IsLetter(c)) return null;
        // Заглавная буква даёт заглавную замену: «U» -> «Y».
        return Confusions.TryGetValue(char.ToLowerInvariant(c), out string? lower)
            ? lower.Select(r => char.IsUpper(c) ? char.ToUpperInvariant(r) : r)
            : null;
    }
}
