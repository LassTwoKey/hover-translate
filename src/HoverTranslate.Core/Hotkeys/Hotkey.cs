using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace HoverTranslate.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>Сочетание клавиш: модификаторы + виртуальный код клавиши. Текстовый вид — «Ctrl+Alt+Q».</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Backspace"] = 0x08, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Pause"] = 0x13, ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD,
        [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, ["\\"] = 0xDC,
    };

    public static readonly Hotkey DefaultLookup = new(HotkeyModifiers.Alt, 'Q');
    public static readonly Hotkey DefaultAreaTranslate = new(HotkeyModifiers.Alt, 'W');

    public static bool TryParse(string? text, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyModifiers.None;
        int? vk = null;
        // «Ctrl++» не поддерживаем; разделитель — «+», пробелы вокруг допустимы.
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0) return false;
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= HotkeyModifiers.Control; continue;
                case "ALT": mods |= HotkeyModifiers.Alt; continue;
                case "SHIFT": mods |= HotkeyModifiers.Shift; continue;
                case "WIN" or "WINDOWS": mods |= HotkeyModifiers.Win; continue;
            }
            if (vk is not null) return false; // две обычные клавиши
            vk = ParseKey(part);
            if (vk is null) return false;
        }
        if (vk is null || mods == HotkeyModifiers.None) return false; // без модификатора — перехват обычного ввода
        hotkey = new Hotkey(mods, vk.Value);
        return true;
    }

    public static Hotkey Parse(string text) =>
        TryParse(text, out var h) ? h.Value : throw new FormatException($"Некорректное сочетание клавиш: «{text}»");

    private static int? ParseKey(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f')
            && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 24)
            return 0x70 + n - 1;
        return NamedKeys.TryGetValue(key, out int vk) ? vk : null;
    }

    private static string KeyName(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        foreach (var (name, code) in NamedKeys)
            if (code == vk) return name;
        return "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }
}
