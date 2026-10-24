using System.Runtime.InteropServices;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Native;
using static HoverTranslate.Core.Native.NativeMethods;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace HoverTranslate.Core.Capture;

/// <summary>
/// Захват монитора через Windows.Graphics.Capture: один кадр, затем вырезаем нужную область.
/// Курсор в кадр не попадает (иначе он закрывал бы слово для OCR).
/// </summary>
public sealed partial class GraphicsCaptureService(ILogger<GraphicsCaptureService> log) : IScreenCapture, IDisposable
{
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Lock _deviceLock = new();
    private IDirect3DDevice? _device;
    private bool _borderlessRequested;

    public string Name => "WGC";

    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (COMException) { return false; }
        }
    }

    public async Task<BgraImage> CaptureAsync(ScreenRect region, MonitorInfo monitor, CancellationToken ct)
    {
        await RequestBorderlessOnceAsync().ConfigureAwait(false);
        var item = CreateItem(interop => interop.CreateForMonitor(monitor.Handle, IID_IGraphicsCaptureItem));
        return await GrabAsync(item, region, monitor.Bounds.X, monitor.Bounds.Y, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Снимок через окно <paramref name="hwnd"/>, а не через монитор: запасной путь, когда кадр монитора пуст
    /// (полноэкранная игра). Область — в координатах экрана; то, что за пределами окна, остаётся чёрным.
    /// </summary>
    public async Task<BgraImage> CaptureWindowAsync(nint hwnd, ScreenRect region, CancellationToken ct)
    {
        await RequestBorderlessOnceAsync().ConfigureAwait(false);
        var item = CreateItem(interop => interop.CreateForWindow(hwnd, IID_IGraphicsCaptureItem));
        // Кадр окна начинается в левом верхнем углу видимой рамки окна (без невидимых границ изменения размера).
        var bounds = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0
            ? frame
            : GetWindowRect(hwnd, out var rect) ? rect : throw new InvalidOperationException("Не удалось определить границы окна");
        return await GrabAsync(item, region, bounds.Left, bounds.Top, ct).ConfigureAwait(false);
    }

    /// <summary>Один кадр элемента захвата; из него вырезается <paramref name="region"/> (начало кадра — <paramref name="originX"/>,<paramref name="originY"/> на экране).</summary>
    private async Task<BgraImage> GrabAsync(GraphicsCaptureItem item, ScreenRect region, double originX, double originY, CancellationToken ct)
    {
        var device = GetDevice();

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        try
        {
            // Без жёлтой рамки вокруг монитора (Windows 11, требует разрешения Borderless).
            session.IsBorderRequired = false;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // рамка мелькнёт — не критично
        }

        var frameTcs = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (p, _) =>
        {
            var frame = p.TryGetNextFrame();
            if (frame is not null && !frameTcs.TrySetResult(frame))
                frame.Dispose();
        };
        session.StartCapture();

        using var captured = await frameTcs.Task.WaitAsync(FrameTimeout, ct).ConfigureAwait(false);
        using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(captured.Surface, BitmapAlphaMode.Premultiplied);
        return Crop(bitmap, region, originX, originY);
    }

    private static unsafe BgraImage Crop(SoftwareBitmap bitmap, ScreenRect region, double originX, double originY)
    {
        int x0 = (int)(region.X - originX), y0 = (int)(region.Y - originY);
        int w = Math.Min((int)region.Width, bitmap.PixelWidth - x0);
        int h = Math.Min((int)region.Height, bitmap.PixelHeight - y0);
        if (w <= 0 || h <= 0 || x0 < 0 || y0 < 0) throw new InvalidOperationException("Область захвата вне кадра");

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out uint capacity);

        var pixels = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
        {
            long offset = plane.StartIndex + (long)(y0 + row) * plane.Stride + x0 * 4L;
            if (offset + w * 4L > capacity) break;
            new ReadOnlySpan<byte>(data + offset, w * 4).CopyTo(pixels.AsSpan(row * w * 4, w * 4));
        }
        return new BgraImage(pixels, w, h);
    }

    private async Task RequestBorderlessOnceAsync()
    {
        if (_borderlessRequested) return;
        _borderlessRequested = true;
        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            log.LogInformation("WGC: доступ без рамки — {Status}", status);
        }
        catch (Exception ex)
        {
            log.LogInformation("WGC: запрос захвата без рамки недоступен ({Message})", ex.Message);
        }
    }

    private IDirect3DDevice GetDevice()
    {
        lock (_deviceLock)
        {
            return _device ??= CreateDevice();
        }
    }

    private static IDirect3DDevice CreateDevice()
    {
        const int D3D_DRIVER_TYPE_HARDWARE = 1;
        const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        const uint D3D11_SDK_VERSION = 7;

        Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, D3D_DRIVER_TYPE_HARDWARE, 0, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            0, 0, D3D11_SDK_VERSION, out nint d3dDevice, out _, out nint context));
        try
        {
            Guid iid = IID_IDXGIDevice;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3dDevice, in iid, out nint dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint inspectable));
                try
                {
                    return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        finally
        {
            Marshal.Release(context);
            Marshal.Release(d3dDevice);
        }
    }

    private static GraphicsCaptureItem CreateItem(Func<IGraphicsCaptureItemInterop, nint> create)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        nint itemPtr = create(interop);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    public void Dispose()
    {
        lock (_deviceLock)
        {
            _device?.Dispose();
            _device = null;
        }
    }

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);

        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
