using System.Collections.ObjectModel;
using System.Diagnostics;
using HoverTranslate.Core;
using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Hotkeys;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Speech;
using HoverTranslate.Core.Storage;
using HoverTranslate.Core.TextSource;
using HoverTranslate.Core.Translation;
using HoverTranslate.Core.Translation.Cloud;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Windows.UI.Core;

namespace HoverTranslate.App.Settings;

/// <summary>
/// Окно настроек. Каждое изменение сразу записывается в <see cref="ISettingsStore"/> и действует без перезапуска:
/// сочетания перерегистрирует LookupCoordinator, вид карточки применяет PopupController, список исключений — ExclusionService.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>Причина приостановки горячих клавиш, пока в окне записывается новое сочетание.</summary>
    private const string CaptureReason = "запись сочетания в настройках";
    private const string TestPhrase = "Hello! This is how I sound.";

    private readonly ISettingsStore _settings;
    private readonly AppSettings _app;
    private readonly IHotkeyService _hotkeys;
    private readonly ISpeechService _speech;
    private readonly IHistoryStore _history;
    private readonly ITranslationCache _cache;
    private readonly CloudTranslator _cloud;
    private readonly Autostart _autostart;
    private readonly Action _showSample;
    private readonly ObservableCollection<string> _exclusions = [];

    private bool _loading;
    private HotkeyAction? _capturing;
    private Button? _captureButton;

    public SettingsWindow(ISettingsStore settings, AppSettings app, IHotkeyService hotkeys, ISpeechService speech,
        IHistoryStore history, ITranslationCache cache, CloudTranslator cloud, Autostart autostart, Action showSample)
    {
        _settings = settings;
        _app = app;
        _hotkeys = hotkeys;
        _speech = speech;
        _history = history;
        _cache = cache;
        _cloud = cloud;
        _autostart = autostart;
        _showSample = showSample;
        _loading = true; // при разборе XAML элементы меняют значения и вызывают обработчики, когда соседних ещё нет
        InitializeComponent();

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HoverTranslate.ico"));
        WindowSizing.SizeAndCenter(AppWindow, 780, 860);

        ExclusionsList.ItemsSource = _exclusions;
        LoadState();
        Activated += (_, e) =>
        {
            // Окно потеряло фокус во время записи сочетания — иначе горячие клавиши остались бы приостановленными.
            if (e.WindowActivationState == WindowActivationState.Deactivated) EndCapture();
        };
        Closed += (_, _) =>
        {
            EndCapture();
            _cloudTest?.Cancel();
            _cloud.Usage.Changed -= OnCloudUsageChanged;
        };
        _cloud.Usage.Changed += OnCloudUsageChanged;
    }

    private void OnCloudUsageChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ShowCloudUsage);

    // ------------------------------------------------------------------ загрузка

    private void LoadState()
    {
        _loading = true;
        try
        {
            ShowHotkeys();
            Select(MouseX1Combo, _settings.Get(SettingKeys.MouseX1) ?? MouseButtonHotkeys.DefaultActionValue(MouseButton.X1));
            Select(MouseX2Combo, _settings.Get(SettingKeys.MouseX2) ?? MouseButtonHotkeys.DefaultActionValue(MouseButton.X2));

            FontSlider.Value = _app.PopupFontPercent;
            FontValue.Text = _app.PopupFontPercent + " %";
            OpacitySlider.Value = _app.PopupOpacityPercent;
            OpacityValue.Text = _app.PopupOpacityPercent + " %";
            Select(ThemeCombo, _app.PopupTheme.ToString().ToLowerInvariant());
            Select(AutoCloseCombo, _app.PopupAutoCloseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ExcludeCaptureToggle.IsOn = _app.PopupExcludeFromCapture;

            LoadVoices();
            ShowCloudState();

            FallbackOcrToggle.IsOn = _app.FallbackOcrEnabled && PaddleOcr.ModelsExist(PaddleOcr.ModelDir);
            if (!PaddleOcr.ModelsExist(PaddleOcr.ModelDir))
            {
                FallbackOcrToggle.IsEnabled = false;
                FallbackOcrHint.Text = "Модели запасного распознавания не найдены (data\\models\\ppocr): соберите их скриптом tools\\DataBuilder\\export_ppocr.py.";
            }

            HistoryToggle.IsOn = _app.HistoryEnabled;
            CacheToggle.IsOn = _app.CacheEnabled;
            RefreshStorage();

            AutostartToggle.IsOn = _autostart.IsEnabled;

            _exclusions.Clear();
            foreach (string name in _app.ExcludedProcesses) _exclusions.Add(name);
            UpdateExclusionsEmpty();
        }
        finally
        {
            _loading = false;
        }
    }

    private static void Select(ComboBox combo, string tag)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string?)i.Tag, tag, StringComparison.OrdinalIgnoreCase))
                   ?? combo.Items.OfType<ComboBoxItem>().First();
        combo.SelectedItem = item;
    }

    private static string TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    // ------------------------------------------------------------------ сочетания

    private (SettingKey Key, HotkeyAction Action, Hotkey Default, Button Button, string Title)[] Hotkeys =>
    [
        new(SettingKey.Lookup, HotkeyAction.Lookup, Hotkey.DefaultLookup, LookupKeyButton, "Слово под курсором"),
        new(SettingKey.Area, HotkeyAction.AreaTranslate, Hotkey.DefaultAreaTranslate, AreaKeyButton, "Перевод выделенной области"),
    ];

    private enum SettingKey
    {
        Lookup,
        Area,
    }

    private static string KeyName(SettingKey key) => key == SettingKey.Lookup ? SettingKeys.LookupHotkey : SettingKeys.AreaHotkey;

    /// <summary>Сочетание, назначенное действию: из настроек, а если там нет или оно испорчено — по умолчанию.</summary>
    private Hotkey Assigned(HotkeyAction action) => Hotkeys.First(h => h.Action == action) is var h
        && Hotkey.TryParse(_settings.Get(KeyName(h.Key)), out var parsed) ? parsed.Value : h.Default;

    private void ShowHotkeys()
    {
        var problems = new List<string>();
        foreach (var h in Hotkeys)
        {
            var assigned = Assigned(h.Action);
            h.Button.Content = assigned.ToString();
            if (_hotkeys.GetRegistered(h.Action) != assigned)
                problems.Add($"Сочетание {assigned} («{h.Title}») занято другой программой — выберите другое.");
        }
        SetHotkeyStatus(problems.Count > 0 ? string.Join(" ", problems) : null);
    }

    private void SetHotkeyStatus(string? text)
    {
        HotkeyStatus.Text = text ?? "";
        HotkeyStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        EndCapture();
        var button = (Button)sender;
        _capturing = button == LookupKeyButton ? HotkeyAction.Lookup : HotkeyAction.AreaTranslate;
        _captureButton = button;
        // Пока записываем, свои горячие клавиши отпущены: иначе Alt+Q сработало бы как команда и не дошло бы до окна.
        _hotkeys.SetSuspended(CaptureReason, true);
        button.Content = "Нажмите сочетание…";
        SetHotkeyStatus("Нажмите сочетание клавиш с Ctrl, Alt, Shift или Win. Esc — отмена.");
    }

    private void HotkeyButton_PreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_capturing is not { } action) return;
        e.Handled = true;

        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            return; // ждём основную клавишу

        var mods = HotkeyModifiers.None;
        if (IsDown(VirtualKey.Control)) mods |= HotkeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) mods |= HotkeyModifiers.Alt;
        if (IsDown(VirtualKey.Shift)) mods |= HotkeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= HotkeyModifiers.Win;

        if (key == VirtualKey.Escape && mods == HotkeyModifiers.None)
        {
            EndCapture();
            return;
        }
        TryAssign(action, new Hotkey(mods, (int)key));
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void HotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _captureButton)) EndCapture();
    }

    /// <summary>Проверяет сочетание и, если оно годится, сохраняет; иначе объясняет и ждёт другое.</summary>
    private void TryAssign(HotkeyAction action, Hotkey hotkey)
    {
        var entry = Hotkeys.First(h => h.Action == action);
        var otherEntry = Hotkeys.First(h => h.Action != action);

        string? error;
        if (!Hotkey.TryParse(hotkey.ToString(), out var roundTrip) || roundTrip != hotkey)
            error = "Эту клавишу нельзя использовать: поддерживаются буквы, цифры, F1–F24 и основные клавиши.";
        else
            error = HotkeyValidator.Validate(hotkey, otherEntry.Title, Assigned(otherEntry.Action))
                    ?? (_hotkeys.CanRegister(hotkey) ? null : $"Сочетание {hotkey} уже занято другой программой или Windows.");

        if (error is not null)
        {
            SetHotkeyStatus(error + " Нажмите другое сочетание или Esc.");
            return;
        }

        EndCapture(); // возвращает свои сочетания, затем перерегистрируем изменённое
        _settings.Set(KeyName(entry.Key), hotkey.ToString());
        ShowHotkeys();
        if (_hotkeys.GetRegistered(action) == hotkey) SetHotkeyStatus($"«{entry.Title}»: {hotkey}");
    }

    /// <summary>Завершает запись сочетания (отмена или готово), возвращает приостановленные горячие клавиши.</summary>
    private void EndCapture()
    {
        if (_capturing is null) return;
        _capturing = null;
        _captureButton = null;
        _hotkeys.SetSuspended(CaptureReason, false);
        ShowHotkeys();
    }

    private void MouseX1Combo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetIfUser(SettingKeys.MouseX1, TagOf(MouseX1Combo));

    private void MouseX2Combo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetIfUser(SettingKeys.MouseX2, TagOf(MouseX2Combo));

    private void SetIfUser(string key, string value)
    {
        if (!_loading) _settings.Set(key, value);
    }

    // ------------------------------------------------------------------ карточка

    private void FontSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        FontValue.Text = (int)e.NewValue + " %";
        SetIfUser(SettingKeys.PopupFontPercent, ((int)e.NewValue).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void OpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        OpacityValue.Text = (int)e.NewValue + " %";
        SetIfUser(SettingKeys.PopupOpacityPercent, ((int)e.NewValue).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetIfUser(SettingKeys.PopupTheme, TagOf(ThemeCombo));

    private void AutoCloseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetIfUser(SettingKeys.PopupAutoCloseSeconds, TagOf(AutoCloseCombo));

    private void ExcludeCaptureToggle_Toggled(object sender, RoutedEventArgs e) =>
        SetIfUser(SettingKeys.PopupExcludeFromCapture, ExcludeCaptureToggle.IsOn ? "true" : "false");

    private void SampleButton_Click(object sender, RoutedEventArgs e) => _showSample();

    // ------------------------------------------------------------------ озвучка

    private void LoadVoices()
    {
        var voices = _speech.Voices;
        VoiceCombo.Items.Clear();
        VoiceCombo.Items.Add(new ComboBoxItem { Content = "Автоматически (en-US)", Tag = "" });
        foreach (var v in voices)
            VoiceCombo.Items.Add(new ComboBoxItem { Content = $"{v.DisplayName} ({v.Language})", Tag = v.Id });

        string? saved = _app.SpeechVoice;
        bool missing = saved is not null && voices.All(v => v.Id != saved);
        Select(VoiceCombo, missing ? "" : saved ?? "");

        VoiceCombo.IsEnabled = VoiceTestButton.IsEnabled = voices.Count > 0;
        SpeechSettingsLink.Visibility = voices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        string? status = voices.Count == 0
            ? "В Windows не установлен английский голос. Добавьте язык English (United States) с речью — и откройте окно заново."
            : missing ? "Выбранный ранее голос удалён из системы — используется автоматический." : null;
        VoiceStatus.Text = status ?? "";
        VoiceStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Set(SettingKeys.SpeechVoice, TagOf(VoiceCombo));
        _ = _speech.SpeakAsync(TestPhrase); // сразу слышно, что голос работает
    }

    private void VoiceTestButton_Click(object sender, RoutedEventArgs e) => _ = _speech.SpeakAsync(TestPhrase);

    private void SpeechSettingsLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true });

    // ------------------------------------------------------------------ история и кэш

    private void FallbackOcrToggle_Toggled(object sender, RoutedEventArgs e) =>
        SetIfUser(SettingKeys.FallbackOcrEnabled, FallbackOcrToggle.IsOn ? "true" : "false");

    private void HistoryToggle_Toggled(object sender, RoutedEventArgs e) =>
        SetIfUser(SettingKeys.HistoryEnabled, HistoryToggle.IsOn ? "true" : "false");

    private void CacheToggle_Toggled(object sender, RoutedEventArgs e) =>
        SetIfUser(SettingKeys.CacheEnabled, CacheToggle.IsOn ? "true" : "false");

    private void RefreshStorage()
    {
        long user = DiskUsage.DatabaseSize(AppPaths.UserDbPath), cache = DiskUsage.DatabaseSize(AppPaths.CacheDbPath);
        StorageText.Text = $"История, избранное и настройки (user.db): {DiskUsage.Format(user)}\n" +
                           $"Кэш переводов (cache.db): {DiskUsage.Format(cache)}, предложений: {_cache.Count}";
    }

    private void SetStorageStatus(string text)
    {
        StorageStatus.Text = text;
        StorageStatus.Visibility = Visibility.Visible;
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int count = _history.Count;
            if (count == 0)
            {
                SetStorageStatus("История уже пуста.");
                return;
            }
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Очистить историю?",
                Content = $"Будут удалены все записи истории ({count}). Избранное не затрагивается.",
                PrimaryButtonText = "Очистить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _history.Clear();
            RefreshStorage();
            SetStorageStatus($"История очищена: удалено записей — {count}.");
        }
        catch (Exception ex)
        {
            SetStorageStatus("Не удалось очистить историю: " + ex.Message);
        }
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int count = _cache.Count;
            ClearCacheButton.IsEnabled = false;
            await Task.Run(_cache.Clear);
            RefreshStorage();
            SetStorageStatus(count == 0 ? "Кэш уже пуст." : $"Кэш очищен: удалено предложений — {count}.");
        }
        catch (Exception ex)
        {
            SetStorageStatus("Не удалось очистить кэш: " + ex.Message);
        }
        finally
        {
            ClearCacheButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ облачный перевод

    private CancellationTokenSource? _cloudTest;

    private void ShowCloudState()
    {
        CloudToggle.IsOn = _app.CloudEnabled && _cloud.HasKey;
        CloudKeyState.Text = _cloud.Keys.HasStoredKey ? "Ключ сохранён."
            : _cloud.HasKey ? "Ключ взят из переменной окружения GEMINI_API_KEY (режим разработчика)."
            : "Ключ не задан.";
        CloudDeleteButton.IsEnabled = _cloud.Keys.HasStoredKey;
        CloudModelBox.Text = _app.CloudModel;
        ShowCloudUsage();
    }

    private void ShowCloudUsage() =>
        CloudUsageText.Text = $"Запросов к Gemini сегодня: {_cloud.Usage.Today}, всего: {_cloud.Usage.Total}. " +
                              "Лимит бесплатного уровня задаёт Google; когда он исчерпан, перевод идёт встроенной моделью.";

    private void SetCloudStatus(string text, bool error = false)
    {
        CloudStatus.Text = text;
        CloudStatus.Visibility = Visibility.Visible;
        CloudStatus.Foreground = error
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed)
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private void CloudToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (CloudToggle.IsOn && !_cloud.HasKey)
        {
            SetCloudStatus("Сначала сохраните ключ Gemini.", error: true);
            _loading = true; // возвращаем переключатель без повторного срабатывания
            CloudToggle.IsOn = false;
            _loading = false;
            return;
        }
        _settings.Set(SettingKeys.CloudEnabled, CloudToggle.IsOn ? "true" : "false");
        CloudStatus.Visibility = Visibility.Collapsed;
    }

    private void CloudKeyBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        CloudSaveButton.IsEnabled = CloudKeyBox.Password.Trim().Length > 0;

    private void CloudSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _cloud.Keys.Save(CloudKeyBox.Password);
            CloudKeyBox.Password = "";
            SetCloudStatus("Ключ сохранён. Нажмите «Проверить», чтобы убедиться, что Google его принимает.");
        }
        catch (Exception ex)
        {
            SetCloudStatus("Не удалось сохранить ключ: " + ex.Message, error: true);
        }
        ShowCloudState();
    }

    private async void CloudTest_Click(object sender, RoutedEventArgs e)
    {
        _cloudTest?.Cancel();
        var cts = _cloudTest = new CancellationTokenSource();
        bool typed = CloudKeyBox.Password.Trim().Length > 0;
        CloudTestButton.IsEnabled = false;
        SetCloudStatus("Проверяю ключ…");
        try
        {
            string? problem = await _cloud.TestAsync(typed ? CloudKeyBox.Password : null, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (problem is null)
                SetCloudStatus(typed ? "Ключ рабочий. Нажмите «Сохранить», чтобы запомнить его." : "Ключ рабочий, Gemini отвечает.");
            else
                SetCloudStatus("Не получилось: " + problem + ".", error: true);
        }
        catch (OperationCanceledException)
        {
            // окно закрыто или проверка запущена заново
        }
        catch (Exception ex)
        {
            SetCloudStatus("Не получилось проверить: " + ex.Message, error: true);
        }
        finally
        {
            CloudTestButton.IsEnabled = true;
        }
    }

    private async void CloudDelete_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Удалить ключ?",
            Content = "Ключ Gemini будет удалён с этого компьютера, облачный перевод выключится. Новый ключ можно получить в Google AI Studio.",
            PrimaryButtonText = "Удалить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _cloud.Keys.Delete();
        _settings.Set(SettingKeys.CloudEnabled, "false");
        SetCloudStatus("Ключ удалён.");
        ShowCloudState();
    }

    private void CloudKeyLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(CloudDefaults.KeyPageUrl) { UseShellExecute = true });

    private void CloudModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string name = CloudModelBox.Text.Trim();
        // Пусто или как по умолчанию — храним пустую строку, чтобы при смене умолчания в новой версии оно подхватилось.
        _settings.Set(SettingKeys.CloudModel, name == CloudDefaults.Model ? "" : name);
        CloudModelBox.Text = _app.CloudModel;
    }

    private void CloudModelReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.Set(SettingKeys.CloudModel, "");
        CloudModelBox.Text = _app.CloudModel;
    }

    // ------------------------------------------------------------------ автозапуск

    private void AutostartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            _autostart.SetEnabled(AutostartToggle.IsOn);
            AutostartStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            AutostartStatus.Text = "Не удалось изменить автозапуск: " + ex.Message;
            AutostartStatus.Visibility = Visibility.Visible;
            _loading = true; // возвращаем переключатель без повторного срабатывания
            AutostartToggle.IsOn = _autostart.IsEnabled;
            _loading = false;
        }
    }

    // ------------------------------------------------------------------ исключения

    private void UpdateExclusionsEmpty() =>
        ExclusionsEmpty.Visibility = _exclusions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SaveExclusions()
    {
        _settings.Set(SettingKeys.ExcludedProcesses, ProcessExclusions.Format(_exclusions));
        UpdateExclusionsEmpty();
    }

    private void AddExclusion(string? input)
    {
        string name = ProcessExclusions.Normalize(input);
        if (name.Length == 0 || _exclusions.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        _exclusions.Add(name);
        SaveExclusions();
    }

    private void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        AddExclusion(ExclusionBox.Text);
        ExclusionBox.Text = "";
    }

    private void ExclusionBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        AddExclusion_Click(sender, e);
    }

    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string name) return;
        _exclusions.Remove(name);
        SaveExclusions();
    }

    /// <summary>Хосты окон Windows: в фокусе вместо настоящей программы, исключение отключило бы все приложения Store сразу.</summary>
    private static readonly string[] ShellHosts = ["ApplicationFrameHost", "TextInputHost"];

    /// <summary>Программы с видимыми окнами, которых ещё нет в списке; сама HoverTranslate не предлагается.</summary>
    private void RunningFlyout_Opening(object? sender, object e)
    {
        RunningFlyout.Items.Clear();
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        string self = Process.GetCurrentProcess().ProcessName;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle != 0 && process.MainWindowTitle.Length > 0
                    && !string.Equals(process.ProcessName, self, StringComparison.OrdinalIgnoreCase)
                    && !ShellHosts.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)
                    && !_exclusions.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    names.Add(process.ProcessName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // процесс завершился или недоступен
            }
            finally
            {
                process.Dispose();
            }
        }
        if (names.Count == 0)
        {
            RunningFlyout.Items.Add(new MenuFlyoutItem { Text = "Нет подходящих программ", IsEnabled = false });
            return;
        }
        foreach (string name in names)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => AddExclusion(name);
            RunningFlyout.Items.Add(item);
        }
    }
}
