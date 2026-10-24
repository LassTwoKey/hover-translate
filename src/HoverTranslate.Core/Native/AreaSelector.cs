using System.Runtime.InteropServices;
using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Models;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Native;

/// <summary>
/// Выделение прямоугольника мышью для перевода области (Alt+W): полупрозрачное затемнение поверх всех
/// мониторов с «окном» в выделенной области. Окно не активируется — игра остаётся на переднем плане,
/// Esc ловится LL-хуком (только пока идёт выделение). Правая кнопка тоже отменяет.
/// Окно исключено из снимков экрана, чтобы OCR не увидел затемнение.
/// </summary>
public sealed class AreaSelector : IDisposable
{
    private const string ClassName = "HoverTranslate.AreaSelector";
    private const byte DimAlpha = 110;
    private const uint KeyColor = 0x00FF00FF;   // пурпурный — «дыра» на месте выделения (LWA_COLORKEY)
    private const uint DimColor = 0x00000000;
    private const uint BorderColor = 0x00FFFFFF;
    private const int MinSize = 8;
    private const string Hint = "Выделите область для перевода  ·  Esc — отмена";

    private static AreaSelector? _instance; // оконная процедура статическая; окно одно на процесс
    private static bool _classRegistered;

    private readonly MessageWindow _thread;
    private readonly InputHooks _hooks;
    private nint _hwnd;
    private nint _dimBrush, _keyBrush, _borderBrush, _font;
    private ScreenRect _virtual;
    private ScreenPoint _start, _current;
    private bool _dragging;
    private TaskCompletionSource<ScreenRect?>? _pending;

    public AreaSelector(MessageWindow thread)
    {
        _thread = thread;
        _hooks = new InputHooks(thread);
        _hooks.EscapePressed += (_, _) => Finish(null); // колбэк хука — уже на потоке окна
        _thread.Invoke(Create);
    }

    /// <summary>Идёт выделение.</summary>
    public bool IsActive => _pending is not null;

    /// <summary>
    /// Показать затемнение и дождаться выделения. null — отмена (Esc, правая кнопка, слишком маленькая рамка,
    /// повторный вызов). Ответ приходит на потоке служебного окна.
    /// </summary>
    public Task<ScreenRect?> SelectAsync() => _thread.InvokeAsync(() =>
    {
        Finish(null); // предыдущее выделение, если было
        var tcs = new TaskCompletionSource<ScreenRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        _dragging = false;
        _virtual = new ScreenRect(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));
        CreateFontForCursorMonitor();
        ClipCursor(0); // игра могла запереть курсор в окне: на время выделения он должен двигаться свободно
        WindowStyles.ShowNoActivate(_hwnd, _virtual);
        InvalidateRect(_hwnd, 0, false);
        _hooks.Install(keyboard: true, mouse: false);
        return tcs.Task;
    }).Unwrap();

    /// <summary>Отменить выделение (повторное нажатие горячей клавиши).</summary>
    public void Cancel() => _thread.Invoke(() => Finish(null));

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
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            ClassName, "HoverTranslate — выделение области", WS_POPUP, 0, 0, 1, 1, 0, 0, hInstance, 0);
        SetLayeredWindowAttributes(_hwnd, KeyColor, DimAlpha, LWA_COLORKEY | LWA_ALPHA);
        SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
        _dimBrush = CreateSolidBrush(DimColor);
        _keyBrush = CreateSolidBrush(KeyColor);
        _borderBrush = CreateSolidBrush(BorderColor);
        _instance = this;
    }

    private void CreateFontForCursorMonitor()
    {
        GetCursorPos(out var pt);
        double scale = ScreenGeometry.MonitorFromPoint(new ScreenPoint(pt.X, pt.Y)).Scale;
        if (_font != 0) DeleteObject(_font);
        _font = CreateFont(-(int)Math.Round(20 * scale), 0, 0, 0, 600, 0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0,
            5 /*CLEARTYPE_QUALITY*/, 0, "Segoe UI");
    }

    private void Finish(ScreenRect? result)
    {
        if (_pending is not { } pending) return;
        _pending = null;
        _dragging = false;
        _hooks.Uninstall();
        ReleaseCapture();
        WindowStyles.Hide(_hwnd); // скрыть до захвата экрана
        pending.TrySetResult(result);
    }

    private ScreenRect Selection()
    {
        double x = Math.Min(_start.X, _current.X), y = Math.Min(_start.Y, _current.Y);
        return new ScreenRect(x, y, Math.Abs(_current.X - _start.X), Math.Abs(_current.Y - _start.Y));
    }

    private static ScreenPoint Cursor()
    {
        GetCursorPos(out var pt);
        return new ScreenPoint(pt.X, pt.Y);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        var self = _instance;
        if (self is null || hwnd != self._hwnd) return DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            return self.Handle(msg, wParam, lParam);
        }
        catch (Exception)
        {
            self.Finish(null); // исключение не должно пересечь границу оконной процедуры
            return 0;
        }
    }

    private nint Handle(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_SETCURSOR:
                SetCursor(LoadCursor(0, IDC_CROSS));
                return 1;
            case WM_LBUTTONDOWN when _pending is not null:
                _start = _current = Cursor();
                _dragging = true;
                SetCapture(_hwnd);
                InvalidateRect(_hwnd, 0, false);
                return 0;
            case WM_MOUSEMOVE when _dragging:
                _current = Cursor();
                InvalidateRect(_hwnd, 0, false);
                return 0;
            case WM_LBUTTONUP when _dragging:
                _current = Cursor();
                var rect = Selection();
                Finish(rect.Width >= MinSize && rect.Height >= MinSize ? rect : null);
                return 0;
            case WM_RBUTTONDOWN:
                Finish(null);
                return 0;
            case WM_CAPTURECHANGED when _dragging && lParam != _hwnd:
                Finish(null); // захват мыши отобрали (например, Ctrl+Alt+Del)
                return 0;
            case WM_ERASEBKGND:
                return 1;
            case WM_PAINT:
                Paint();
                return 0;
            default:
                return DefWindowProc(_hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>Затемнение, «дыра» и рамка выделения, подсказка сверху — с двойной буферизацией.</summary>
    private void Paint()
    {
        nint hdc = BeginPaint(_hwnd, out var ps);
        try
        {
            int w = (int)_virtual.Width, h = (int)_virtual.Height;
            nint mem = CreateCompatibleDC(hdc);
            nint bitmap = CreateCompatibleBitmap(hdc, w, h);
            nint oldBitmap = SelectObject(mem, bitmap);
            try
            {
                FillRect(mem, new RECT { Right = w, Bottom = h }, _dimBrush);
                if (_dragging)
                {
                    var s = Selection();
                    int left = (int)(s.X - _virtual.X), top = (int)(s.Y - _virtual.Y);
                    int right = left + (int)s.Width, bottom = top + (int)s.Height;
                    const int b = 2;
                    FillRect(mem, new RECT { Left = left - b, Top = top - b, Right = right + b, Bottom = bottom + b }, _borderBrush);
                    FillRect(mem, new RECT { Left = left, Top = top, Right = right, Bottom = bottom }, _keyBrush);
                }
                else
                {
                    DrawHint(mem);
                }
                BitBlt(hdc, 0, 0, w, h, mem, 0, 0, SRCCOPY);
            }
            finally
            {
                SelectObject(mem, oldBitmap);
                DeleteObject(bitmap);
                DeleteDC(mem);
            }
        }
        finally
        {
            EndPaint(_hwnd, ps);
        }
    }

    private void DrawHint(nint hdc)
    {
        var monitor = ScreenGeometry.MonitorFromPoint(Cursor());
        var area = new RECT
        {
            Left = (int)(monitor.Bounds.X - _virtual.X),
            Top = (int)(monitor.Bounds.Y - _virtual.Y + 40 * monitor.Scale),
            Right = (int)(monitor.Bounds.Right - _virtual.X),
            Bottom = (int)(monitor.Bounds.Y - _virtual.Y + 90 * monitor.Scale),
        };
        nint oldFont = SelectObject(hdc, _font);
        SetBkMode(hdc, TRANSPARENT);
        SetTextColor(hdc, BorderColor);
        DrawText(hdc, Hint, -1, ref area, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(hdc, oldFont);
    }

    public void Dispose()
    {
        try
        {
            _thread.Invoke(() =>
            {
                Finish(null);
                if (_hwnd != 0) DestroyWindow(_hwnd);
                _hwnd = 0;
                foreach (nint gdi in new[] { _dimBrush, _keyBrush, _borderBrush, _font })
                    if (gdi != 0) DeleteObject(gdi);
                if (_instance == this) _instance = null;
            });
        }
        catch (ObjectDisposedException)
        {
            // служебное окно уже закрыто
        }
        _hooks.Dispose();
    }
}
