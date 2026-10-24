using System.Runtime.InteropServices;
using HoverTranslate.Core.Models;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Native;

/// <summary>
/// Низкоуровневые хуки клавиатуры и мыши (WH_KEYBOARD_LL / WH_MOUSE_LL) — ставятся только пока открыт
/// popup и снимаются сразу после закрытия. Это обычный механизм Windows в нашем процессе:
/// без внедрения DLL в чужие процессы. Колбэки — на потоке служебного окна, обработчики должны быть
/// быстрыми (Windows снимает хук, который отвечает дольше LowLevelHooksTimeout).
/// </summary>
public sealed class InputHooks : IDisposable
{
    private const uint VK_ESCAPE = 0x1B;

    private static InputHooks? _active; // хуки глобальны, одновременно активен один набор

    private readonly MessageWindow _window;
    private nint _keyboardHook;
    private nint _mouseHook;
    private bool _swallowEscape;

    /// <summary>Нажата Esc (событие на потоке служебного окна).</summary>
    public event EventHandler? EscapePressed;

    /// <summary>Нажата кнопка мыши (физические координаты экрана). Нажатие не перехватывается.</summary>
    public event EventHandler<ScreenPoint>? MouseDown;

    public InputHooks(MessageWindow window) => _window = window;

    public bool IsInstalled => _keyboardHook != 0 || _mouseHook != 0;

    /// <param name="keyboard">Хук клавиатуры (Esc «съедается», чтобы игра не открыла своё меню).</param>
    /// <param name="mouse">Хук мыши — для закрытия по щелчку вне окна.</param>
    public void Install(bool keyboard, bool mouse) => _window.Invoke(() =>
    {
        UninstallCore();
        _active = this;
        nint hmod = GetModuleHandle(null);
        unsafe
        {
            if (keyboard) _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, &KeyboardProc, hmod, 0);
            if (mouse) _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, &MouseProc, hmod, 0);
        }
        if ((keyboard && _keyboardHook == 0) || (mouse && _mouseHook == 0))
            HookError?.Invoke(this, Marshal.GetLastPInvokeError());
    });

    public void Uninstall() => _window.Invoke(UninstallCore);

    /// <summary>Хук не удалось поставить (код ошибки Win32).</summary>
    public event EventHandler<int>? HookError;

    private void UninstallCore()
    {
        if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = 0;
        _swallowEscape = false;
        if (_active == this) _active = null;
    }

    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int code, nuint wParam, nint lParam)
    {
        var self = _active;
        if (code >= 0 && self is not null)
        {
            unsafe
            {
                var data = (KBDLLHOOKSTRUCT*)lParam;
                if (data->vkCode == VK_ESCAPE)
                {
                    uint msg = (uint)wParam;
                    if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                    {
                        self._swallowEscape = true;
                        self.Raise(self.EscapePressed);
                        return 1;
                    }
                    if (msg is WM_KEYUP or WM_SYSKEYUP && self._swallowEscape)
                    {
                        self._swallowEscape = false;
                        return 1; // отпускание тоже не отдаём — пара должна быть целой
                    }
                }
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static nint MouseProc(int code, nuint wParam, nint lParam)
    {
        var self = _active;
        if (code >= 0 && self is not null && (uint)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
        {
            unsafe
            {
                var data = (MSLLHOOKSTRUCT*)lParam;
                var point = new ScreenPoint(data->pt.X, data->pt.Y);
                try { self.MouseDown?.Invoke(self, point); }
                catch (Exception ex) { self.HandlerError?.Invoke(self, ex); }
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    private void Raise(EventHandler? handler)
    {
        try { handler?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { HandlerError?.Invoke(this, ex); }
    }

    /// <summary>Исключение в обработчике (не должно пересекать границу колбэка хука).</summary>
    public event EventHandler<Exception>? HandlerError;

    public void Dispose()
    {
        try { Uninstall(); }
        catch (ObjectDisposedException) { /* служебное окно уже закрыто */ }
    }
}
