namespace HoverTranslate.Core.Models;

/// <summary>Откуда получен текст под курсором.</summary>
public enum TextSourceKind
{
    Uia,
    Ocr,
}

/// <summary>Итог поиска слова под курсором: всё, что показывает всплывающее окно.</summary>
public sealed record LookupResult(
    string Word,              // как на экране
    string Lemma,             // начальная форма
    string? Sentence,         // предложение-контекст на английском
    string? SentenceRu,       // перевод предложения
    DictEntry? Entry,         // словарная статья
    string SourceApp,         // имя процесса
    TextSourceKind Source)    // Uia | Ocr
{
    /// <summary>Все найденные статьи: фразы («give up») первыми, затем само слово. Entry — первая из них.</summary>
    public IReadOnlyList<DictEntry> Entries { get; init; } = Entry is null ? [] : [Entry];
}

public sealed record DictEntry(
    string Lemma,
    string? Ipa,
    string? Cefr,
    IReadOnlyList<Sense> Senses,
    IReadOnlyList<string> Synonyms);

public sealed record Sense(
    string PartOfSpeech,
    string DefinitionEn,
    IReadOnlyList<string> TranslationsRu,
    IReadOnlyList<string> Examples);
