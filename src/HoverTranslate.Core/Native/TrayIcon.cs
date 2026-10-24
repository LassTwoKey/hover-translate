using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Native;

/// <summary>Пункт контекстного меню значка в трее; с <see cref="Children"/> — подменю.</summary>
public sealed record TrayMenuItem(string Text, Action? OnClick, bool Enabled = true, bool Checked = false,
    IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static readonly TrayMenuItem Separator = new("-", null);
}

/// <summary>Тип всплывающего уведомления.</summary>
public enum TrayBalloonKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Значок в области уведомлений через Shell_NotifyIcon. Живёт на потоке служебного окна;
/// восстанавливается после перезапуска Проводника (TaskbarCreated).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint WM_TRAY = WM_APP + 10;
    private const uint IconId = 1;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int IDI_APPLICATION = 32512;

    private readonly MessageWindow _window;
    private readonly uint _taskbarCreated;
    private readonly nint _icon;
    private readonly bool _ownsIcon;
    private Func<IReadOnlyList<TrayMenuItem>> _menuFactory = () => [];
    private string _tooltip;
    private bool _added;

    /// <summary>Двойной щелчок (или Enter) по значку.</summary>
    public event EventHandler? DoubleClicked;

    /// <param name="iconPath">.ico; если файла нет — стандартный значок приложения.</param>
    public TrayIcon(MessageWindow window, string? iconPath, string tooltip)
    {
        _window = window;
        _tooltip = tooltip;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        int size = GetSystemMetrics(49); // SM_CXSMICON — размер значка в трее с учётом DPI
        if (iconPath is not null && File.Exists(iconPath))
        {
            _icon = LoadImage(0, iconPath, IMAGE_ICON, size, size, LR_LOADFROMFILE);
            _ownsIcon = _icon != 0;
        }
        if (_icon == 0)
            _icon = LoadIcon(0, IDI_APPLICATION);
        _window.AddHandler(OnMessage);
        _window.Invoke(Add);
    }

    /// <summary>Меню строится при каждом открытии — так в нём всегда актуальные подписи.</summary>
    public void SetMenu(Func<IReadOnlyList<TrayMenuItem>> factory) => _menuFactory = factory;

    public void SetTooltip(string tooltip) => _window.Invoke(() =>
    {
        _tooltip = tooltip;
        if (_added) Notify(NIM_MODIFY, NIF_TIP | NIF_SHOWTIP);
    });

    public unsafe void ShowBalloon(string title, string text, TrayBalloonKind kind = TrayBalloonKind.Info) => _window.Invoke(() =>
    {
        var data = CreateData(NIF_INFO);
        Copy(title, data.szInfoTitle, 64);
        Copy(text, data.szInfo, 256);
        data.dwInfoFlags = kind switch
        {
            TrayBalloonKind.Warning => NIIF_WARNING,
            TrayBalloonKind.Error => NIIF_ERROR,
            _ => NIIF_INFO,
        };
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    });

    private void Add()
    {
        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (_added)
        {
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
    }

    private void Notify(uint message, uint flags)
    {
        var data = CreateData(flags);
        Shell_NotifyIcon(message, ref data);
    }

    private unsafe NOTIFYICONDATAW CreateData(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
        };
        Copy(_tooltip, data.szTip, 128);
        return data;
    }

    private static unsafe void Copy(string text, char* dest, int capacity)
    {
        int n = Math.Min(text.Length, capacity - 1);
        for (int i = 0; i < n; i++) dest[i] = text[i];
        dest[n] = '\0';
    }

    private bool OnMessage(uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message == _taskbarCreated)
        {
            Add(); // Проводник перезапущен — значок пропал
            return false;
        }
        if (message != WM_TRAY) return false;

        // NOTIFYICON_VERSION_4: событие — в младшем слове lParam, координаты — в wParam.
        uint evt = (uint)(lParam & 0xFFFF);
        switch (evt)
        {
            case WM_LBUTTONDBLCLK or NIN_KEYSELECT:
                DoubleClicked?.Invoke(this, EventArgs.Empty);
                break;
            case WM_CONTEXTMENU or WM_RBUTTONUP:
                ShowMenu(unchecked((short)(wParam & 0xFFFF)), unchecked((short)((wParam >> 16) & 0xFFFF)));
                break;
        }
        return true;
    }

    private void ShowMenu(int x, int y)
    {
        var commands = new List<TrayMenuItem>();
        nint menu = BuildMenu(_menuFactory(), commands);
        try
        {
            // Без этого меню не закрывается по щелчку мимо (известная особенность Shell_NotifyIcon).
            SetForegroundWindow(_window.Handle);
            int cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, _window.Handle, 0);
            if (cmd > 0 && cmd <= commands.Count)
                commands[cmd - 1].OnClick?.Invoke();
        }
        finally
        {
            DestroyMenu(menu); // уничтожает и вложенные подменю
        }
    }

    /// <summary>Строит HMENU; идентификатор команды — индекс в <paramref name="commands"/> + 1.</summary>
    private static nint BuildMenu(IReadOnlyList<TrayMenuItem> items, List<TrayMenuItem> commands)
    {
        nint menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (ReferenceEquals(item, TrayMenuItem.Separator))
            {
                AppendMenu(menu, MF_SEPARATOR, 0, null);
                continue;
            }
            uint flags = MF_STRING | (item.Enabled ? 0 : MF_GRAYED) | (item.Checked ? MF_CHECKED : 0);
            if (item.Children is { Count: > 0 })
            {
                AppendMenu(menu, flags | MF_POPUP, (nuint)BuildMenu(item.Children, commands), item.Text);
            }
            else
            {
                commands.Add(item);
                AppendMenu(menu, flags, (nuint)commands.Count, item.Text);
            }
        }
        return menu;
    }

    public void Dispose()
    {
        try
        {
            _window.Invoke(() =>
            {
                if (_added) Notify(NIM_DELETE, 0);
                _added = false;
            });
            _window.RemoveHandler(OnMessage);
        }
        catch (ObjectDisposedException)
        {
            // служебное окно уже закрыто
        }
        if (_ownsIcon) DestroyIcon(_icon);
    }

    /// <summary>Модальное сообщение Windows (до запуска UI, например «уже запущено»).</summary>
    public static void ShowMessage(string text, string caption, bool error = false) =>
        _ = MessageBox(0, text, caption, MB_OK | MB_SETFOREGROUND | (error ? MB_ICONERROR : MB_ICONINFORMATION));
}
