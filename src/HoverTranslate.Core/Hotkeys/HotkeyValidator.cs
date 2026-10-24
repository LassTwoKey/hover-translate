namespace HoverTranslate.Core.Hotkeys;

/// <summary>Проверка сочетания, выбранного в настройках, до обращения к системе (занятость проверяет <see cref="IHotkeyService.CanRegister"/>).</summary>
public static class HotkeyValidator
{
    /// <summary>Сочетания, которые принадлежат Windows или ломают привычную работу.</summary>
    private static readonly string[] Reserved =
    [
        "Alt+F4", "Alt+Tab", "Alt+Esc", "Alt+Space", "Ctrl+Esc", "Ctrl+Shift+Esc", "Ctrl+Alt+Delete",
        "Win+L", "Win+D", "Win+R", "Win+Tab",
    ];

    /// <param name="candidate">Выбранное сочетание.</param>
    /// <param name="otherName">Название другого действия («Перевод области»).</param>
    /// <param name="other">Сочетание другого действия, если назначено.</param>
    /// <returns>Текст ошибки или null, если сочетание годится.</returns>
    public static string? Validate(Hotkey candidate, string otherName, Hotkey? other)
    {
        if (candidate.Modifiers == HotkeyModifiers.None)
            return "Добавьте к клавише Ctrl, Alt, Shift или Win.";

        if (other is { } o && o == candidate)
            return $"Это сочетание уже назначено на действие «{otherName}».";

        foreach (string text in Reserved)
        {
            if (Hotkey.Parse(text) == candidate)
                return $"{candidate} зарезервировано Windows.";
        }

        // Ctrl+Буква — копирование, вставка и прочие команды программ; Shift+Буква — заглавные буквы.
        bool onlyCtrlOrShift = candidate.Modifiers is HotkeyModifiers.Control or HotkeyModifiers.Shift;
        bool functionKey = candidate.VirtualKey is >= 0x70 and <= 0x87;
        if (onlyCtrlOrShift && !functionKey)
            return $"{candidate} перехватило бы обычный ввод и команды программ (копирование, заглавные буквы). Добавьте Alt или Win.";

        return null;
    }
}
