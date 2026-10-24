namespace HoverTranslate.Core.Dictionary;

/// <summary>Какой частью речи, судя по соседнему слову, скорее всего является слово под курсором.</summary>
public enum PosHint
{
    None,
    /// <summary>После вспомогательного/модального глагола, подлежащего-местоимения: «was running», «he left».</summary>
    Verb,
    /// <summary>После артикля, указательного или притяжательного слова: «the building», «my left».</summary>
    Nominal,
}

/// <summary>Простейшая контекстная эвристика по предыдущему слову — без ML и разметки.</summary>
public static class ContextHints
{
    private static readonly HashSet<string> VerbTriggers = new(StringComparer.Ordinal)
    {
        "am", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "having",
        "will", "would", "shall", "should", "can", "could", "may", "might", "must",
        "do", "does", "did", "not", "never", "just", "already",
        "i", "he", "she", "we", "they", "who",
        "don't", "doesn't", "didn't", "won't", "wouldn't", "can't", "cannot", "couldn't", "shouldn't", "mustn't",
        "isn't", "aren't", "wasn't", "weren't", "haven't", "hasn't", "hadn't",
    };

    private static readonly HashSet<string> NominalTriggers = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "this", "these", "those", "my", "your", "his", "its", "our", "their",
        "no", "every", "each", "some", "any", "another", "such", "several", "many", "few",
    };

    /// <summary>«he's», «they're», «I've», «we'd», «I'm» — за ними глагол.</summary>
    private static readonly string[] VerbClitics = ["'s", "'re", "'ve", "'d", "'m", "'ll"];

    public static PosHint Guess(IReadOnlyList<Token> tokens, int index)
    {
        if (index <= 0 || index >= tokens.Count || tokens[index].BreakBefore) return PosHint.None;
        string prev = tokens[index - 1].Lower;
        if (NominalTriggers.Contains(prev)) return PosHint.Nominal;
        if (VerbTriggers.Contains(prev)) return PosHint.Verb;
        foreach (string clitic in VerbClitics)
        {
            // «John's book» тоже оканчивается на 's, поэтому только местоимения.
            if (prev.EndsWith(clitic, StringComparison.Ordinal) && IsPronoun(prev[..^clitic.Length]))
                return PosHint.Verb;
        }
        return PosHint.None;
    }

    /// <summary>Подходит ли часть речи (код wiktextract) к подсказке.</summary>
    public static bool Matches(PosHint hint, string pos) => hint switch
    {
        PosHint.Verb => pos == "verb",
        PosHint.Nominal => pos is "noun" or "adj" or "num",
        _ => false,
    };

    private static bool IsPronoun(string w) =>
        w is "i" or "you" or "he" or "she" or "it" or "we" or "they" or "who" or "that" or "there";
}
