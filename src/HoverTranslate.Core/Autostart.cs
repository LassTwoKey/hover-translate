using Microsoft.Win32;

namespace HoverTranslate.Core;

/// <summary>Автозапуск при входе в Windows: значение в HKCU\Software\Microsoft\Windows\CurrentVersion\Run (права администратора не нужны).</summary>
public sealed class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HoverTranslate";

    private readonly string _exePath;
    private readonly string _subKey;
    private readonly string _valueName;

    public Autostart() : this(Environment.ProcessPath ?? "", RunKey, ValueName)
    {
    }

    internal Autostart(string exePath, string subKey, string valueName)
    {
        _exePath = exePath;
        _subKey = subKey;
        _valueName = valueName;
    }

    /// <summary>Автозапуск включён именно для этого exe: запись со старым путём (программу переместили) не считается.</summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKey);
            return key?.GetValue(_valueName) is string value
                   && string.Equals(value.Trim().Trim('"'), _exePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (_exePath.Length == 0) throw new InvalidOperationException("Не удалось определить путь к программе");
            using var key = Registry.CurrentUser.CreateSubKey(_subKey);
            key.SetValue(_valueName, "\"" + _exePath + "\"", RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }
}
