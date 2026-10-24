using HoverTranslate.Core.Native;
using HoverTranslate.Core.Storage;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.Core.Hotkeys;

/// <summary>
/// Программы-исключения: пока одна из них в фокусе, горячие клавиши и боковые кнопки мыши приостановлены
/// и доходят до программы как обычно (например, игра, в которой Alt+Q — свой приём). Список можно менять
/// на ходу; без исключений слежение за фокусом ничего не делает.
/// </summary>
public sealed partial class ExclusionService : IDisposable
{
    public const string SuspendReason = "программа-исключение в фокусе";

    private readonly ForegroundWatcher _watcher;
    private readonly IHotkeyService _hotkeys;
    private readonly MouseButtonHotkeys _mouse;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ExclusionService> _log;
    private volatile string _foreground = "?";

    public ExclusionService(ForegroundWatcher watcher, IHotkeyService hotkeys, MouseButtonHotkeys mouse,
        ISettingsStore settings, ILogger<ExclusionService> log)
    {
        _watcher = watcher;
        _hotkeys = hotkeys;
        _mouse = mouse;
        _settings = settings;
        _log = log;
    }

    /// <summary>Начать слежение (после регистрации сочетаний).</summary>
    public void Start()
    {
        _watcher.Changed += OnForegroundChanged;
        _settings.Changed += OnSettingChanged;
        _watcher.Start();
        _foreground = _watcher.Current;
        Apply();
    }

    private void OnForegroundChanged(object? sender, string process)
    {
        _foreground = process;
        Apply();
    }

    private void OnSettingChanged(object? sender, string key)
    {
        if (key == SettingKeys.ExcludedProcesses) Apply();
    }

    private void Apply()
    {
        try
        {
            bool excluded = ProcessExclusions.Contains(_settings.Get(SettingKeys.ExcludedProcesses), _foreground);
            _mouse.Suspended = excluded;
            _hotkeys.SetSuspended(SuspendReason, excluded);
            if (excluded) LogExcluded(_foreground);
        }
        catch (ObjectDisposedException)
        {
            // служебное окно закрыто при выходе
        }
        catch (Exception ex)
        {
            LogFailed(ex);
        }
    }

    public void Dispose()
    {
        _watcher.Changed -= OnForegroundChanged;
        _settings.Changed -= OnSettingChanged;
    }

    [LoggerMessage(LogLevel.Information, "В фокусе программа-исключение {Process}: горячие клавиши не перехватываются")]
    private partial void LogExcluded(string process);

    [LoggerMessage(LogLevel.Warning, "Не удалось применить список исключений")]
    private partial void LogFailed(Exception ex);
}
