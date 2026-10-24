using System.Runtime.InteropServices;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Native;
using Microsoft.Extensions.Logging;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Hotkeys;

/// <summary>Действие, привязанное к горячей клавише.</summary>
public enum HotkeyAction
{
    Lookup = 1,
    AreaTranslate = 2,
    /// <summary>Перевод предложения под курсором (без словарной статьи).</summary>
    Sentence = 3,
}

/// <summary>Нажатие горячей клавиши: позиция курсора снимается сразу в WM_HOTKEY, до любой обработки.</summary>
public sealed record HotkeyPressedEventArgs(HotkeyAction Action, ScreenPoint Cursor, long Timestamp);

public interface IHotkeyService
{
    /// <summary>Регистрирует (или перерегистрирует) сочетание. false — сочетание занято другой программой.</summary>
    bool Register(HotkeyAction action, Hotkey hotkey);

    void Unregister(HotkeyAction action);

    /// <summary>Назначенное сочетание (во время приостановки — то, что вернётся после неё).</summary>
    Hotkey? GetRegistered(HotkeyAction action);

    /// <summary>
    /// Приостанавливает все сочетания, пока есть хотя бы одна причина: клавиши возвращаются приложениям.
    /// Причины независимы: «в фокусе программа-исключение» и «в настройках записывается сочетание».
    /// </summary>
    void SetSuspended(string reason, bool suspended);

    /// <summary>Свободно ли сочетание для регистрации. Собственные сочетания считаются свободными.</summary>
    bool CanRegister(Hotkey hotkey);

    /// <summary>Вызывается на потоке служебного окна — обработчик должен быстро вернуть управление.</summary>
    event EventHandler<HotkeyPressedEventArgs>? Pressed;
}

/// <summary>Глобальные горячие клавиши через RegisterHotKey (без низкоуровневых хуков).</summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    /// <summary>Идентификатор для пробной регистрации в <see cref="CanRegister"/>; не пересекается с <see cref="HotkeyAction"/>.</summary>
    private const int ProbeId = 0x7FF0;

    private readonly MessageWindow _window;
    private readonly ILogger<HotkeyService> _log;
    private readonly Dictionary<HotkeyAction, Hotkey> _desired = [];   // что назначено
    private readonly HashSet<HotkeyAction> _active = [];                // что сейчас зарегистрировано в системе
    private readonly HashSet<string> _suspendReasons = [];

    public event EventHandler<HotkeyPressedEventArgs>? Pressed;

    public HotkeyService(MessageWindow window, ILogger<HotkeyService> log)
    {
        _window = window;
        _log = log;
        _window.AddHandler(OnMessage);
    }

    private bool IsSuspended => _suspendReasons.Count > 0;

    public bool Register(HotkeyAction action, Hotkey hotkey) => _window.Invoke(() =>
    {
        _desired.TryGetValue(action, out var old);
        bool hadOld = _desired.ContainsKey(action);
        if (hadOld && old == hotkey && (IsSuspended || _active.Contains(action))) return true;
        if (IsSuspended)
        {
            _desired[action] = hotkey; // зарегистрируется при возобновлении
            return true;
        }

        if (_active.Remove(action)) UnregisterHotKey(_window.Handle, (int)action);
        if (!RegisterHotKey(_window.Handle, (int)action, (uint)hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.VirtualKey))
        {
            int err = Marshal.GetLastPInvokeError();
            _log.LogWarning("Не удалось зарегистрировать {Hotkey} для {Action}: ошибка {Error}{Hint}", hotkey, action, err,
                err == ErrorHotkeyAlreadyRegistered ? " (сочетание занято другой программой)" : "");
            // Возвращаем прежнее сочетание, чтобы не остаться без горячей клавиши.
            if (hadOld && RegisterHotKey(_window.Handle, (int)action, (uint)old.Modifiers | MOD_NOREPEAT, (uint)old.VirtualKey))
                _active.Add(action);
            return false;
        }
        _desired[action] = hotkey;
        _active.Add(action);
        _log.LogInformation("Горячая клавиша {Hotkey} -> {Action}", hotkey, action);
        return true;
    });

    public void Unregister(HotkeyAction action) => _window.Invoke(() =>
    {
        _desired.Remove(action);
        if (_active.Remove(action)) UnregisterHotKey(_window.Handle, (int)action);
    });

    public Hotkey? GetRegistered(HotkeyAction action) =>
        _window.Invoke(() => _desired.TryGetValue(action, out var h) ? h : (Hotkey?)null);

    public void SetSuspended(string reason, bool suspended) => _window.Invoke(() =>
    {
        bool was = IsSuspended;
        if (suspended) _suspendReasons.Add(reason);
        else _suspendReasons.Remove(reason);
        if (was == IsSuspended) return;

        if (IsSuspended)
        {
            foreach (var action in _active) UnregisterHotKey(_window.Handle, (int)action);
            _active.Clear();
            _log.LogInformation("Горячие клавиши приостановлены: {Reason}", reason);
            return;
        }
        foreach (var (action, hotkey) in _desired)
        {
            if (RegisterHotKey(_window.Handle, (int)action, (uint)hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.VirtualKey))
                _active.Add(action);
            else
                _log.LogWarning("После приостановки {Hotkey} ({Action}) занято другой программой", hotkey, action);
        }
        _log.LogInformation("Горячие клавиши возобновлены");
    });

    public bool CanRegister(Hotkey hotkey) => _window.Invoke(() =>
    {
        if (_desired.ContainsValue(hotkey)) return true;
        if (!RegisterHotKey(_window.Handle, ProbeId, (uint)hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.VirtualKey))
            return false;
        UnregisterHotKey(_window.Handle, ProbeId);
        return true;
    });

    private bool OnMessage(uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message != WM_HOTKEY) return false;

        long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var action = (HotkeyAction)(int)wParam;
        if (!_active.Contains(action)) return true; // сообщение, поставленное в очередь до приостановки
        GetCursorPos(out POINT pt);
        if (_desired.TryGetValue(action, out var hotkey))
            AltMenuSuppressor.Suppress(hotkey.Modifiers);
        Pressed?.Invoke(this, new HotkeyPressedEventArgs(action, new ScreenPoint(pt.X, pt.Y), timestamp));
        return true;
    }

    public void Dispose()
    {
        try
        {
            _window.Invoke(() =>
            {
                foreach (var action in _active)
                    UnregisterHotKey(_window.Handle, (int)action);
                _active.Clear();
                _desired.Clear();
            });
            _window.RemoveHandler(OnMessage);
        }
        catch (ObjectDisposedException)
        {
            // окно уже закрыто вместе с хостом
        }
    }
}
