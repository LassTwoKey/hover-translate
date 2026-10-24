using System.Text.RegularExpressions;

namespace HoverTranslate.Core.Translation;

/// <summary>
/// Подготовка предложения к модели. Единственное, что реально нужно (замер на формах «gonna», «wanna», «kinda», «dunno»,
/// «lemme», слов в верхнем регистре, «—», «…»: модель переводит их так же, как обычные): растянутые слова
/// («Nooooo», «Heeeey») ломают декодер — перевод превращается в «Нееееееееее…» до предела длины.
/// </summary>
public static partial class SentencePreprocessor
{
    /// <summary>Буквы, которые в английских междометиях удваиваются по-настоящему: «Hmm», «Mmm», «Shh», «Zzz», «Brr», «Fff».</summary>
    private const string KeptDoubled = "mhszrf";

    /// <summary>Три и больше одинаковых букв подряд («Nooooo») сжимаются: до одной, для «m h s z r f» — до двух.</summary>
    public static string Clean(string sentence) => sentence.Length < 3 ? sentence : Elongated().Replace(sentence, m =>
    {
        string first = m.Groups[1].Value;
        return KeptDoubled.Contains(char.ToLowerInvariant(first[0])) ? first + first.ToLowerInvariant() : first;
    });

    [GeneratedRegex(@"(\p{L})\1{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex Elongated();
}
