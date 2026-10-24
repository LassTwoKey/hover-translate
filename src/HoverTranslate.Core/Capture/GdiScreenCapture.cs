using System.Runtime.InteropServices;
using HoverTranslate.Core.Models;
using static HoverTranslate.Core.Native.NativeMethods;
using BITMAPINFOHEADER = HoverTranslate.Core.Native.BITMAPINFOHEADER;

namespace HoverTranslate.Core.Capture;

/// <summary>
/// Захват через BitBlt с экранного DC — запасной вариант. Работает для оконных игр под DWM,
/// но может вернуть чёрный кадр для окон с аппаратным оверлеем или защитой от захвата.
/// Без CAPTUREBLT: курсор и мерцание слоёных окон в снимок не попадают.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    public string Name => "BitBlt";

    public Task<BgraImage> CaptureAsync(ScreenRect region, MonitorInfo monitor, CancellationToken ct) =>
        Task.FromResult(Capture(region));

    public static unsafe BgraImage Capture(ScreenRect region)
    {
        int x = (int)region.X, y = (int)region.Y, w = (int)region.Width, h = (int)region.Height;
        nint screenDc = GetDC(0);
        nint memDc = CreateCompatibleDC(screenDc);
        var bih = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        nint bitmap = CreateDIBSection(screenDc, &bih, DIB_RGB_COLORS, out nint bits, 0, 0);
        try
        {
            if (bitmap == 0) throw new InvalidOperationException("CreateDIBSection не удался");
            nint old = SelectObject(memDc, bitmap);
            bool ok = BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY);
            SelectObject(memDc, old);
            if (!ok) throw new InvalidOperationException($"BitBlt: {Marshal.GetLastPInvokeError()}");
            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new BgraImage(pixels, w, h);
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(memDc);
            _ = ReleaseDC(0, screenDc);
        }
    }
}
