using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Models;
using Microsoft.UI.Windowing;

namespace HoverTranslate.App;

internal static class WindowSizing
{
    /// <summary>
    /// Размер окна в логических px (с учётом масштаба монитора), не больше 90 % рабочей области, по центру этой области.
    /// </summary>
    public static void SizeAndCenter(AppWindow window, double width, double height)
    {
        var pos = window.Position;
        var monitor = ScreenGeometry.MonitorFromPoint(new ScreenPoint(pos.X + 10, pos.Y + 10));
        var work = monitor.WorkArea;
        int w = (int)Math.Min(width * monitor.Scale, work.Width * 0.9);
        int h = (int)Math.Min(height * monitor.Scale, work.Height * 0.9);
        window.MoveAndResize(new Windows.Graphics.RectInt32((int)(work.CenterX - w / 2.0), (int)(work.CenterY - h / 2.0), w, h));
    }
}
