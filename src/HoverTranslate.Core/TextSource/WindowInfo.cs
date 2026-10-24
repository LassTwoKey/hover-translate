using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Native;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.TextSource;

/// <summary>Как окно под курсором показано на экране.</summary>
public enum WindowMode
{
    Unknown,
    /// <summary>Обычное окно (не закрывает весь монитор).</summary>
    Windowed,
    /// <summary>Развёрнутое окно с рамкой и заголовком.</summary>
    Maximized,
    /// <summary>Без рамки на весь монитор: оконная игра «во весь экран» (borderless).</summary>
    Borderless,
    /// <summary>Полноэкранный Direct3D (exclusive fullscreen).</summary>
    Exclusive,
}

/// <summary>Режим окна под курсором и его размер (для лога и отладочных снимков).</summary>
public readonly record struct WindowModeInfo(WindowMode Mode, int Width, int Height)
{
    public override string ToString() => Mode switch
    {
        WindowMode.Windowed => $"окно {Width}×{Height}",
        WindowMode.Maximized => $"развёрнутое окно {Width}×{Height}",
        WindowMode.Borderless => $"на весь экран без рамки (borderless) {Width}×{Height}",
        WindowMode.Exclusive => $"полноэкранный режим Direct3D (exclusive) {Width}×{Height}",
        _ => "режим окна неизвестен",
    };
}

/// <summary>Сведения об окне под курсором.</summary>
public static class WindowInfo
{
    /// <summary>Окно верхнего уровня под точкой; 0, если окна нет.</summary>
    public static nint RootWindowFromPoint(ScreenPoint point)
    {
        nint hwnd = WindowFromPoint(new POINT { X = point.X, Y = point.Y });
        if (hwnd == 0) return 0;
        nint root = GetAncestor(hwnd, GA_ROOT);
        return root != 0 ? root : hwnd;
    }

    /// <summary>
    /// Режим окна верхнего уровня под точкой. Окно размером с монитор без заголовка — borderless;
    /// exclusive отличается только ответом оболочки (SHQueryUserNotificationState), и то лишь для окна в фокусе.
    /// </summary>
    public static WindowModeInfo DescribeMode(ScreenPoint point)
    {
        nint root = RootWindowFromPoint(point);
        if (root == 0) return default;
        if (!GetWindowRect(root, out var rect)) return default;

        var bounds = ScreenGeometry.MonitorFromPoint(point).Bounds;
        bool coversMonitor = rect.Left <= bounds.X && rect.Top <= bounds.Y && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
        if (!coversMonitor) return new WindowModeInfo(WindowMode.Windowed, rect.Width, rect.Height);

        bool hasCaption = ((long)GetWindowLongPtr(root, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
        if (hasCaption) return new WindowModeInfo(WindowMode.Maximized, rect.Width, rect.Height);

        bool exclusive = GetForegroundWindow() == root
            && SHQueryUserNotificationState(out int state) == 0 && state == 3;
        return new WindowModeInfo(exclusive ? WindowMode.Exclusive : WindowMode.Borderless, rect.Width, rect.Height);
    }

    /// <summary>Имя процесса (без .exe) окна верхнего уровня под точкой; «?», если узнать не удалось.</summary>
    public static string ProcessNameFromPoint(ScreenPoint point) =>
        ProcessNameOfWindow(WindowFromPoint(new POINT { X = point.X, Y = point.Y }));

    /// <summary>Имя процесса (без .exe) окна верхнего уровня, которому принадлежит <paramref name="hwnd"/>; «?», если узнать не удалось.</summary>
    public static unsafe string ProcessNameOfWindow(nint hwnd)
    {
        if (hwnd == 0) return "?";
        nint root = GetAncestor(hwnd, GA_ROOT);
        GetWindowThreadProcessId(root != 0 ? root : hwnd, out uint pid);
        if (pid == 0) return "?";

        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return "?"; // процесс с повышенными правами — без прав администратора не открыть
        try
        {
            char* buffer = stackalloc char[1024];
            uint size = 1024;
            if (!QueryFullProcessImageName(process, 0, buffer, ref size)) return "?";
            return Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)size));
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
