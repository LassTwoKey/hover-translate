using System.ComponentModel;
using System.Globalization;
using HoverTranslate.Core.Storage;
using Microsoft.UI.Xaml;

namespace HoverTranslate.App.Vocabulary;

/// <summary>Строка списка «Словаря»; звезда обновляется на месте, без перестроения списка.</summary>
public sealed class VocabularyRow : INotifyPropertyChanged
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private bool _isFavorite;

    public VocabularyRow(WordRecord record, bool showStar, bool isFavorite)
    {
        Record = record;
        StarVisibility = showStar ? Visibility.Visible : Visibility.Collapsed;
        _isFavorite = isFavorite;
        Title = string.Equals(record.Word, record.Lemma, StringComparison.OrdinalIgnoreCase)
            ? record.Lemma
            : $"{record.Lemma} ({record.Word})";
        TranslationSuffix = string.IsNullOrEmpty(record.Translation) ? "" : "  —  " + record.Translation;
        SentenceVisibility = string.IsNullOrWhiteSpace(record.Sentence) ? Visibility.Collapsed : Visibility.Visible;
        string when = record.At.LocalDateTime.ToString("d MMM yyyy, HH:mm", Russian);
        Meta = string.IsNullOrEmpty(record.SourceApp) ? when : $"{when} · {record.SourceApp}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WordRecord Record { get; }

    public long Id => Record.Id;

    public string Title { get; }

    /// <summary>«  —  перевод» или пустая строка (слова нет в словаре).</summary>
    public string TranslationSuffix { get; }

    public string Sentence => Record.Sentence ?? "";

    public Visibility SentenceVisibility { get; }

    public string Meta { get; }

    public Visibility StarVisibility { get; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StarGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StarTip)));
        }
    }

    public string StarGlyph => _isFavorite ? "" : "";

    public string StarTip => _isFavorite ? "Убрать из избранного" : "Добавить в избранное";
}
