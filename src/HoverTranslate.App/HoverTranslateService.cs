using System.Diagnostics;
using HoverTranslate.App.Popup;
using HoverTranslate.Core;
using HoverTranslate.Core.Hotkeys;
using HoverTranslate.Core.Native;
using HoverTranslate.Core.TextSource;
using HoverTranslate.Core.Translation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.App;

/// <summary>Фоновая часть приложения: значок в трее, горячие клавиши, прогрев UIA/OCR, загрузка модели перевода.</summary>
internal sealed partial class HoverTranslateService(
    MessageWindow window,
    LookupCoordinator coordinator,
    ExclusionService exclusions,
    PopupController popup,
    Core.Dictionary.IDictionaryService dictionary,
    IUiaTextSource uia,
    IOcrTextSource ocr,
    Translator translator,
    IAppShell shell,
    ILogger<HoverTranslateService> log) : IHostedService, IDisposable
{
    private TrayIcon? _tray;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        window.UnhandledException += (_, ex) => LogWindowError(ex);

        _tray = new TrayIcon(window, Path.Combine(AppContext.BaseDirectory, "Assets", "HoverTranslate.ico"), Tooltip());
        _tray.SetMenu(BuildMenu);
        _tray.DoubleClicked += (_, _) => shell.ShowVocabularyWindow();

        coordinator.RegistrationFailed += (_, hotkey) => _tray.ShowBalloon(
            "Горячая клавиша занята",
            $"Сочетание {hotkey} уже использует другая программа. Выберите другое: значок HoverTranslate в трее → Настройки.",
            TrayBalloonKind.Warning);
        coordinator.Completed += (_, outcome) => shell.ShowOutcome(outcome.Capture);
        popup.Start();
        coordinator.HotkeyChanged += (_, _) => _tray?.SetTooltip(Tooltip());
        coordinator.Start();
        exclusions.Start();

        if (!ocr.IsAvailable)
        {
            _tray.ShowBalloon("Не установлено распознавание текста",
                "Для игр и программ без доступа к тексту нужен OCR английского языка: Параметры → Время и язык → Язык → English (United States).",
                TrayBalloonKind.Warning);
        }
        else if (coordinator.RegisteredLookupHotkey is { } hotkey)
        {
            string area = coordinator.RegisteredAreaHotkey is { } a ? $" {a} — перевод выделенной области." : "";
            _tray.ShowBalloon("HoverTranslate запущен", $"Наведите курсор на английское слово и нажмите {hotkey}.{area}");
        }

        translator.StateChanged += (_, state) =>
        {
            if (state == TranslatorState.Unavailable)
                _tray?.ShowBalloon("Перевод предложений недоступен",
                    @"Не удалось загрузить модель перевода (data\models рядом с программой). Словарь работает.", TrayBalloonKind.Warning);
        };

        _ = Task.Run(WarmUpAsync, CancellationToken.None);
        // Флаги разработчика: проверка окон без трея и без движения курсора.
        if (Program.Args.Contains("--demo-popup", StringComparer.OrdinalIgnoreCase))
            ShowSampleCard();
        if (Program.Args.Contains("--demo-area", StringComparer.OrdinalIgnoreCase))
            _ = Task.Run(async () =>
            {
                await translator.StartLoading(); // карточка перевода нужна уже с готовой моделью
                ShowSampleSentence();
            });
        if (Program.Args.Contains("--demo-vocabulary", StringComparer.OrdinalIgnoreCase))
            shell.ShowVocabularyWindow();
        if (Program.Args.Contains("--demo-settings", StringComparer.OrdinalIgnoreCase))
            shell.ShowSettingsWindow();
        LogStarted(coordinator.RegisteredLookupHotkey?.ToString() ?? "—", AppPaths.UserDataDir);
        return Task.CompletedTask;
    }

    /// <summary>Повторный запуск exe: напоминаем, что приложение уже работает.</summary>
    public void NotifyAlreadyRunning() => _tray?.ShowBalloon(
        "HoverTranslate уже запущен", $"Значок — в области уведомлений. {Tooltip()}.");

    private string Tooltip()
    {
        string lookup = coordinator.RegisteredLookupHotkey is { } h ? $"слово {h}" : "горячая клавиша не назначена";
        string area = coordinator.RegisteredAreaHotkey is { } a ? $", область {a}" : "";
        return $"HoverTranslate — {lookup}{area}";
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new("Словарь (история и избранное)", shell.ShowVocabularyWindow),
        new("Настройки…", shell.ShowSettingsWindow),
        TrayMenuItem.Separator,
#if DEBUG
        new("Окно отладки", shell.ShowDebugWindow),
#endif
        new("Открыть папку логов", OpenLogs),
        TrayMenuItem.Separator,
        new("Выход", shell.Quit),
    ];

    private void OpenLogs()
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsDir}\"") { UseShellExecute = false });
    }

    /// <summary>
    /// Пример карточки с настоящей статьёй посреди экрана: закреплён и не пишется в историю. Нужен окну настроек
    /// (изменения вида видны сразу) и флагу разработчика --demo-popup. Вызывается из любого потока.
    /// </summary>
    public void ShowSampleCard()
    {
        const string word = "gave", sentence = "She was running late for the meeting, so she gave up and took a taxi.";
        var matches = dictionary.Lookup(word, sentence, sentence.IndexOf(word, StringComparison.Ordinal));
        var match = matches.Count > 0 ? matches[0] : null;
        var work = Core.Capture.ScreenGeometry.MonitorFromPoint(new Core.Models.ScreenPoint(0, 0)).WorkArea;
        var bounds = new Core.Models.ScreenRect(work.CenterX - 40, work.Y + work.Height * 0.3, 80, 24);
        var hit = new Core.Models.TextHit(word, word, sentence, bounds, Core.Models.TextSourceKind.Uia);
        var capture = new Core.Models.CaptureOutcome(DateTimeOffset.Now, new Core.Models.ScreenPoint((int)bounds.CenterX, (int)bounds.CenterY),
            "demo", hit, TimeSpan.Zero, null, null);
        popup.Show(new Core.Models.LookupOutcome(capture, new Core.Models.LookupResult(word, match?.Lemma ?? word, sentence, null,
            match?.Entry, "demo", Core.Models.TextSourceKind.Uia) { Entries = matches.Select(m => m.Entry).ToList() }), sample: true);
    }

    /// <summary>Пример карточки перевода предложения (как у кнопки мыши 4 / Alt+W) для флага разработчика --demo-area.</summary>
    private void ShowSampleSentence()
    {
        var work = Core.Capture.ScreenGeometry.MonitorFromPoint(new Core.Models.ScreenPoint(0, 0)).WorkArea;
        var anchor = new Core.Models.ScreenRect(work.CenterX - 40, work.Y + work.Height * 0.3, 80, 24);
        popup.ShowArea(new Core.Models.AreaOutcome(DateTimeOffset.Now, anchor,
            "She was running late for the meeting, so she gave up and took a taxi.", TimeSpan.Zero, null, "gave", 31));
    }

    private async Task WarmUpAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            uia.WarmUp();
            await ocr.WarmUpAsync(CancellationToken.None).ConfigureAwait(false);
            LogWarmedUp(sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            LogWarmUpFailed(ex);
        }
        // Модель — после прогрева OCR, чтобы не тормозить первые нажатия; до готовности карточка показывает словарь.
        await translator.StartLoading().ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        exclusions.Dispose();
        popup.Dispose();
        coordinator.Dispose();
        _tray?.Dispose();
        _tray = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _tray?.Dispose();

    [LoggerMessage(LogLevel.Information, "HoverTranslate запущен: горячая клавиша {Hotkey}, данные пользователя в {Dir}")]
    private partial void LogStarted(string hotkey, string dir);

    [LoggerMessage(LogLevel.Information, "Прогрев UIA/OCR: {Ms} мс")]
    private partial void LogWarmedUp(long ms);

    [LoggerMessage(LogLevel.Warning, "Прогрев UIA/OCR не удался")]
    private partial void LogWarmUpFailed(Exception ex);

    [LoggerMessage(LogLevel.Error, "Ошибка в обработчике служебного окна")]
    private partial void LogWindowError(Exception ex);
}
