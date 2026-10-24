using System.Runtime.InteropServices;
using HoverTranslate.Core.TextSource;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Native;

/// <summary>
/// Следит за сменой окна в фокусе через SetWinEventHook(EVENT_SYSTEM_FOREGROUND) — хук работает внутри
/// нашего процесса (WINEVENT_OUTOFCONTEXT), ничего в чужие процессы не внедряется. Колбэк приходит
/// на потоке служебного окна.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private static ForegroundWatcher? _active;

    private readonly MessageWindow _window;
    private nint _hook;

    /// <summary>Имя процесса нового окна в фокусе («?», если не удалось определить). На потоке служебного окна.</summary>
    public event EventHandler<string>? Changed;

    public ForegroundWatcher(MessageWindow window) => _window = window;

    /// <summary>Имя процесса окна, которое в фокусе сейчас.</summary>
    public string Current => WindowInfo.ProcessNameOfWindow(GetForegroundWindow());

    public void Start() => _window.Invoke(() =>
    {
        if (_hook != 0) return;
        _active = this;
        unsafe
        {
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &WinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
    });

    private void Stop()
    {
        if (_hook != 0) UnhookWinEvent(_hook);
        _hook = 0;
        if (_active == this) _active = null;
    }

    [UnmanagedCallersOnly]
    private static void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            var self = _active;
            if (self is null || hwnd == 0) return;
            self.Changed?.Invoke(self, WindowInfo.ProcessNameOfWindow(hwnd));
        }
        catch (Exception)
        {
            // исключение нельзя пропускать через неуправляемый вызов; сбой слежения не должен ронять процесс
        }
    }

    public void Dispose()
    {
        try
        {
            _window.Invoke(Stop);
        }
        catch (ObjectDisposedException)
        {
            // служебное окно уже закрыто — хук снят вместе с потоком
        }
    }
}
