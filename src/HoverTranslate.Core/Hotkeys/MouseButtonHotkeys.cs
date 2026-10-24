using System.Diagnostics;
using System.Runtime.InteropServices;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Native;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Hotkeys;

/// <summary>Боковые кнопки мыши: X1 — «назад» (кнопка 4), X2 — «вперёд» (кнопка 5).</summary>
public enum MouseButton
{
    X1 = 1,
    X2 = 2,
}

/// <summary>
/// Действия на боковых кнопках мыши. RegisterHotKey мышь не поддерживает, поэтому — постоянный хук
/// WH_MOUSE_LL (в нашем процессе, без внедрения DLL), который ставится только если назначена хотя бы
/// одна кнопка. Назначенная кнопка «съедается» (нажатие и отпускание): иначе браузер ушёл бы назад,
/// а игра получила бы лишнее действие. Колбэк — на потоке служебного окна, обработчик должен быть быстрым.
/// </summary>
public sealed class MouseButtonHotkeys : IDisposable
{
    private const uint WM_XBUTTONUP = 0x020C;
    private const uint LLMHF_INJECTED = 0x1;

    private static MouseButtonHotkeys? _active;

    private readonly MessageWindow _window;
    private nint _hook;
    private HotkeyAction? _x1, _x2;
    private volatile bool _suspended;

    /// <summary>Нажата назначенная кнопка (на потоке служебного окна).</summary>
    public event EventHandler<HotkeyPressedEventArgs>? Pressed;

    /// <summary>Хук не удалось поставить (код ошибки Win32).</summary>
    public event EventHandler<int>? HookError;

    public event EventHandler<Exception>? HandlerError;

    public MouseButtonHotkeys(MessageWindow window) => _window = window;

    /// <summary>Назначить действия кнопкам (null — кнопка свободна). Без назначений хук снимается.</summary>
    public void SetBindings(HotkeyAction? x1, HotkeyAction? x2) => _window.Invoke(() =>
    {
        _x1 = x1;
        _x2 = x2;
        bool needed = x1 is not null || x2 is not null;
        if (needed && _hook == 0)
        {
            _active = this;
            unsafe
            {
                _hook = SetWindowsHookEx(WH_MOUSE_LL, &MouseProc, GetModuleHandle(null), 0);
            }
            if (_hook == 0) HookError?.Invoke(this, Marshal.GetLastPInvokeError());
        }
        else if (!needed)
        {
            Uninstall();
        }
    });

    /// <summary>
    /// Пока true, кнопки не перехватываются и доходят до программы как обычно (в фокусе программа-исключение).
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set => _suspended = value;
    }

    public HotkeyAction? GetBinding(MouseButton button) => button == MouseButton.X1 ? _x1 : _x2;

    /// <summary>Действие кнопки, пока оно не выбрано в настройках: 4 — предложение, 5 — слово.</summary>
    public static string DefaultActionValue(MouseButton button) => button == MouseButton.X1 ? "sentence" : "word";

    private void Uninstall()
    {
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        _hook = 0;
        if (_active == this) _active = null;
    }

    [UnmanagedCallersOnly]
    private static nint MouseProc(int code, nuint wParam, nint lParam)
    {
        var self = _active;
        uint msg = (uint)wParam;
        if (code >= 0 && self is not null && !self._suspended && msg is WM_XBUTTONDOWN or WM_XBUTTONUP)
        {
            unsafe
            {
                var data = (MSLLHOOKSTRUCT*)lParam;
                // Старшее слово mouseData: 1 — XBUTTON1, 2 — XBUTTON2. Синтетические нажатия не трогаем.
                var action = (data->mouseData >> 16) switch
                {
                    1 => self._x1,
                    2 => self._x2,
                    _ => null,
                };
                if (action is { } a && (data->flags & LLMHF_INJECTED) == 0)
                {
                    if (msg == WM_XBUTTONDOWN)
                    {
                        var args = new HotkeyPressedEventArgs(a, new ScreenPoint(data->pt.X, data->pt.Y), Stopwatch.GetTimestamp());
                        try { self.Pressed?.Invoke(self, args); }
                        catch (Exception ex) { self.HandlerError?.Invoke(self, ex); }
                    }
                    return 1; // не отдаём ни нажатие, ни отпускание
                }
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public void Dispose()
    {
        try
        {
            _window.Invoke(Uninstall);
        }
        catch (ObjectDisposedException)
        {
            // служебное окно уже закрыто — хук снят вместе с потоком
        }
    }

    /// <summary>Значение настройки: "word" | "sentence" | "area" | "none".</summary>
    public static HotkeyAction? ParseAction(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "word" => HotkeyAction.Lookup,
        "sentence" => HotkeyAction.Sentence,
        "area" => HotkeyAction.AreaTranslate,
        _ => null,
    };
}
