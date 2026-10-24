using System.Text.RegularExpressions;
using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Dictionary;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Native;
using HoverTranslate.Core.Popup;
using HoverTranslate.Core.Speech;
using HoverTranslate.Core.Storage;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using WinRT;

namespace HoverTranslate.App.Popup;

/// <summary>Чей перевод предложения показан в карточке.</summary>
public enum TranslationEngine
{
    /// <summary>Встроенная модель на этом компьютере.</summary>
    Local,
    /// <summary>Облачный перевод Gemini.</summary>
    Cloud,
}

/// <summary>Что показать в карточке: результат поиска или сообщение.</summary>
public abstract record PopupContent
{
    public sealed record Result(LookupResult Value) : PopupContent;

    public sealed record Message(string Title, string? Hint) : PopupContent;

    /// <summary>Пока ищется или распознаётся текст: крутилка и подпись. <paramref name="IsArea"/> — карточка области (шире).</summary>
    public sealed record Loading(string Label, bool IsArea) : PopupContent;

    /// <summary>
    /// Текст области (Alt+W) или предложения под курсором: перевод сверху, оригинал ниже (всегда развёрнут).
    /// <paramref name="Highlight"/> — слово под курсором, подсвечивается в оригинале.
    /// </summary>
    public sealed record Area(string Text, string? Highlight = null, int HighlightOffset = -1) : PopupContent;
}

/// <summary>Внешний вид карточки из настроек; применяется к уже открытой карточке на ходу.</summary>
public sealed record PopupAppearance(int FontPercent, int OpacityPercent, PopupTheme Theme, bool ExcludeFromCapture)
{
    public static PopupAppearance From(AppSettings s) =>
        new(s.PopupFontPercent, s.PopupOpacityPercent, s.PopupTheme, s.PopupExcludeFromCapture);
}

/// <summary>
/// Всплывающая карточка. Окно WinUI 3 без рамки с WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST:
/// показывается и принимает щелчки, не забирая фокус у приложения под курсором.
/// </summary>
public sealed partial class PopupWindow : Window
{
    public const double CardWidth = 380;
    private const double AreaCardWidth = 460;
    private const double MaxCardHeight = 560;
    private static readonly Color DarkCard = Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C);
    private static readonly Color LightCard = Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9);

    private readonly nint _hwnd;
    private readonly ArrowWindow _arrow;
    private readonly ISpeechService _speech;
    private readonly IFavoritesStore _favorites;
    private readonly DesktopAcrylicController _acrylic = new();
    private readonly SystemBackdropConfiguration _backdropConfig = new() { IsInputActive = true };
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LookupResult? _result;   // со статьёй, выбранной во вкладках
    private string? _areaText;       // текст области (Alt+W)
    private double _cardWidth = CardWidth;
    private bool _areaMode;
    private double _fontScale = 1;
    private float _tintOpacity = AppSettings.OpacityPercentDefault / 100f;
    private bool? _excludedFromCapture;
    private bool _pointerOver;
    private Sense? _mainSense;
    private ScreenRect _anchor;
    private ArrowSide _side;
    private bool _fitting;

    public bool IsShown { get; private set; }

    public bool IsPinned => PinButton.IsChecked == true;

    /// <summary>Прямоугольник карточки на экране (физические px).</summary>
    public ScreenRect CardRect { get; private set; }

    public ScreenRect ArrowRect { get; private set; }

    public event EventHandler? CloseRequested;

    /// <summary>Карточка перемерена и переставлена (перевод вырос, раскрыли блок): прямоугольники <see cref="CardRect"/> и <see cref="ArrowRect"/> новые.</summary>
    public event EventHandler? LayoutChanged;

    public event EventHandler<bool>? PinnedChanged;

    /// <summary>Пользователь переключил «Локально | Облако» под переводом предложения.</summary>
    public event EventHandler<TranslationEngine>? EngineSelected;

    /// <summary>Указатель мыши зашёл на карточку или ушёл с неё (автозакрытие ждёт, пока карточку читают).</summary>
    public event EventHandler<bool>? PointerOverChanged;

    public bool IsPointerOver => _pointerOver;

    public PopupWindow(ArrowWindow arrow, ISpeechService speech, IFavoritesStore favorites, PopupAppearance appearance)
    {
        InitializeComponent();
        _arrow = arrow;
        _speech = speech;
        _favorites = favorites;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        WindowStyles.MakeNoActivateTopmost(_hwnd);
        NoFrameSubclass.Attach(_hwnd);
        WindowStyles.SetRoundedCorners(_hwnd);

        // Acrylic вместо SystemBackdrop: стандартный фон в неактивном окне превращается в сплошной цвет,
        // а карточка неактивна всегда — поэтому сообщаем контроллеру, что окно «активно».
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
        ApplyAppearance(appearance);
        Root.ActualThemeChanged += (_, _) => ApplyTheme();
        Root.Loaded += (_, _) => _loaded.TrySetResult();
        Root.PointerEntered += (_, _) => SetPointerOver(true);
        Root.PointerExited += (_, _) => SetPointerOver(false);
        Closed += (_, _) => _acrylic.Dispose();
    }

    /// <summary>Показать карточку у слова <paramref name="anchor"/>. Закреплённая карточка остаётся на месте.</summary>
    public async Task ShowAsync(PopupContent content, ScreenRect anchor)
    {
        Fill(content);
        if (!_loaded.Task.IsCompleted)
        {
            // Первый показ: XAML загружается только у показанного окна — показываем его за экраном.
            WindowStyles.ShowNoActivate(_hwnd, new ScreenRect(-32000, -32000, 10, 10));
            await _loaded.Task;
        }
        if (!(IsShown && IsPinned))
            _anchor = anchor;
        IsShown = true;
        Fit();
    }

    /// <summary>Закрепить или открепить карточку из кода (пример карточки в настройках).</summary>
    public void SetPinned(bool pinned)
    {
        if (IsPinned == pinned) return;
        PinButton.IsChecked = pinned;
        PinnedChanged?.Invoke(this, pinned);
    }

    /// <summary>
    /// Применить настройки внешнего вида — при создании и на ходу, пока карточка открыта.
    /// Размер шрифта задаётся каждому тексту отдельно (у них разные базовые размеры), ширина растёт вдвое медленнее шрифта.
    /// </summary>
    public void ApplyAppearance(PopupAppearance a)
    {
        _fontScale = a.FontPercent / 100.0;
        _tintOpacity = a.OpacityPercent / 100f;
        Root.RequestedTheme = a.Theme switch
        {
            PopupTheme.Light => ElementTheme.Light,
            PopupTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (_excludedFromCapture != a.ExcludeFromCapture)
        {
            _excludedFromCapture = a.ExcludeFromCapture;
            WindowStyles.ExcludeFromCapture(_hwnd, a.ExcludeFromCapture);
            _arrow.ExcludeFromCapture(a.ExcludeFromCapture);
        }
        ApplyFonts();
        ApplyWidth();
        ApplyTheme();
        if (IsShown) DispatcherQueue.TryEnqueue(Fit);
    }

    private double Sz(double basic) => Math.Round(basic * _fontScale, 1);

    private void ApplyFonts()
    {
        (TextBlock Block, double Size)[] texts =
        [
            (MessageText, 14), (MessageHint, 12), (LoadingText, 14), (TranslatorHeader, 12), (TranslatorNote, 12), (SentenceRuText, 15), (WordText, 26), (CefrText, 12),
            (IpaPosText, 14), (TranslationText, 17), (DefinitionText, 14), (SynonymsText, 14), (NotInDictText, 14),
            (SensesHeader, 14), (ContextHeader, 14), (SourceText, 12),
        ];
        foreach (var (block, size) in texts) block.FontSize = Sz(size);
        SentenceText.FontSize = Sz(14);
        WordText.MaxWidth = 220 * (1 + (_fontScale - 1) * 0.5);
        foreach (var tab in EntryTabs.Children.OfType<ToggleButton>()) tab.FontSize = Sz(13);
        foreach (var tb in SensesPanel.Children.OfType<TextBlock>()) tb.FontSize = Sz(14);
    }

    private void ApplyWidth()
    {
        _cardWidth = (_areaMode ? AreaCardWidth : CardWidth) * (1 + (_fontScale - 1) * 0.5);
        Root.Width = _cardWidth;
    }

    private void SetPointerOver(bool over)
    {
        if (_pointerOver == over) return;
        _pointerOver = over;
        PointerOverChanged?.Invoke(this, over);
    }

    public void HidePopup()
    {
        IsShown = false;
        _pointerOver = false;
        PinButton.IsChecked = false; // закрытая карточка не остаётся закреплённой
        SkeletonPulse.Stop();
        LoadingRing.IsActive = false;
        WindowStyles.Hide(_hwnd);
        _arrow.Hide();
    }

    /// <summary>
    /// Переключатель «Локально | Облако» под переводом: показывается, только если облако доступно (есть ключ).
    /// <paramref name="selected"/> — чей перевод показан сейчас.
    /// </summary>
    public void SetEngineSwitch(bool visible, TranslationEngine selected)
    {
        EngineRow.Visibility = visible || TranslatorNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LocalEngineButton.Visibility = CloudEngineButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LocalEngineButton.IsChecked = selected == TranslationEngine.Local;
        CloudEngineButton.IsChecked = selected == TranslationEngine.Cloud;
        if (IsShown) DispatcherQueue.TryEnqueue(Fit);
    }

    /// <summary>Подсказка под переводом («Облачный перевод…», «Облако недоступно: …»); null — убрать.</summary>
    public void SetTranslatorNote(string? note)
    {
        TranslatorNote.Text = note ?? "";
        TranslatorNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        if (note is not null) EngineRow.Visibility = Visibility.Visible;
        else if (LocalEngineButton.Visibility == Visibility.Collapsed) EngineRow.Visibility = Visibility.Collapsed;
        if (IsShown) DispatcherQueue.TryEnqueue(Fit);
    }

    private void LocalEngine_Click(object sender, RoutedEventArgs e)
    {
        // ToggleButton сам переключился — состояние выставляет SetEngineSwitch после обработки в контроллере.
        LocalEngineButton.IsChecked = true;
        EngineSelected?.Invoke(this, TranslationEngine.Local);
    }

    private void CloudEngine_Click(object sender, RoutedEventArgs e)
    {
        CloudEngineButton.IsChecked = true;
        EngineSelected?.Invoke(this, TranslationEngine.Cloud);
    }

    /// <summary>Перевод предложения: null + loading — скелетон; текст — готово; null без loading — блок скрыт.</summary>
    public void SetSentenceTranslation(string? text, bool loading)
    {
        if (_result is null && _areaText is null) return;
        if (text is null && !loading)
        {
            TranslatorBlock.Visibility = Visibility.Collapsed;
            SkeletonPulse.Stop();
            return;
        }
        TranslatorBlock.Visibility = Visibility.Visible;
        Skeleton.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        SentenceRuText.Text = text ?? "";
        SentenceRuText.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        CopySentenceRuItem.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (loading) SkeletonPulse.Begin();
        else SkeletonPulse.Stop();
    }

    // ------------------------------------------------------------------ заполнение

    private void Fill(PopupContent content)
    {
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        SetAreaMode(content is PopupContent.Area || content is PopupContent.Loading { IsArea: true });
        _areaText = null;
        LoadingPanel.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        if (content is PopupContent.Loading loading)
        {
            _result = null;
            _mainSense = null;
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
            LoadingText.Text = loading.Label;
            MessagePanel.Visibility = TranslatorBlock.Visibility = DictBlock.Visibility = ContextExpander.Visibility = Visibility.Collapsed;
            SkeletonPulse.Stop();
            BookmarkButton.Visibility = Visibility.Collapsed;
            Root.ContextFlyout = null;
            return;
        }
        if (content is PopupContent.Area area)
        {
            _result = null;
            _mainSense = null;
            _areaText = area.Text;
            MessagePanel.Visibility = DictBlock.Visibility = BookmarkButton.Visibility = Visibility.Collapsed;
            TranslatorBlock.Visibility = Visibility.Collapsed; // покажет SetSentenceTranslation
            ContextExpander.Visibility = Visibility.Visible;
            ContextExpander.IsExpanded = true; // оригинал всегда на виду: по нему видно, что на самом деле прочитал OCR
            FillSentence(area.Text, area.Highlight, area.HighlightOffset);
            SourceText.Text = "распознано с экрана";
            Root.ContextFlyout = CopyMenu;
            CopyWordItem.Visibility = CopyTranslationItem.Visibility = Visibility.Collapsed;
            CopySentenceItem.Visibility = Visibility.Visible;
            CopySentenceRuItem.Visibility = Visibility.Collapsed;
            return;
        }
        if (content is PopupContent.Message message)
        {
            _result = null;
            _mainSense = null;
            MessagePanel.Visibility = Visibility.Visible;
            MessageText.Text = message.Title;
            MessageHint.Text = message.Hint ?? "";
            MessageHint.Visibility = message.Hint is null ? Visibility.Collapsed : Visibility.Visible;
            TranslatorBlock.Visibility = DictBlock.Visibility = ContextExpander.Visibility = Visibility.Collapsed;
            BookmarkButton.Visibility = Visibility.Collapsed;
            Root.ContextFlyout = null;
            return;
        }

        var r = ((PopupContent.Result)content).Value;
        MessagePanel.Visibility = Visibility.Collapsed;
        DictBlock.Visibility = Visibility.Visible;
        BookmarkButton.Visibility = Visibility.Visible;
        Root.ContextFlyout = CopyMenu;
        CopyWordItem.Visibility = Visibility.Visible;
        ContextExpander.IsExpanded = true;
        TranslatorBlock.Visibility = Visibility.Collapsed; // покажет SetSentenceTranslation

        // Контекст
        FillSentence(r.Sentence, r.Word);
        ContextExpander.Visibility = r.Sentence is null ? Visibility.Collapsed : Visibility.Visible;
        SourceText.Text = $"{r.SourceApp} · {(r.Source == TextSourceKind.Uia ? "текст приложения" : "распознано с экрана")}";
        CopySentenceItem.Visibility = r.Sentence is null ? Visibility.Collapsed : Visibility.Visible;
        CopySentenceRuItem.Visibility = Visibility.Collapsed;

        FillEntryTabs(r);
        ShowEntry(r, r.Entry);
    }

    /// <summary>Текст области — шире карточка и другие подписи; результат поиска слова — обычный вид.</summary>
    private void SetAreaMode(bool area)
    {
        _areaMode = area;
        ApplyWidth();
        TranslatorHeader.Text = area ? "ПЕРЕВОД" : "ПЕРЕВОД ПРЕДЛОЖЕНИЯ";
        ContextHeader.Text = area ? "Оригинал" : "Контекст";
        CopySentenceItem.Text = area ? "Копировать оригинал" : "Копировать предложение";
        CopySentenceRuItem.Text = area ? "Копировать перевод" : "Копировать перевод предложения";
        ToolTipService.SetToolTip(SpeakSentenceButton, area ? "Произнести оригинал" : "Произнести предложение");
    }

    /// <summary>Вкладки статей, если найдено несколько (фраза + слово).</summary>
    private void FillEntryTabs(LookupResult r)
    {
        EntryTabs.Children.Clear();
        EntryTabs.Visibility = r.Entries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (r.Entries.Count < 2) return;
        foreach (var entry in r.Entries)
        {
            var tab = new ToggleButton
            {
                Content = entry.Lemma,
                IsChecked = ReferenceEquals(entry, r.Entry),
                Padding = new Thickness(10, 2, 10, 3),
                MinHeight = 0,
                FontSize = Sz(13),
                CornerRadius = new CornerRadius(11),
                Tag = entry,
            };
            tab.Click += (_, _) =>
            {
                foreach (var t in EntryTabs.Children.OfType<ToggleButton>()) t.IsChecked = ReferenceEquals(t, tab);
                if (_result is not null) ShowEntry(_result, entry);
            };
            EntryTabs.Children.Add(tab);
        }
    }

    /// <summary>Словарная часть карточки для выбранной статьи.</summary>
    private void ShowEntry(LookupResult r, DictEntry? entry)
    {
        r = entry is null ? r : r with { Lemma = entry.Lemma, Entry = entry };
        _result = r;
        WordText.Text = entry?.Lemma ?? r.Word;
        _mainSense = entry?.Senses.FirstOrDefault(s => s.TranslationsRu.Count > 0) ?? entry?.Senses.FirstOrDefault();

        // CEFR
        CefrBadge.Visibility = entry?.Cefr is null ? Visibility.Collapsed : Visibility.Visible;
        if (entry?.Cefr is { } cefr)
        {
            CefrText.Text = cefr;
            CefrBadge.Background = new SolidColorBrush(CefrColor(cefr));
        }

        // IPA · часть речи (и форма с экрана, если отличается: «ran → run»)
        var meta = new List<string>();
        if (entry?.Ipa is { } ipa) meta.Add(ipa);
        if (_mainSense is not null) meta.Add(PartOfSpeech.Russian(_mainSense.PartOfSpeech));
        if (entry is not null && !entry.Lemma.Contains(' ') && !string.Equals(r.Word, entry.Lemma, StringComparison.OrdinalIgnoreCase))
            meta.Add($"на экране: {r.Word}");
        IpaPosText.Text = string.Join("  ·  ", meta);
        IpaPosText.Visibility = meta.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Основной перевод
        string translation = _mainSense is { TranslationsRu.Count: > 0 } ? string.Join(", ", _mainSense.TranslationsRu) : "";
        TranslationText.Text = translation;
        bool hasTranslation = translation.Length > 0;
        TranslationText.Visibility = hasTranslation ? Visibility.Visible : Visibility.Collapsed;
        CopyTranslationItem.Visibility = hasTranslation ? Visibility.Visible : Visibility.Collapsed;

        DefinitionText.Text = _mainSense?.DefinitionEn ?? "";
        DefinitionText.Visibility = _mainSense is null ? Visibility.Collapsed : Visibility.Visible;

        var synonyms = entry?.Synonyms.Take(6).ToList() ?? [];
        SynonymsText.Text = synonyms.Count > 0 ? "Синонимы: " + string.Join(", ", synonyms) : "";
        SynonymsText.Visibility = synonyms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        NotInDictText.Visibility = entry is null ? Visibility.Visible : Visibility.Collapsed;
        FillOtherSenses(entry);

        UpdateBookmark(_favorites.IsFavorite(r.Lemma));
    }

    private void FillOtherSenses(DictEntry? entry)
    {
        SensesPanel.Children.Clear();
        var others = entry?.Senses.Where(s => !ReferenceEquals(s, _mainSense)).Take(15).ToList() ?? [];
        SensesExpander.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SensesExpander.IsExpanded = false;
        SensesHeader.Text = $"Другие значения ({others.Count})";
        foreach (var s in others)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = Sz(14) };
            tb.Inlines.Add(new Run
            {
                Text = PartOfSpeech.Russian(s.PartOfSpeech) + "  ",
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = SecondaryTextBrush(),
            });
            if (s.TranslationsRu.Count > 0)
                tb.Inlines.Add(new Run { Text = string.Join(", ", s.TranslationsRu) + " — ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            tb.Inlines.Add(new Run { Text = s.DefinitionEn });
            SensesPanel.Children.Add(tb);
        }
    }

    /// <summary>
    /// Предложение с подсвеченным словом. Слово ищется в известном месте <paramref name="offset"/> (если там оно и есть),
    /// иначе — первое целое вхождение; <paramref name="word"/> = null — без подсветки.
    /// </summary>
    private void FillSentence(string? sentence, string? word, int offset = -1)
    {
        SentenceText.Blocks.Clear();
        if (sentence is null) return;
        var paragraph = new Paragraph();
        var (start, length) = FindHighlight(sentence, word, offset);
        if (length > 0)
        {
            paragraph.Inlines.Add(new Run { Text = sentence[..start] });
            paragraph.Inlines.Add(new Run
            {
                Text = sentence.Substring(start, length),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AccentTextBrush(),
            });
            paragraph.Inlines.Add(new Run { Text = sentence[(start + length)..] });
        }
        else
        {
            paragraph.Inlines.Add(new Run { Text = sentence });
        }
        SentenceText.Blocks.Add(paragraph);
    }

    /// <summary>Где в предложении слово: (начало, длина); длина 0 — не найдено.</summary>
    internal static (int Start, int Length) FindHighlight(string sentence, string? word, int offset)
    {
        if (string.IsNullOrEmpty(word)) return (0, 0);
        if (offset >= 0 && offset + word.Length <= sentence.Length
            && string.Compare(sentence, offset, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
            return (offset, word.Length);
        var match = Regex.Match(sentence, $@"(?<![\w'])({Regex.Escape(word)})(?![\w'])", RegexOptions.IgnoreCase);
        return match.Success ? (match.Index, match.Length) : (0, 0);
    }

    private static Color CefrColor(string level) => level switch
    {
        "A1" => Color.FromArgb(255, 0x2E, 0x7D, 0x32),
        "A2" => Color.FromArgb(255, 0x43, 0xA0, 0x47),
        "B1" => Color.FromArgb(255, 0x1E, 0x88, 0xE5),
        "B2" => Color.FromArgb(255, 0x15, 0x65, 0xC0),
        "C1" => Color.FromArgb(255, 0x8E, 0x24, 0xAA),
        _ => Color.FromArgb(255, 0x6A, 0x1B, 0x9A),
    };

    // ------------------------------------------------------------------ размер и положение

    /// <summary>Измеряет содержимое, выбирает сторону (под/над словом) и ставит окно и стрелку.</summary>
    private void Fit()
    {
        if (!IsShown || _fitting) return;
        _fitting = true;
        try
        {
            var monitor = ScreenGeometry.MonitorFromPoint(new ScreenPoint((int)_anchor.CenterX, (int)_anchor.CenterY));
            double scale = monitor.Scale;
            Body.Measure(new Windows.Foundation.Size(_cardWidth, double.PositiveInfinity));
            double maxHeight = Math.Min(MaxCardHeight * _fontScale, monitor.WorkArea.Height / scale * 0.85);
            double height = Math.Min(Math.Ceiling(Body.DesiredSize.Height), maxHeight);

            var layout = PopupPlacement.Place(_anchor, _cardWidth * scale, height * scale, monitor.WorkArea, PopupMetrics.ForScale(scale));
            _side = layout.Side;
            CardRect = layout.Card;
            ArrowRect = layout.Arrow;
            WindowStyles.ShowNoActivate(_hwnd, layout.Card);
            _arrow.Show(layout.Arrow, layout.Side, ToArgb(CurrentCardColor()));
        }
        finally
        {
            _fitting = false;
        }
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Body_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsShown && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5)
            DispatcherQueue.TryEnqueue(Fit); // раскрыли «Другие значения» и т. п.
    }

    // ------------------------------------------------------------------ тема

    private Color CurrentCardColor() => Root.ActualTheme == ElementTheme.Light ? LightCard : DarkCard;

    private void ApplyTheme()
    {
        bool light = Root.ActualTheme == ElementTheme.Light;
        _backdropConfig.Theme = light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
        var color = CurrentCardColor();
        // Плотный тинт: стрелка (сплошной цвет) почти не отличается от карточки на пёстром фоне игры.
        _acrylic.TintColor = color;
        _acrylic.TintOpacity = _tintOpacity;
        _acrylic.LuminosityOpacity = 1f;
        _acrylic.FallbackColor = color;
        WindowStyles.SetBorderColor(_hwnd, ToArgb(color));
        if (IsShown) _arrow.Show(ArrowRect, _side, ToArgb(color));
        RefreshThemedContent();
    }

    // Тексты, созданные в коде, не следят за темой карточки: Application.Current.Resources отдаёт кисти
    // темы приложения (Windows), а у карточки она может быть другой. Цвета берутся по теме самой карточки.
    private Brush AccentTextBrush() => new SolidColorBrush(Root.ActualTheme == ElementTheme.Light
        ? Color.FromArgb(0xFF, 0x00, 0x3E, 0x92) : Color.FromArgb(0xFF, 0x99, 0xEB, 0xFF));

    private Brush SecondaryTextBrush() => new SolidColorBrush(Root.ActualTheme == ElementTheme.Light
        ? Color.FromArgb(0x9E, 0x00, 0x00, 0x00) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));

    /// <summary>После смены темы пересоздаёт тексты с цветами в коде (подсветка слова, части речи в «Других значениях»).</summary>
    private void RefreshThemedContent()
    {
        if (_result is null) return;
        bool expanded = SensesExpander.IsExpanded;
        FillSentence(_result.Sentence, _result.Word);
        FillOtherSenses(_result.Entry);
        SensesExpander.IsExpanded = expanded; // FillOtherSenses сворачивает блок
    }

    private static uint ToArgb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    // ------------------------------------------------------------------ действия

    private void SpeakWord_Click(object sender, RoutedEventArgs e) => _ = _speech.SpeakAsync(WordText.Text);

    private void SpeakSentence_Click(object sender, RoutedEventArgs e)
    {
        if ((_result?.Sentence ?? _areaText) is { } s) _ = _speech.SpeakAsync(s);
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_result is not null) UpdateBookmark(_favorites.Toggle(_result));
    }

    private void UpdateBookmark(bool isFavorite)
    {
        BookmarkIcon.Glyph = isFavorite ? "" : "";
        ToolTipService.SetToolTip(BookmarkButton, isFavorite ? "Убрать из избранного" : "Добавить в избранное");
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => PinnedChanged?.Invoke(this, IsPinned);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void CopyWord_Click(object sender, RoutedEventArgs e) => Copy(_result?.Entry?.Lemma ?? _result?.Word);

    private void CopyTranslation_Click(object sender, RoutedEventArgs e) => Copy(TranslationText.Text);

    private void CopySentence_Click(object sender, RoutedEventArgs e) => Copy(_result?.Sentence ?? _areaText);

    private void CopySentenceRu_Click(object sender, RoutedEventArgs e) => Copy(SentenceRuText.Text);

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
