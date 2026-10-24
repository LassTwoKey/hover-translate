using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Models;

namespace HoverTranslate.Core.Tests;

public class ScreenGeometryTests
{
    private static MonitorInfo Monitor(double x, double y, double w, double h, double scale) =>
        new(0, new ScreenRect(x, y, w, h), new ScreenRect(x, y, w, h - 48 * scale), scale);

    [Theory]
    [InlineData(1.0, 800, 240)]
    [InlineData(1.25, 1000, 300)]
    [InlineData(1.5, 1200, 360)]
    [InlineData(2.0, 1600, 480)]
    public void Region_ScalesWithDpi(double scale, double w, double h)
    {
        var m = Monitor(0, 0, 3840, 2160, scale);

        var r = ScreenGeometry.CaptureRegionAround(new ScreenPoint(1920, 1080), m, 800, 240);

        Assert.Equal(new ScreenRect(1920 - w / 2, 1080 - h / 2, w, h), r);
    }

    [Fact]
    public void Region_NearTopLeftCorner_ShiftedInside()
    {
        var r = ScreenGeometry.CaptureRegionAround(new ScreenPoint(5, 5), Monitor(0, 0, 1920, 1080, 1), 800, 240);

        Assert.Equal(new ScreenRect(0, 0, 800, 240), r);
    }

    [Fact]
    public void Region_NearBottomRight_ShiftedInside()
    {
        var r = ScreenGeometry.CaptureRegionAround(new ScreenPoint(1915, 1075), Monitor(0, 0, 1920, 1080, 1), 800, 240);

        Assert.Equal(new ScreenRect(1120, 840, 800, 240), r);
    }

    [Fact]
    public void Region_SecondaryMonitorWithNegativeCoordinates()
    {
        // Монитор слева от основного, масштаб 150 %.
        var m = Monitor(-2880, -200, 2880, 1620, 1.5);

        var r = ScreenGeometry.CaptureRegionAround(new ScreenPoint(-2870, 0), m, 800, 240);

        Assert.Equal(new ScreenRect(-2880, -180, 1200, 360), r);
    }

    [Fact]
    public void Region_LargerThanMonitor_ClampedToMonitor()
    {
        var r = ScreenGeometry.CaptureRegionAround(new ScreenPoint(300, 200), Monitor(0, 0, 640, 480, 2), 800, 240);

        Assert.Equal(new ScreenRect(0, 0, 640, 480), r);
    }

    [Fact]
    public void ScreenRect_DistanceTo()
    {
        var r = new ScreenRect(10, 10, 20, 10);
        Assert.Equal(0, r.DistanceTo(15, 15));
        Assert.Equal(5, r.DistanceTo(35, 15));
        Assert.Equal(5, r.DistanceTo(15, 5));
        Assert.Equal(5, r.DistanceTo(33, 24), 6);
    }
}
