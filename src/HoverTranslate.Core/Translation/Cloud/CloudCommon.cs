using System.Globalization;
using HoverTranslate.Core.Storage;

namespace HoverTranslate.Core.Translation.Cloud;

/// <summary>Значения по умолчанию для облачного перевода.</summary>
public static class CloudDefaults
{
    /// <summary>Алиас «последняя flash-lite»: имена конкретных версий быстро устаревают (gemini-2.5-flash-lite уже отдаёт 404).</summary>
    public const string Model = "gemini-flash-lite-latest";

    /// <summary>Запасные модели на случай 404 по основной (пробуются по порядку, затем локальный перевод).</summary>
    public static readonly string[] FallbackModels = ["gemini-flash-lite-latest", "gemini-flash-latest"];

    public const string KeyPageUrl = "https://aistudio.google.com/apikey";

    /// <summary>Переменная окружения — запасной источник ключа для разработчика; в окне настроек не показывается.</summary>
    public const string KeyEnvironmentVariable = "GEMINI_API_KEY";
}

public enum CloudErrorKind
{
    /// <summary>Ключ отклонён (400 «API key not valid», 401, 403).</summary>
    InvalidKey,
    /// <summary>Нет ключа в настройках.</summary>
    NoKey,
    /// <summary>429: исчерпан лимит запросов бесплатного уровня.</summary>
    RateLimited,
    /// <summary>404: такой модели нет.</summary>
    ModelNotFound,
    /// <summary>Gemini отказался переводить (фильтр безопасности).</summary>
    Blocked,
    /// <summary>Нет связи с Google.</summary>
    Network,
    Timeout,
    /// <summary>5xx.</summary>
    Server,
    Other,
}

/// <summary>Ошибка облачного перевода; <see cref="UserMessage"/> — текст для подсказки в карточке и окна настроек.</summary>
public sealed class CloudException(CloudErrorKind kind, string userMessage, Exception? inner = null) : Exception(userMessage, inner)
{
    public CloudErrorKind Kind { get; } = kind;

    public string UserMessage => Message;
}

/// <summary>Ключ Gemini API: шифруется DPAPI и хранится в <see cref="ISettingsStore"/> (user.db).</summary>
public sealed class CloudKeyStore(ISettingsStore settings, Func<string, string?>? environment = null)
{
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>Ключ сохранён в настройках.</summary>
    public bool HasStoredKey => GetStoredKey() is not null;

    /// <summary>Ключ есть: сохранённый или (для разработчика) из переменной GEMINI_API_KEY.</summary>
    public bool HasKey => GetKey() is not null;

    /// <summary>Ключ для запросов: сохранённый, иначе из переменной окружения; null — ключа нет.</summary>
    public string? GetKey() =>
        GetStoredKey() ?? (_environment(CloudDefaults.KeyEnvironmentVariable) is { } env && env.Trim().Length > 0 ? env.Trim() : null);

    private string? GetStoredKey()
    {
        string? key = SecretProtector.Unprotect(settings.Get(SettingKeys.CloudKeyProtected));
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public void Save(string key)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ArgumentException("Пустой ключ", nameof(key));
        settings.Set(SettingKeys.CloudKeyProtected, SecretProtector.Protect(key));
    }

    public void Delete() => settings.Set(SettingKeys.CloudKeyProtected, "");
}

/// <summary>Счётчик запросов к облаку: за сегодняшнюю (локальную) дату и всего; хранится в настройках.</summary>
public sealed class CloudUsage(ISettingsStore settings, Func<DateTime>? now = null)
{
    private readonly Func<DateTime> _now = now ?? (() => DateTime.Now);
    private readonly Lock _lock = new();

    public event EventHandler? Changed;

    public int Today
    {
        get
        {
            lock (_lock)
                return settings.Get(SettingKeys.CloudUsageDate) == Date() ? Read(SettingKeys.CloudUsageToday) : 0;
        }
    }

    public int Total
    {
        get
        {
            lock (_lock) return Read(SettingKeys.CloudUsageTotal);
        }
    }

    public void Increment()
    {
        lock (_lock)
        {
            string date = Date();
            int today = settings.Get(SettingKeys.CloudUsageDate) == date ? Read(SettingKeys.CloudUsageToday) : 0;
            settings.Set(SettingKeys.CloudUsageDate, date);
            settings.Set(SettingKeys.CloudUsageToday, (today + 1).ToString(CultureInfo.InvariantCulture));
            settings.Set(SettingKeys.CloudUsageTotal, (Read(SettingKeys.CloudUsageTotal) + 1).ToString(CultureInfo.InvariantCulture));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string Date() => _now().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private int Read(string key) =>
        int.TryParse(settings.Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? Math.Max(0, v) : 0;
}
