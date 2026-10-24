namespace HoverTranslate.Core.Dictionary;

/// <summary>
/// Правила лемматизации для слов, которых нет ни среди лемм, ни в таблице словоформ:
/// -s/-es/-ies, -ed/-ied, -ing, -er/-est/-ier/-iest (с удвоенной согласной и немой «e»),
/// притяжательное 's и сокращения 're/'ll/'ve/'d/'m.
/// Возвращает кандидатов по убыванию правдоподобия; существование проверяет словарь.
/// </summary>
public static class Lemmatizer
{
    private const string Vowels = "aeiou";

    private static readonly string[] Clitics = ["'s", "'re", "'ll", "'ve", "'d", "'m"];

    /// <summary>Кандидаты в начальную форму для слова в нижнем регистре (без самого слова).</summary>
    public static IReadOnlyList<string> Candidates(string word)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(word)) return result;
        string w = word.ToLowerInvariant();

        // «world's» -> «world», «they're» -> «they»; дальше правила применяются и к основе.
        foreach (string clitic in Clitics)
        {
            if (w.Length > clitic.Length + 1 && w.EndsWith(clitic, StringComparison.Ordinal))
            {
                string stem = w[..^clitic.Length];
                Add(result, stem, w);
                foreach (string c in SuffixCandidates(stem)) Add(result, c, w);
                return result;
            }
        }
        // «students'» уже нормализован до «students»; «n't» оставляем словарю (don't, isn't есть как леммы).

        foreach (string c in SuffixCandidates(w)) Add(result, c, w);

        // «e-mail» -> «email», «well-known» не трогаем (нашлось бы раньше), «re-enter» -> «reenter».
        if (w.Contains('-'))
        {
            Add(result, w.Replace("-", ""), w);
            Add(result, w[(w.LastIndexOf('-') + 1)..], w);
        }
        return result;
    }

    private static IEnumerable<string> SuffixCandidates(string w)
    {
        // Множественное число и 3-е лицо.
        if (Ends(w, "ies", 4)) yield return w[..^3] + "y";              // cities -> city
        if (Ends(w, "ves", 4))
        {
            yield return w[..^3] + "f";                                 // leaves -> leaf
            yield return w[..^3] + "fe";                                // knives -> knife
        }
        if (Ends(w, "es", 3) && EndsWithSibilant(w[..^2])) yield return w[..^2]; // boxes, wishes
        if (Ends(w, "s", 3) && !w.EndsWith("ss", StringComparison.Ordinal) && !w.EndsWith("us", StringComparison.Ordinal))
            yield return w[..^1];                                       // cats, makes
        if (Ends(w, "es", 3)) yield return w[..^2];                     // goes

        // Прошедшее время / причастие.
        if (Ends(w, "ied", 4)) yield return w[..^3] + "y";              // tried -> try
        if (Ends(w, "ed", 4))
        {
            string stem = w[..^2];
            foreach (string c in StemVariants(stem)) yield return c;    // walked, liked, stopped
        }

        // -ing
        if (Ends(w, "ying", 5)) yield return w[..^4] + "ie";            // lying -> lie
        if (Ends(w, "ing", 5))
        {
            string stem = w[..^3];
            foreach (string c in StemVariants(stem)) yield return c;    // walking, making, running
        }

        // Сравнительная и превосходная степени.
        if (Ends(w, "iest", 6)) yield return w[..^4] + "y";             // happiest -> happy
        if (Ends(w, "ier", 5)) yield return w[..^3] + "y";              // happier -> happy
        if (Ends(w, "est", 5))
            foreach (string c in StemVariants(w[..^3])) yield return c; // biggest, latest
        if (Ends(w, "er", 4))
            foreach (string c in StemVariants(w[..^2])) yield return c; // bigger, later

        // Наречия на -ly: quickly -> quick, happily -> happy.
        if (Ends(w, "ily", 5)) yield return w[..^3] + "y";
        if (Ends(w, "ly", 5)) yield return w[..^2];
    }

    /// <summary>Основа после отсечения окончания: как есть, с удвоенной согласной без повтора, с немой «e».</summary>
    private static IEnumerable<string> StemVariants(string stem)
    {
        if (stem.Length >= 3 && stem[^1] == stem[^2] && !Vowels.Contains(stem[^1]) && stem[^1] is not ('l' or 's' or 'z'))
            yield return stem[..^1];                                    // stopp -> stop, runn -> run
        yield return stem;                                              // walk
        if (!stem.EndsWith('e')) yield return stem + "e";               // lik -> like, mak -> make
        if (stem.Length >= 3 && stem[^1] == stem[^2] && stem[^1] is 'l' or 's' or 'z')
            yield return stem[..^1];                                    // travell -> travel (брит.)
    }

    private static bool EndsWithSibilant(string stem) =>
        stem.EndsWith('s') || stem.EndsWith('x') || stem.EndsWith('z') || stem.EndsWith("ch", StringComparison.Ordinal)
        || stem.EndsWith("sh", StringComparison.Ordinal) || stem.EndsWith('o');

    private static bool Ends(string w, string suffix, int minLength) =>
        w.Length >= minLength && w.EndsWith(suffix, StringComparison.Ordinal);

    private static void Add(List<string> list, string candidate, string original)
    {
        if (candidate.Length >= 2 && candidate != original && !list.Contains(candidate))
            list.Add(candidate);
    }
}
