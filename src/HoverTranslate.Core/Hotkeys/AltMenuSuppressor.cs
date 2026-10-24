using HoverTranslate.Core.Native;
using static HoverTranslate.Core.Native.NativeMethods;

namespace HoverTranslate.Core.Hotkeys;

/// <summary>
/// Не даёт приложению под курсором открыть строку меню после Alt+Q (или меню «Пуск» после Win+…).
/// </summary>
/// <remarks>
/// RegisterHotKey «съедает» Q, и приложение видит одиночное нажатие-отпускание Alt — для классических
/// Win32-окон это команда активировать меню. Пока модификатор ещё зажат, посылаем нажатие
/// неназначенной клавиши vkE8: система считает Alt частью сочетания. Тот же приём используют
/// AutoHotkey («menu mask key») и PowerToys. Это обычный SendInput, без хуков и внедрения.
/// </remarks>
internal static class AltMenuSuppressor
{
    private const ushort VK_UNASSIGNED = 0xE8;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    public static void Suppress(HotkeyModifiers modifiers)
    {
        if (!modifiers.HasFlag(HotkeyModifiers.Alt) && !modifiers.HasFlag(HotkeyModifiers.Win))
            return;
        bool held = IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN);
        if (!held) return; // модификатор уже отпущен — меню не откроется

        Span<INPUT> inputs = stackalloc INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_UNASSIGNED;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_UNASSIGNED;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        unsafe
        {
            SendInput(2, inputs, sizeof(INPUT));
        }
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
}
