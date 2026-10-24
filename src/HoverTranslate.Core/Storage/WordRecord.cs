using System.Globalization;
using HoverTranslate.Core.Models;

namespace HoverTranslate.Core.Storage;

/// <summary>Запись «Словаря»: слово из истории или избранного.</summary>
public sealed record WordRecord(
    long Id,
    string Lemma,
    string Word,
    string? Translation,
    string? Definition,
    string? Sentence,
    string? SentenceRu,
    string? SourceApp,
    DateTimeOffset At)
{
    /// <summary>Запись по результату поиска: перевод и определение — из первого значения с переводом (как в карточке).</summary>
    public static WordRecord From(LookupResult result)
    {
        var sense = result.Entry?.Senses.FirstOrDefault(s => s.TranslationsRu.Count > 0) ?? result.Entry?.Senses.FirstOrDefault();
        return new WordRecord(0, result.Lemma, result.Word,
            sense is { TranslationsRu.Count: > 0 } ? string.Join(", ", sense.TranslationsRu) : null,
            sense?.DefinitionEn, result.Sentence, result.SentenceRu, result.SourceApp, DateTimeOffset.Now);
    }

    /// <summary>Поиск без учёта регистра: каждое слово запроса должно встретиться в слове, переводе, определении или предложении.</summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (string term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Contains(Lemma, term) && !Contains(Word, term) && !Contains(Translation, term)
                && !Contains(Definition, term) && !Contains(Sentence, term))
                return false;
        }
        return true;
    }

    private static bool Contains(string? text, string term) =>
        text is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);

    internal string AtText => At.ToString("O", CultureInfo.InvariantCulture);
}
