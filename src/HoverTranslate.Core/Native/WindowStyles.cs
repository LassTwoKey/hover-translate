using HoverTranslate.Core.Models;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Native;

/// <summary>Настройка HWND всплывающих окон через Win32 interop.</summary>
public static class WindowStyles
{
    /// <summary>
    /// WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST: окно поверх всех, не в Alt+Tab и панели задач,
    /// не забирает фокус ни при показе, ни при щелчке — игра продолжает получать клавиатуру.
    /// </summary>
    public static void MakeNoActivateTopmost(nint hwnd)
    {
        // Без рамки: WS_DLGFRAME (его оставляет OverlappedPresenter без заголовка) у неактивного окна
        // рисуется светлой полосой по краю.
        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        style = (style & ~(long)(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU)) | WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, (nint)style);

        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)ex);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Скругление углов DWM (Windows 11).</summary>
    public static void SetRoundedCorners(nint hwnd) =>
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND, sizeof(int));

    /// <summary>
    /// Цвет рамки DWM (ARGB, альфа игнорируется). У неактивного окна рамка по умолчанию светлая и
    /// режет глаз на тёмной карточке — красим её в цвет фона.
    /// </summary>
    public static void SetBorderColor(nint hwnd, uint argb)
    {
        int colorref = (int)(((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF)); // 0x00BBGGRR
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, colorref, sizeof(int));
    }

    /// <summary>
    /// Окно не попадает в снимки экрана (WGC/BitBlt) — иначе OCR при следующем нажатии
    /// прочитал бы текст самой карточки.
    /// </summary>
    public static void ExcludeFromCapture(nint hwnd) => ExcludeFromCapture(hwnd, true);

    /// <summary>Включить или выключить скрытие окна из снимков экрана (настройка применяется на ходу).</summary>
    public static void ExcludeFromCapture(nint hwnd, bool exclude) =>
        SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : 0);

    /// <summary>Показать без активации и поставить поверх всех в заданном прямоугольнике (физические px).</summary>
    public static void ShowNoActivate(nint hwnd, ScreenRect rect) =>
        SetWindowPos(hwnd, HWND_TOPMOST, (int)Math.Round(rect.X), (int)Math.Round(rect.Y),
            (int)Math.Round(rect.Width), (int)Math.Round(rect.Height), SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);

    public static void Hide(nint hwnd) =>
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);

    /// <summary>Текущее окно переднего плана — для проверки, что popup не украл фокус.</summary>
    public static nint ForegroundWindow => GetForegroundWindow();
}
