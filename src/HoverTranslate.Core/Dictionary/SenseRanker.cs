using System.Text.RegularExpressions;
using HoverTranslate.Core.Models;

namespace HoverTranslate.Core.Dictionary;

/// <summary>
/// Выбор значения по контексту: значения ранжируются по числу слов предложения, встречающихся
/// в определении и примерах (после отбрасывания служебных слов и грубого стемминга).
/// Плюс подсказка части речи, небольшой бонус за русский перевод и штраф за устаревшие/редкие значения.
/// При равенстве сохраняется порядок Викисловаря (первые значения обычно самые частые).
/// </summary>
public static partial class SenseRanker
{
    private const double OverlapWeight = 1.0;
    private const double ExampleOverlapWeight = 0.5;
    private const double PosWeight = 1.0;
    private const double TranslationWeight = 0.3;
    private const double LabelPenalty = 1.0;

    /// <summary>Служебные слова и типовые слова определений Викисловаря — для выбора значения ничего не дают.</summary>
    private static readonly HashSet<string> StopWords = new(FunctionWords.All, StringComparer.Ordinal)
    {
        "yes", "let", "got", "get", "gets", "anything", "anyone", "used", "use", "especially", "usually",
        "often", "typically", "generally", "certain", "particular", "thing", "things", "person", "people",
        "act", "state", "quality", "process", "manner", "way", "kind", "type", "form", "make", "cause",
        "become", "relating", "pertaining", "countable", "uncountable", "transitive", "intransitive",
        "figurative", "figuratively", "informal", "colloquial", "slang", "chiefly", "british", "american",
        "plural", "singular", "sense", "senses", "term", "word",
    };

    /// <summary>Пометки в начале определения, которые делают значение маловероятным в живом тексте.</summary>
    [GeneratedRegex(@"^\([^)]*\b(obsolete|archaic|dated|rare|dialectal|dialect|historical|nonstandard|poetic|nautical slang)\b[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RareLabelRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z'\-]*")]
    private static partial Regex WordRegex();

    /// <param name="tokens">Слова предложения.</param>
    /// <param name="skipFrom">Начало слова/фразы под курсором (эти слова не считаются контекстом).</param>
    /// <param name="skipCount">Длина фразы в словах.</param>
    public static IReadOnlyList<Sense> Rank(IReadOnlyList<Sense> senses, IReadOnlyList<Token> tokens,
        int skipFrom, int skipCount, PosHint hint)
    {
        if (senses.Count < 2) return senses;
        var context = ContextStems(tokens, skipFrom, skipCount);
        return senses
            .Select((sense, index) => (sense, index, score: Score(sense, context, hint)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.sense)
            .ToList();
    }

    internal static double Score(Sense sense, IReadOnlySet<string> context, PosHint hint)
    {
        double score = 0;
        if (context.Count > 0)
        {
            // Примеры — длинные живые предложения, случайных совпадений в них больше: вес вдвое меньше.
            var definition = new HashSet<string>(StringComparer.Ordinal);
            AddStems(definition, sense.DefinitionEn);
            var examples = new HashSet<string>(StringComparer.Ordinal);
            foreach (string example in sense.Examples) AddStems(examples, example);
            foreach (string stem in context)
            {
                if (definition.Contains(stem)) score += OverlapWeight;
                else if (examples.Contains(stem)) score += ExampleOverlapWeight;
            }
        }
        if (ContextHints.Matches(hint, sense.PartOfSpeech)) score += PosWeight;
        if (sense.TranslationsRu.Count > 0) score += TranslationWeight;
        if (RareLabelRegex().IsMatch(sense.DefinitionEn)) score -= LabelPenalty;
        return score;
    }

    internal static HashSet<string> ContextStems(IReadOnlyList<Token> tokens, int skipFrom, int skipCount)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (i >= skipFrom && i < skipFrom + skipCount) continue;
            AddStems(set, tokens[i].Text);
        }
        return set;
    }

    private static void AddStems(HashSet<string> set, string text)
    {
        foreach (Match m in WordRegex().Matches(text))
        {
            string w = m.Value.ToLowerInvariant();
            if (w.Length < 3 || StopWords.Contains(w)) continue;
            string stem = Stem(w);
            if (stem.Length >= 3 && !StopWords.Contains(stem)) set.Add(stem);
        }
    }

    /// <summary>Грубый стемминг: одно частое окончание. Одинаково применяется к контексту и определению.</summary>
    internal static string Stem(string w)
    {
        if (w.EndsWith("'s", StringComparison.Ordinal)) w = w[..^2];
        if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) return Undouble(w[..^3]);
        if (w.Length > 4 && w.EndsWith("ied", StringComparison.Ordinal)) return w[..^3] + "y";
        if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) return w[..^3] + "y";
        if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) return Undouble(w[..^2]);
        if (w.Length > 4 && w.EndsWith("ly", StringComparison.Ordinal)) return w[..^2];
        if (w.Length > 4 && w.EndsWith("es", StringComparison.Ordinal) && (w[^3] is 's' or 'x' or 'z' or 'h')) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) return w[..^1];
        if (w.Length > 3 && w.EndsWith('e')) return w[..^1]; // «make»/«making» -> «mak»
        return w;
    }

    private static string Undouble(string s) =>
        s.Length >= 3 && s[^1] == s[^2] && !"aeiou".Contains(s[^1]) ? s[..^1] : s;
}
