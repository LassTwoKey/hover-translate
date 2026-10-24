using System.Security.Cryptography;
using System.Text;
using HoverTranslate.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.Core.Translation;

/// <summary>Кэш переводов предложений.</summary>
public interface ITranslationCache
{
    /// <summary>Перевод из кэша или null.</summary>
    /// <param name="scope">Чей перевод: null — локальной модели, иначе облачной (имя модели). Переводы разных источников не смешиваются.</param>
    string? Get(string source, string? scope = null);

    void Put(string source, string target, string? scope = null);

    /// <summary>Сколько предложений в кэше.</summary>
    int Count { get; }

    /// <summary>Удалить всё и вернуть место на диске.</summary>
    void Clear();
}

/// <summary>
/// Кэш в SQLite (%LocalAppData%\HoverTranslate\cache.db): ключ — SHA-256 от «модель|нормализованная строка».
/// Ограничен по размеру: при переполнении удаляются самые старые записи. Выключается настройкой
/// <see cref="SettingKeys.CacheEnabled"/> (тогда не читается и не пополняется, содержимое остаётся).
/// Ошибка кэша никогда не ломает перевод: она пишется в лог и считается промахом.
/// </summary>
public sealed partial class SqliteTranslationCache : ITranslationCache, IDisposable
{
    /// <summary>Входит в ключ: другая модель — другие переводы.</summary>
    public const string ModelId = "opus-mt-en-ru-int8-beam4";

    /// <summary>Предел записей (≈ 10–20 МБ на диске).</summary>
    public const int MaxEntries = 20_000;

    /// <summary>Как часто (в записях) проверять переполнение.</summary>
    private const int TrimEvery = 200;

    private readonly ILogger<SqliteTranslationCache> _log;
    private readonly Func<bool> _isEnabled;
    private readonly int _maxEntries;
    private readonly SqliteConnection? _db;
    private readonly Lock _lock = new();
    private int _putsSinceTrim;
    private long _lastCreated;

    public SqliteTranslationCache(ISettingsStore settings, ILogger<SqliteTranslationCache> log)
        : this(AppPaths.CacheDbPath, log, () => new AppSettings(settings).CacheEnabled)
    {
    }

    public SqliteTranslationCache(string path, ILogger<SqliteTranslationCache> log, Func<bool>? isEnabled = null, int maxEntries = MaxEntries)
    {
        _log = log;
        _isEnabled = isEnabled ?? (() => true);
        _maxEntries = maxEntries;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            _db.Open();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS translations (
                    hash BLOB PRIMARY KEY,
                    source TEXT NOT NULL,
                    target TEXT NOT NULL,
                    created INTEGER NOT NULL
                ) WITHOUT ROWID;
                """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogOpenFailed(ex, path);
            _db?.Dispose();
            _db = null;
        }
    }

    /// <summary>
    /// Время создания записи для порядка вытеснения: миллисекунды, строго возрастающие внутри процесса (иначе сотни
    /// записей за одну секунду «одновременны», и вытесняются случайные, в том числе самые новые). Записи старых версий хранят
    /// секунды — это заведомо меньшие числа, то есть самые старые. Вызывается под <c>_lock</c>.
    /// </summary>
    private long NextCreated() => _lastCreated = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _lastCreated + 1);

    public int Count
    {
        get
        {
            if (_db is null) return 0;
            try
            {
                lock (_lock) return CountLocked();
            }
            catch (SqliteException ex)
            {
                LogFailed(ex);
                return 0;
            }
        }
    }

    public string? Get(string source, string? scope = null)
    {
        if (_db is null || !_isEnabled()) return null;
        try
        {
            lock (_lock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT target FROM translations WHERE hash = $hash";
                cmd.Parameters.AddWithValue("$hash", Hash(source, scope));
                return cmd.ExecuteScalar() as string;
            }
        }
        catch (SqliteException ex)
        {
            LogFailed(ex);
            return null;
        }
    }

    public void Put(string source, string target, string? scope = null)
    {
        if (_db is null || !_isEnabled()) return;
        try
        {
            lock (_lock)
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT OR REPLACE INTO translations (hash, source, target, created) VALUES ($hash, $source, $target, $created)
                        """;
                    cmd.Parameters.AddWithValue("$hash", Hash(source, scope));
                    cmd.Parameters.AddWithValue("$source", TextSegmenter.Normalize(source));
                    cmd.Parameters.AddWithValue("$target", target);
                    cmd.Parameters.AddWithValue("$created", NextCreated());
                    cmd.ExecuteNonQuery();
                }
                if (++_putsSinceTrim >= TrimEvery)
                {
                    _putsSinceTrim = 0;
                    TrimLocked();
                }
            }
        }
        catch (SqliteException ex)
        {
            LogFailed(ex);
        }
    }

    public void Clear()
    {
        if (_db is null) return;
        try
        {
            lock (_lock)
            {
                using var cmd = _db.CreateCommand();
                // VACUUM возвращает место на диске, checkpoint сбрасывает журнал WAL в основной файл и обрезает его
                cmd.CommandText = "DELETE FROM translations; PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
                cmd.ExecuteNonQuery();
                _putsSinceTrim = 0;
            }
        }
        catch (SqliteException ex)
        {
            LogFailed(ex);
        }
    }

    internal void Trim()
    {
        if (_db is null) return;
        lock (_lock) TrimLocked();
    }

    /// <summary>Если записей больше предела — удаляет самые старые, оставляя 90 % предела (чтобы не чистить на каждой записи).</summary>
    private void TrimLocked()
    {
        int count = CountLocked();
        if (count <= _maxEntries) return;
        int keep = Math.Max(1, _maxEntries * 9 / 10);
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = """
            DELETE FROM translations WHERE hash IN (
                SELECT hash FROM translations ORDER BY created ASC LIMIT $drop)
            """;
        cmd.Parameters.AddWithValue("$drop", count - keep);
        cmd.ExecuteNonQuery();
    }

    private int CountLocked()
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM translations";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Ключ записи: модель (и, для облака, имя облачной модели) + нормализованный текст. Локальные ключи не изменились.</summary>
    internal static byte[] Hash(string source, string? scope = null) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(ModelId + (scope is null ? "" : "|cloud:" + scope) + "|" + TextSegmenter.Normalize(source)));

    public void Dispose() => _db?.Dispose();

    [LoggerMessage(LogLevel.Warning, "Кэш переводов недоступен: {Path}")]
    private partial void LogOpenFailed(Exception ex, string path);

    [LoggerMessage(LogLevel.Warning, "Ошибка кэша переводов")]
    private partial void LogFailed(Exception ex);
}
