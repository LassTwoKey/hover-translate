using Microsoft.Data.Sqlite;

namespace HoverTranslate.Core.Storage;

/// <summary>Ключи настроек.</summary>
public static class SettingKeys
{
    public const string LookupHotkey = "hotkey.lookup";
    public const string AreaHotkey = "hotkey.area";
    /// <summary>Действие на боковой кнопке мыши 4 (X1) и 5 (X2): "word" | "sentence" | "area" | "none".</summary>
    public const string MouseX1 = "mouse.x1";
    public const string MouseX2 = "mouse.x2";
    /// <summary>
    /// "false" — карточка видна в скриншотах и записи экрана. По умолчанию скрыта: иначе OCR при
    /// следующем нажатии прочитал бы текст самой карточки.
    /// </summary>
    public const string PopupExcludeFromCapture = "popup.excludeFromCapture";
    /// <summary>Идентификатор голоса озвучки; пусто или нет в системе — выбирается автоматически (en-US).</summary>
    public const string SpeechVoice = "speech.voice";
    /// <summary>"false" — не записывать найденные слова в историю.</summary>
    public const string HistoryEnabled = "history.enabled";
    /// <summary>"false" — не использовать и не пополнять кэш переводов предложений.</summary>
    public const string CacheEnabled = "cache.enabled";
    /// <summary>"false" — не запускать запасной OCR (PaddleOCR), когда Windows OCR прочитал текст плохо.</summary>
    public const string FallbackOcrEnabled = "ocr.fallback.enabled";
    /// <summary>Размер шрифта карточки, % (80–150, по умолчанию 100).</summary>
    public const string PopupFontPercent = "popup.fontPercent";
    /// <summary>Плотность фона карточки, % (60–100, по умолчанию 90).</summary>
    public const string PopupOpacityPercent = "popup.opacityPercent";
    /// <summary>"system" | "light" | "dark".</summary>
    public const string PopupTheme = "popup.theme";
    /// <summary>Автозакрытие карточки через N секунд; 0 или нет — выключено.</summary>
    public const string PopupAutoCloseSeconds = "popup.autoCloseSeconds";
    /// <summary>Имена процессов (по одному в строке), в которых горячие клавиши и кнопки мыши не перехватываются.</summary>
    public const string ExcludedProcesses = "exclusions.processes";
    /// <summary>"true" — переводить предложения и области через Gemini (нужен ключ). По умолчанию выключено.</summary>
    public const string CloudEnabled = "cloud.enabled";
    /// <summary>Ключ Gemini API, зашифрованный DPAPI (base64). Открытым текстом не хранится.</summary>
    public const string CloudKeyProtected = "cloud.gemini.key";
    /// <summary>Имя модели Gemini; пусто — по умолчанию (<see cref="Translation.Cloud.CloudDefaults.Model"/>).</summary>
    public const string CloudModel = "cloud.gemini.model";
    /// <summary>Счётчик запросов к облаку: дата (локальная, yyyy-MM-dd), запросов за эту дату и всего.</summary>
    public const string CloudUsageDate = "cloud.usage.date";
    public const string CloudUsageToday = "cloud.usage.today";
    public const string CloudUsageTotal = "cloud.usage.total";
}

public interface ISettingsStore
{
    string? Get(string key);

    void Set(string key, string value);

    event EventHandler<string>? Changed;
}

/// <summary>Настройки «ключ — значение» в SQLite (%LocalAppData%\HoverTranslate\user.db).</summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly string _connectionString;

    public event EventHandler<string>? Changed;

    public SettingsStore() : this(AppPaths.UserDbPath)
    {
    }

    public SettingsStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = true }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public string? Get(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using (var db = Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) " +
                              "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
        Changed?.Invoke(this, key);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }
}
