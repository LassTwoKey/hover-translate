using System.Globalization;

namespace HoverTranslate.Core.Storage;

/// <summary>Тема карточки.</summary>
public enum PopupTheme
{
    /// <summary>Как в Windows.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Типизированные настройки поверх <see cref="ISettingsStore"/>: значение читается при каждом обращении
/// (поэтому смена настройки действует сразу), неверное значение заменяется умолчанием.
/// </summary>
public sealed class AppSettings(ISettingsStore store)
{
    public const int FontPercentMin = 80, FontPercentMax = 150, FontPercentDefault = 100;
    public const int OpacityPercentMin = 60, OpacityPercentMax = 100, OpacityPercentDefault = 90;
    public const int AutoCloseSecondsMax = 300;

    /// <summary>Размер шрифта карточки в процентах от обычного.</summary>
    public int PopupFontPercent => ParseInt(store.Get(SettingKeys.PopupFontPercent), FontPercentDefault, FontPercentMin, FontPercentMax);

    /// <summary>Плотность фона карточки в процентах (100 — сплошной).</summary>
    public int PopupOpacityPercent => ParseInt(store.Get(SettingKeys.PopupOpacityPercent), OpacityPercentDefault, OpacityPercentMin, OpacityPercentMax);

    public PopupTheme PopupTheme => store.Get(SettingKeys.PopupTheme)?.Trim().ToLowerInvariant() switch
    {
        "light" => PopupTheme.Light,
        "dark" => PopupTheme.Dark,
        _ => PopupTheme.System,
    };

    /// <summary>Через сколько секунд закрывать карточку, 0 — не закрывать. Закреплённая карточка и карточка под курсором не закрываются.</summary>
    public int PopupAutoCloseSeconds => ParseInt(store.Get(SettingKeys.PopupAutoCloseSeconds), 0, 0, AutoCloseSecondsMax);

    /// <summary>Скрывать карточку из снимков экрана. По умолчанию да: иначе OCR прочитал бы текст самой карточки.</summary>
    public bool PopupExcludeFromCapture => ParseBool(store.Get(SettingKeys.PopupExcludeFromCapture), true);

    public bool HistoryEnabled => ParseBool(store.Get(SettingKeys.HistoryEnabled), true);

    /// <summary>Читать плохо распознанный текст запасным движком (PaddleOCR) — точнее, но на 0,5–1,5 с дольше. По умолчанию да.</summary>
    public bool FallbackOcrEnabled => ParseBool(store.Get(SettingKeys.FallbackOcrEnabled), true);

    public bool CacheEnabled => ParseBool(store.Get(SettingKeys.CacheEnabled), true);

    /// <summary>Выбранный голос озвучки; null — автоматически.</summary>
    public string? SpeechVoice => string.IsNullOrEmpty(store.Get(SettingKeys.SpeechVoice)) ? null : store.Get(SettingKeys.SpeechVoice);

    /// <summary>Переводить предложения и области через облако (Gemini). По умолчанию нет.</summary>
    public bool CloudEnabled => ParseBool(store.Get(SettingKeys.CloudEnabled), false);

    /// <summary>Модель Gemini: из настроек, иначе <see cref="Translation.Cloud.CloudDefaults.Model"/>.</summary>
    public string CloudModel => store.Get(SettingKeys.CloudModel) is { } m && !string.IsNullOrWhiteSpace(m) ? m.Trim() : Translation.Cloud.CloudDefaults.Model;

    /// <summary>Программы, в которых горячие клавиши и кнопки мыши не перехватываются.</summary>
    public IReadOnlyList<string> ExcludedProcesses => ProcessExclusions.Parse(store.Get(SettingKeys.ExcludedProcesses));

    internal static int ParseInt(string? text, int fallback, int min, int max) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? Math.Clamp(v, min, max) : fallback;

    internal static bool ParseBool(string? text, bool fallback) => text?.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => fallback,
    };
}

/// <summary>Список программ-исключений: имена процессов без «.exe», без учёта регистра; хранится по одному в строке.</summary>
public static class ProcessExclusions
{
    /// <summary>Имя процесса из ввода пользователя: «C:\Games\Game.exe», «game.exe» и «Game» дают «Game».</summary>
    public static string Normalize(string? name)
    {
        string s = (name ?? "").Trim().Trim('"');
        if (s.Contains('\\') || s.Contains('/')) s = s[(s.LastIndexOfAny(['\\', '/']) + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Trim();
    }

    public static IReadOnlyList<string> Parse(string? stored)
    {
        var result = new List<string>();
        foreach (string part in (stored ?? "").Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = Normalize(part);
            if (name.Length > 0 && !result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
        }
        return result;
    }

    public static string Format(IEnumerable<string> names) => string.Join('\n', Parse(string.Join('\n', names)));

    /// <summary>Входит ли процесс в список. Неизвестный процесс («?») — никогда.</summary>
    public static bool Contains(string? stored, string processName)
    {
        if (string.IsNullOrEmpty(stored) || processName is "" or "?") return false;
        return Parse(stored).Contains(processName, StringComparer.OrdinalIgnoreCase);
    }
}
