using System.Runtime.InteropServices;

namespace HoverTranslate.Core.Native;

#pragma warning disable CA1707, CA1712, CA1401 // имена Win32 сохраняются как в SDK

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PAINTSTRUCT
{
    public nint hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    public fixed byte rgbReserved[32];
}

/// <summary>Рисование GDI и мышь — для окна выделения области.</summary>
internal static partial class NativeMethods
{
    public const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020;
    public const uint WM_MOUSEMOVE = 0x0200, WM_CAPTURECHANGED = 0x0215;
    public const int IDC_CROSS = 32515;
    public const uint LWA_COLORKEY = 0x1, LWA_ALPHA = 0x2;
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const int TRANSPARENT = 1;
    public const uint DT_CENTER = 0x1, DT_SINGLELINE = 0x20, DT_VCENTER = 0x4, DT_NOPREFIX = 0x800;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint SetCapture(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint cursorName);

    [LibraryImport("user32.dll")]
    public static partial nint SetCursor(nint cursor);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint hwnd, out PAINTSTRUCT paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, in PAINTSTRUCT paint);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(nint hdc, in RECT rect, nint brush);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(nint hdc, string text, int length, ref RECT rect, uint format);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(uint colorref);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextColor(nint hdc, uint colorref);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);
}
