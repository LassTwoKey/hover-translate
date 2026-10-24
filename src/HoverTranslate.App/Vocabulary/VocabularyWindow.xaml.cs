using System.Collections.ObjectModel;
using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Speech;
using HoverTranslate.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace HoverTranslate.App.Vocabulary;

/// <summary>
/// Окно «Словарь»: вкладки «История» и «Избранное», поиск, удаление записи, очистка вкладки, экспорт для Anki.
/// Список обновляется на месте при изменении хранилищ (новое слово из карточки, звезда, удаление).
/// </summary>
public sealed partial class VocabularyWindow : Window
{
    private enum Tab
    {
        History,
        Favorites,
    }

    private readonly IHistoryStore _history;
    private readonly IFavoritesStore _favorites;
    private readonly ISettingsStore _settings;
    private readonly ISpeechService _speech;
    private readonly ObservableCollection<VocabularyRow> _rows = [];
    private readonly EventHandler _onHistoryChanged;
    private readonly EventHandler _onFavoritesChanged;
    private readonly EventHandler<string> _onSettingChanged;

    private bool _syncingToggle;
    private Tab _tab = Tab.History;
    private IReadOnlyList<WordRecord> _all = [];          // все записи открытой вкладки
    private HashSet<string> _favoriteLemmas = [];         // для звёзд на вкладке «История»
    private bool _closed;

    public VocabularyWindow(IHistoryStore history, IFavoritesStore favorites, ISettingsStore settings, ISpeechService speech)
    {
        _history = history;
        _favorites = favorites;
        _settings = settings;
        _speech = speech;
        InitializeComponent();

        ResultsList.ItemsSource = _rows;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HoverTranslate.ico"));
        WindowSizing.SizeAndCenter(AppWindow, 860, 640);

        HistoryToggle.IsOn = _history.Enabled;
        // Обработчики — после начальной установки состояния: иначе она сама бы вызвала их.
        HistoryToggle.Toggled += HistoryToggle_Toggled;
        Tabs.SelectionChanged += Tabs_SelectionChanged;
        SearchBox.TextChanged += (_, _) => ApplyFilter();

        _onHistoryChanged = (_, _) => RunOnUi(() =>
        {
            if (_tab == Tab.History) Reload();
        });
        _onFavoritesChanged = (_, _) => RunOnUi(Reload);
        // Историю можно включить и в окне настроек — переключатель здесь должен это показать.
        _onSettingChanged = (_, key) =>
        {
            if (key == SettingKeys.HistoryEnabled) RunOnUi(SyncHistoryToggle);
        };
        _history.Changed += _onHistoryChanged;
        _favorites.Changed += _onFavoritesChanged;
        _settings.Changed += _onSettingChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _history.Changed -= _onHistoryChanged;
            _favorites.Changed -= _onFavoritesChanged;
            _settings.Changed -= _onSettingChanged;
        };

        Reload();
    }

    /// <summary>Хранилища сообщают об изменениях из любого потока.</summary>
    private void RunOnUi(Action action)
    {
        if (_closed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) action();
        });
    }

    // ------------------------------------------------------------------ список

    private void Reload()
    {
        _all = _tab == Tab.History ? _history.List() : _favorites.List();
        _favoriteLemmas = _tab == Tab.History ? _favorites.List().Select(r => r.Lemma).ToHashSet() : [];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text;
        var shown = _all.Where(r => r.Matches(query)).ToList();
        SyncRows(shown);

        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = EmptyMessage(filtered: _all.Count > 0);
        StatusText.Text = shown.Count == _all.Count ? $"Записей: {_all.Count}" : $"Показано {shown.Count} из {_all.Count}";
    }

    /// <summary>
    /// Приводит строки к <paramref name="shown"/> без полной перестройки (позиция прокрутки не сбрасывается).
    /// И записи, и строки идут от новых к старым, поэтому новая запись всегда вставляется на своё место по порядку.
    /// </summary>
    private void SyncRows(IReadOnlyList<WordRecord> shown)
    {
        var ids = shown.Select(r => r.Id).ToHashSet();
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(_rows[i].Id)) _rows.RemoveAt(i);
        }
        bool history = _tab == Tab.History;
        for (int i = 0; i < shown.Count; i++)
        {
            bool favorite = history && _favoriteLemmas.Contains(shown[i].Lemma);
            if (i < _rows.Count && _rows[i].Id == shown[i].Id)
                _rows[i].IsFavorite = favorite;
            else
                _rows.Insert(i, new VocabularyRow(shown[i], showStar: history, favorite));
        }
    }

    private string EmptyMessage(bool filtered) =>
        filtered ? "Ничего не найдено."
        : _tab == Tab.History
            ? _history.Enabled
                ? "История пуста. Наведите курсор на английское слово и нажмите горячую клавишу."
                : "История выключена — новые слова не записываются."
            : "Избранное пусто. Нажмите звезду в карточке слова или в истории.";

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tab = sender.SelectedItem == FavoritesTab ? Tab.Favorites : Tab.History;
        if (tab == _tab) return;
        _tab = tab;
        _rows.Clear(); // идентификаторы двух таблиц не связаны
        HistoryToggle.Visibility = tab == Tab.History ? Visibility.Visible : Visibility.Collapsed;
        Reload();
    }

    private void SyncHistoryToggle()
    {
        if (HistoryToggle.IsOn != _history.Enabled)
        {
            _syncingToggle = true;
            HistoryToggle.IsOn = _history.Enabled;
            _syncingToggle = false;
        }
        ApplyFilter();
    }

    private void HistoryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle) return;
        _settings.Set(SettingKeys.HistoryEnabled, HistoryToggle.IsOn ? "true" : "false");
        ApplyFilter(); // текст пустой вкладки зависит от настройки
    }

    // ------------------------------------------------------------------ действия над строкой

    private static VocabularyRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as VocabularyRow;

    private void Speak_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _ = _speech.SpeakAsync(row.Record.Lemma);
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (row.IsFavorite) _favorites.RemoveByLemma(row.Record.Lemma);
        else _favorites.Add(row.Record);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (_tab == Tab.History) _history.Remove(row.Id);
        else _favorites.Remove(row.Id);
    }

    // ------------------------------------------------------------------ очистка и экспорт

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_all.Count == 0) return;
            bool history = _tab == Tab.History;
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = history ? "Очистить историю?" : "Очистить избранное?",
                Content = history
                    ? $"Будут удалены все записи истории ({_all.Count}). Избранное не затрагивается."
                    : $"Будут удалены все слова из избранного ({_all.Count}). Это нельзя отменить.",
                PrimaryButtonText = "Очистить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (history) _history.Clear();
            else _favorites.Clear();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось очистить: " + ex.Message;
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Csv);

    private void ExportTxt_Click(object sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Txt);

    /// <summary>Сохраняет все записи открытой вкладки (поиск на экспорт не влияет).</summary>
    private async Task ExportAsync(ExportFormat format)
    {
        try
        {
            if (_all.Count == 0)
            {
                StatusText.Text = "Нечего экспортировать: вкладка пуста.";
                return;
            }
            bool csv = format == ExportFormat.Csv;
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = _tab == Tab.History ? "hovertranslate-history" : "hovertranslate-favorites",
            };
            picker.FileTypeChoices.Add(csv ? "CSV (UTF-8)" : "Текст (табуляция)", [csv ? ".csv" : ".txt"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var records = _all;
            await Task.Run(() => AnkiExporter.Write(file.Path, records, format));
            StatusText.Text = $"Сохранено записей: {records.Count} → {file.Path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Не удалось сохранить файл: " + ex.Message;
        }
    }
}
