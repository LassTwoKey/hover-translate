using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Native;

/// <summary>
/// Подмена оконной процедуры всплывающего окна:
///   • WM_NCCALCSIZE -> 0: неклиентской области нет вовсе. OverlappedPresenter без заголовка при каждом
///     показе возвращает WS_DLGFRAME, и у неактивного окна эта рамка рисуется светлой полосой;
///   • WM_NCPAINT / WM_NCACTIVATE — рамка не рисуется (классическая объёмная рамка диалога: белые и серые линии);
///   • WM_STYLECHANGING — рамочные стили (WS_DLGFRAME и др.) не возвращаются;
///   • WM_MOUSEACTIVATE -> MA_NOACTIVATE: щелчок по карточке не активирует окно (вдобавок к WS_EX_NOACTIVATE).
/// Остальные сообщения уходят исходной процедуре WinUI.
/// </summary>
public static partial class NoFrameSubclass
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCPAINT = 0x0085;
    private const uint WM_NCACTIVATE = 0x0086;
    private const uint WM_STYLECHANGING = 0x007C;
    private const uint FrameStyles = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU;

    private static readonly ConcurrentDictionary<nint, nint> Originals = new();

    public static unsafe void Attach(nint hwnd)
    {
        if (Originals.ContainsKey(hwnd)) return;
        delegate* unmanaged<nint, uint, nuint, nint, nint> proc = &WndProc;
        nint original = SetWindowLongPtr(hwnd, GWLP_WNDPROC, (nint)proc);
        Originals[hwnd] = original;
        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)((style & ~(long)FrameStyles) | WS_POPUP));
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | 0x4 /*SWP_NOZORDER*/);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (!Originals.TryGetValue(hwnd, out nint original))
            return DefWindowProc(hwnd, msg, wParam, lParam);
        switch (msg)
        {
            case WM_NCCALCSIZE when wParam != 0:
                return 0; // клиентская область = всё окно
            case WM_NCPAINT:
                return 0; // иначе DefWindowProc рисует объёмную рамку WS_DLGFRAME поверх краёв карточки
            case WM_NCACTIVATE:
                return DefWindowProc(hwnd, msg, wParam, -1); // -1: не перерисовывать рамку
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_STYLECHANGING when (int)wParam == GWL_STYLE:
                unsafe
                {
                    // STYLESTRUCT { styleOld, styleNew }: WinUI при показе возвращает WS_DLGFRAME — не даём.
                    var styles = (uint*)lParam;
                    styles[1] = (styles[1] & ~FrameStyles) | WS_POPUP;
                }
                break;
            case WM_DESTROY:
                Originals.TryRemove(hwnd, out _);
                break;
        }
        return CallWindowProc(original, hwnd, msg, wParam, lParam);
    }

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial nint CallWindowProc(nint prev, nint hwnd, uint msg, nuint wParam, nint lParam);
}
