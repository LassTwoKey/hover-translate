using HoverTranslate.App.Settings;
using HoverTranslate.App.Vocabulary;
using HoverTranslate.Core;
using HoverTranslate.Core.Hotkeys;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Speech;
using HoverTranslate.Core.Storage;
using HoverTranslate.Core.Translation;
using HoverTranslate.Core.Translation.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HoverTranslate.App;

/// <summary>Приложение в трее: без главного окна, окна открываются по требованию.</summary>
public partial class App : Application, IAppShell
{
    private readonly DispatcherQueue _dispatcher;
    private readonly List<CaptureOutcome> _recent = [];
    private IHost? _host;
    private ILogger<App>? _log;
    private DebugWindow? _debugWindow;
    private VocabularyWindow? _vocabularyWindow;
    private SettingsWindow? _settingsWindow;
    private bool _quitting;

    public App()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        // Нет главного окна — приложение живёт до явного выхода из меню трея.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        UnhandledException += (_, e) =>
        {
            _log?.LogCritical(e.Exception, "Необработанное исключение в UI-потоке");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _log?.LogCritical(e.ExceptionObject as Exception, "Необработанное исключение");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Ненаблюдаемое исключение в задаче");
            e.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _host = AppHost.Build(Program.Args, this);
            _log = _host.Services.GetRequiredService<ILogger<App>>();
            await _host.StartAsync();
            var service = _host.Services.GetServices<IHostedService>().OfType<HoverTranslateService>().Single();
            Program.Instance?.Listen(
                onActivate: service.NotifyAlreadyRunning,
                onQuit: Quit);
        }
        catch (Exception ex)
        {
            _log?.LogCritical(ex, "Не удалось запустить приложение");
            Program.Fatal("Не удалось запустить HoverTranslate:\n" + ex.Message);
            Exit();
        }
    }

    public void ShowDebugWindow() => OnUi(() =>
    {
        if (_debugWindow is null)
        {
            _debugWindow = new DebugWindow(_recent);
            _debugWindow.Closed += (_, _) => _debugWindow = null;
        }
        _debugWindow.Activate();
    });

    public void ShowVocabularyWindow() => OnUi(() =>
    {
        if (_vocabularyWindow is null)
        {
            var sp = _host!.Services;
            _vocabularyWindow = new VocabularyWindow(
                sp.GetRequiredService<IHistoryStore>(), sp.GetRequiredService<IFavoritesStore>(),
                sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<ISpeechService>());
            _vocabularyWindow.Closed += (_, _) => _vocabularyWindow = null;
        }
        _vocabularyWindow.Activate();
    });

    public void ShowSettingsWindow() => OnUi(() =>
    {
        if (_settingsWindow is null)
        {
            var sp = _host!.Services;
            var service = sp.GetServices<IHostedService>().OfType<HoverTranslateService>().Single();
            _settingsWindow = new SettingsWindow(
                sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<IHotkeyService>(),
                sp.GetRequiredService<ISpeechService>(), sp.GetRequiredService<IHistoryStore>(), sp.GetRequiredService<ITranslationCache>(),
                sp.GetRequiredService<CloudTranslator>(), sp.GetRequiredService<Autostart>(), service.ShowSampleCard);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    });

    public void ShowOutcome(CaptureOutcome outcome) => OnUi(() =>
    {
        _recent.Insert(0, outcome);
        if (_recent.Count > DebugWindow.MaxRows) _recent.RemoveAt(_recent.Count - 1);
        // Окно не активируется: фокус остаётся у приложения под курсором.
        _debugWindow?.Add(outcome);
    });

    public void Quit() => OnUi(async () =>
    {
        if (_quitting) return;
        _quitting = true;
        _debugWindow?.Close();
        _vocabularyWindow?.Close();
        _settingsWindow?.Close();
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Ошибка при остановке");
            }
            _host.Dispose();
        }
        Exit();
    });

    private void OnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }
}
