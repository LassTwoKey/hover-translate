using System.Runtime.InteropServices;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Popup;
using static HoverTranslate.Core.Native.NativeMethods;
using BITMAPINFOHEADER = HoverTranslate.Core.Native.BITMAPINFOHEADER;

namespace HoverTranslate.Core.Native;

/// <summary>
/// Стрелка-указатель карточки: крошечное layered-окно с треугольником (сглаженным) в цвет карточки.
/// Отдельное окно, потому что окно WinUI 3 не бывает прозрачным вне скруглённой карточки.
/// Прозрачно для мыши, не активируется; как и карточка, по умолчанию скрыта из снимков экрана.
/// </summary>
public sealed class ArrowWindow : IDisposable
{
    private const string ClassName = "HoverTranslate.Arrow";
    private static bool _classRegistered;

    private readonly MessageWindow _thread;
    private nint _hwnd;

    public ArrowWindow(MessageWindow thread)
    {
        _thread = thread;
        _thread.Invoke(Create);
    }

    /// <param name="color">Цвет (ARGB) — совпадает с фоном карточки.</param>
    public void Show(ScreenRect rect, ArrowSide side, uint color) => _thread.Invoke(() => Render(rect, side, color));

    public void Hide() => _thread.Invoke(() => { if (_hwnd != 0) WindowStyles.Hide(_hwnd); });

    private unsafe void Create()
    {
        nint hInstance = GetModuleHandle(null);
        if (!_classRegistered)
        {
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = hInstance,
                    lpszClassName = name,
                };
                RegisterClassEx(wc);
            }
            _classRegistered = true;
        }
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            ClassName, "", WS_POPUP, 0, 0, 1, 1, 0, 0, hInstance, 0);
    }

    /// <summary>Скрывать ли стрелку из снимков экрана (вместе с карточкой).</summary>
    public void ExcludeFromCapture(bool exclude) => _thread.Invoke(() =>
    {
        if (_hwnd != 0) SetWindowDisplayAffinity(_hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : 0);
    });

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam) =>
        msg == WM_MOUSEACTIVATE ? MA_NOACTIVATE : DefWindowProc(hwnd, msg, wParam, lParam);

    private unsafe void Render(ScreenRect rect, ArrowSide side, uint color)
    {
        if (_hwnd == 0) return;
        int w = Math.Max(2, (int)Math.Round(rect.Width)), h = Math.Max(2, (int)Math.Round(rect.Height));

        nint screenDc = GetDC(0);
        nint memDc = CreateCompatibleDC(screenDc);
        var bih = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        nint bitmap = CreateDIBSection(screenDc, &bih, DIB_RGB_COLORS, out nint bits, 0, 0);
        try
        {
            if (bitmap == 0) return;
            FillTriangle(new Span<uint>((void*)bits, w * h), w, h, side, color);
            nint old = SelectObject(memDc, bitmap);
            var pos = new POINT { X = (int)Math.Round(rect.X), Y = (int)Math.Round(rect.Y) };
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0 /*AC_SRC_OVER*/, SourceConstantAlpha = 255, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
            UpdateLayeredWindow(_hwnd, screenDc, pos, size, memDc, src, 0, blend, ULW_ALPHA);
            SelectObject(memDc, old);
            SetWindowPos(_hwnd, HWND_TOPMOST, pos.X, pos.Y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(memDc);
            _ = ReleaseDC(0, screenDc);
        }
    }

    /// <summary>Треугольник с суперсэмплингом 4×4; пиксели — premultiplied BGRA.</summary>
    internal static void FillTriangle(Span<uint> pixels, int w, int h, ArrowSide side, uint argb)
    {
        double a = ((argb >> 24) & 0xFF) / 255.0;
        double r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        const int S = 4;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < S; sy++)
                {
                    for (int sx = 0; sx < S; sx++)
                    {
                        double px = x + (sx + 0.5) / S, py = y + (sy + 0.5) / S;
                        // Расстояние от вершины по вертикали (0 у вершины, h у основания).
                        double t = side == ArrowSide.Top ? py : h - py;
                        double halfWidth = w / 2.0 * t / h;
                        if (Math.Abs(px - w / 2.0) <= halfWidth) inside++;
                    }
                }
                double cover = a * inside / (S * S);
                pixels[y * w + x] = ((uint)Math.Round(cover * 255) << 24) | ((uint)Math.Round(r * cover) << 16)
                                    | ((uint)Math.Round(g * cover) << 8) | (uint)Math.Round(b * cover);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _thread.Invoke(() =>
            {
                if (_hwnd != 0) DestroyWindow(_hwnd);
                _hwnd = 0;
            });
        }
        catch (ObjectDisposedException)
        {
            // служебное окно уже закрыто, вместе с ним уничтожены и дочерние окна потока
        }
    }
}
