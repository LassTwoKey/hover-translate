using HoverTranslate.Core.Models;
using HoverTranslate.Core.Native;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Capture;

/// <summary>Монитор: границы и рабочая область в физических пикселях, масштаб (1.0 = 96 DPI).</summary>
public readonly record struct MonitorInfo(nint Handle, ScreenRect Bounds, ScreenRect WorkArea, double Scale);

/// <summary>Геометрия экрана с учётом Per-Monitor DPI v2: все координаты — физические пиксели.</summary>
public static class ScreenGeometry
{
    public static MonitorInfo MonitorFromPoint(ScreenPoint point)
    {
        nint hmon = NativeMethods.MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hmon, ref mi);
        double scale = GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        return new MonitorInfo(hmon, ToRect(mi.rcMonitor), ToRect(mi.rcWork), scale);
    }

    /// <summary>
    /// Область захвата вокруг точки: <paramref name="logicalWidth"/>×<paramref name="logicalHeight"/>
    /// логических пикселей (пересчёт по DPI монитора), центр — курсор. У края монитора область
    /// сдвигается внутрь, а не обрезается, чтобы сохранить контекст.
    /// </summary>
    public static ScreenRect CaptureRegionAround(ScreenPoint point, MonitorInfo monitor, double logicalWidth, double logicalHeight)
    {
        var b = monitor.Bounds;
        double w = Math.Min(Math.Round(logicalWidth * monitor.Scale), b.Width);
        double h = Math.Min(Math.Round(logicalHeight * monitor.Scale), b.Height);
        double x = Math.Clamp(point.X - Math.Floor(w / 2), b.X, b.Right - w);
        double y = Math.Clamp(point.Y - Math.Floor(h / 2), b.Y, b.Bottom - h);
        return new ScreenRect(x, y, w, h);
    }

    private static ScreenRect ToRect(RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}
