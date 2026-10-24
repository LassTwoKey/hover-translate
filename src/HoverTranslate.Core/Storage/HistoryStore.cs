using HoverTranslate.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.Core.Storage;

public interface IHistoryStore
{
    /// <summary>Записывать ли новые слова (настройка <see cref="SettingKeys.HistoryEnabled"/>, по умолчанию да).</summary>
    bool Enabled { get; }

    /// <summary>Записать найденное слово; ничего не делает, если история выключена. Ошибки пишутся в лог.</summary>
    void Record(LookupResult result);

    bool Remove(long id);

    void Clear();

    int Count { get; }

    /// <summary>Записи, новые первыми; <paramref name="query"/> — поиск по слову, переводу, определению и предложению.</summary>
    IReadOnlyList<WordRecord> List(string? query = null);

    /// <summary>Содержимое изменилось. Вызывается в потоке, который внёс изменение.</summary>
    event EventHandler? Changed;
}

/// <summary>История найденных слов в user.db: слово, лемма, перевод, предложение, программа, время.</summary>
public sealed partial class HistoryStore : IHistoryStore
{
    /// <summary>Старые записи сверх этого числа удаляются.</summary>
    public const int MaxRecords = 5000;

    private readonly WordTable _table;
    private readonly ISettingsStore _settings;
    private readonly ILogger<HistoryStore> _log;
    private readonly Lock _gate = new();
    private readonly int _maxRecords;

    public event EventHandler? Changed;

    public HistoryStore(ISettingsStore settings, ILogger<HistoryStore> log) : this(AppPaths.UserDbPath, settings, log)
    {
    }

    public HistoryStore(string dbPath, ISettingsStore settings, ILogger<HistoryStore> log, int maxRecords = MaxRecords)
    {
        _table = new WordTable(dbPath, "history", uniqueLemma: false);
        _settings = settings;
        _log = log;
        _maxRecords = maxRecords;
    }

    public bool Enabled => !string.Equals(_settings.Get(SettingKeys.HistoryEnabled), "false", StringComparison.OrdinalIgnoreCase);

    public int Count => _table.Count();

    public void Record(LookupResult result)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(result.Lemma)) return;
        try
        {
            lock (_gate)
            {
                var record = WordRecord.From(result);
                // Повторный показ того же слова в том же предложении — не новая запись.
                if (_table.Latest() is { } last && last.Lemma == record.Lemma && last.Sentence == record.Sentence) return;
                _table.Insert(record);
                _table.Trim(_maxRecords);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (SqliteException ex)
        {
            LogFailed(ex);
        }
    }

    public bool Remove(long id) => Notify(_table.DeleteById(id));

    public void Clear()
    {
        int removed = _table.Clear();
        if (removed > 0) _table.Compact();
        Notify(removed > 0);
    }

    public IReadOnlyList<WordRecord> List(string? query = null) => query is null
        ? _table.List()
        : _table.List().Where(r => r.Matches(query)).ToList();

    private bool Notify(bool changed)
    {
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    [LoggerMessage(LogLevel.Warning, "Не удалось записать слово в историю")]
    private partial void LogFailed(Exception ex);
}
