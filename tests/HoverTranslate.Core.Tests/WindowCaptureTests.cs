using System.Runtime.InteropServices;
using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoverTranslate.Core.Tests;

/// <summary>Захват окна через WGC (запасной путь для полноэкранных игр) на любом подходящем окне рабочего стола.</summary>
public sealed class WindowCaptureTests(ITestOutputHelper output)
{
    private delegate bool EnumProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc proc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private static (nint Hwnd, Rect Rect)? FindWindow()
    {
        (nint, Rect)? found = null;
        EnumWindows((hwnd, _) =>
        {
            const int DWMWA_CLOAKED = 14;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left < 400 || r.Bottom - r.Top < 300 || r.Left < 0 || r.Top < 0) return true;
            found = (hwnd, r);
            return false;
        }, 0);
        return found;
    }

    [Fact]
    public async Task CaptureWindow_ReturnsRequestedRegion()
    {
        Assert.SkipUnless(GraphicsCaptureService.IsSupported, "WGC не поддерживается");
        var window = FindWindow();
        Assert.SkipWhen(window is null, "на рабочем столе нет подходящего окна");
        var (hwnd, rect) = window!.Value;

        using var wgc = new GraphicsCaptureService(NullLogger<GraphicsCaptureService>.Instance);
        var region = new ScreenRect(rect.Left + 100, rect.Top + 100, 200, 100);
        var image = await wgc.CaptureWindowAsync(hwnd, region, TestContext.Current.CancellationToken);

        output.WriteLine($"окно {hwnd:X} {rect.Left},{rect.Top} {rect.Right - rect.Left}×{rect.Bottom - rect.Top}; кадр {image.Width}×{image.Height}, пустой: {ImageOps.IsBlank(image)}");
        Assert.Equal(200, image.Width);
        Assert.Equal(100, image.Height);
    }
}
