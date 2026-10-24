using System.Text;
using System.Text.RegularExpressions;

namespace HoverTranslate.Core.TextSource;

/// <summary>Нормализация слова с экрана: пунктуация по краям убирается, апострофы и дефисы внутри сохраняются.</summary>
public static partial class WordNormalizer
{
    private const int MaxWordLength = 45;

    /// <summary>«“Don’t,» -> «Don't», «(well-known)» -> «well-known», «students'» -> «students».</summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
            sb.Append(c switch
            {
                '’' or '‘' or 'ʼ' or '´' or '`' => '\'', // ’ ‘ ʼ ´ `
                '‐' or '‑' => '-',                                  // типографские дефисы
                '­' => '\0',                                             // мягкий перенос
                _ => c,
            });
        sb.Replace("\0", "");

        int start = 0, end = sb.Length;
        while (start < end && !char.IsLetterOrDigit(sb[start])) start++;
        while (end > start && !char.IsLetterOrDigit(sb[end - 1])) end--;
        return sb.ToString(start, end - start);
    }

    /// <summary>
    /// Похоже ли на английское слово: латиница (вкл. Latin-1), цифры, внутренние апострофы и дефисы,
    /// хотя бы одна буква. Отсекает мусор UIA/OCR: пустые строки, целые абзацы, «||», «©2024».
    /// </summary>
    public static bool IsPlausibleWord(string word) =>
        word.Length is > 0 and <= MaxWordLength && WordRegex().IsMatch(word);

    [GeneratedRegex(@"^(?=.*[A-Za-zÀ-ÖØ-öø-ÿ])[A-Za-z0-9À-ÖØ-öø-ÿ]+(?:['\-][A-Za-z0-9À-ÖØ-öø-ÿ]+)*$")]
    private static partial Regex WordRegex();
}
