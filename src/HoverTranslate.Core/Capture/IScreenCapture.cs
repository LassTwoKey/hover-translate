using HoverTranslate.Core.Models;

namespace HoverTranslate.Core.Capture;

/// <summary>Снимок области экрана: BGRA, 4 байта на пиксель, без отступов между строками.</summary>
public sealed class BgraImage(byte[] pixels, int width, int height)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride => Width * 4;
}

public interface IScreenCapture
{
    string Name { get; }

    /// <summary>Снимает область <paramref name="region"/> (физические пиксели), лежащую внутри <paramref name="monitor"/>.</summary>
    Task<BgraImage> CaptureAsync(ScreenRect region, MonitorInfo monitor, CancellationToken ct);
}
