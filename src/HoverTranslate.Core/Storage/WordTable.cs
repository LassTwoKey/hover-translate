using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HoverTranslate.Core.Storage;

/// <summary>
/// Таблица слов в user.db. Одна схема на историю и избранное; имя таблицы задаёт только код
/// (в запросы подставляется напрямую, пользовательского ввода в нём нет).
/// </summary>
internal sealed class WordTable
{
    private const string Columns = "id, lemma, word, translation, definition, sentence, sentence_ru, source_app, added_at";

    private readonly string _connectionString;
    private readonly string _table;

    public WordTable(string dbPath, string table, bool uniqueLemma)
    {
        _table = table;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = true }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {table} (
                id INTEGER PRIMARY KEY,
                lemma TEXT NOT NULL{(uniqueLemma ? " UNIQUE" : "")},
                word TEXT NOT NULL,
                translation TEXT,
                definition TEXT,
                sentence TEXT,
                sentence_ru TEXT,
                source_app TEXT,
                added_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Добавить запись; false — не добавлена (лемма уже есть в таблице с уникальной леммой).</summary>
    public bool Insert(WordRecord r)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            INSERT OR IGNORE INTO {_table} (lemma, word, translation, definition, sentence, sentence_ru, source_app, added_at)
            VALUES ($lemma, $word, $translation, $definition, $sentence, $sentenceRu, $app, $at)
            """;
        cmd.Parameters.AddWithValue("$lemma", r.Lemma);
        cmd.Parameters.AddWithValue("$word", r.Word);
        cmd.Parameters.AddWithValue("$translation", (object?)r.Translation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$definition", (object?)r.Definition ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sentence", (object?)r.Sentence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sentenceRu", (object?)r.SentenceRu ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$app", (object?)r.SourceApp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", r.AtText);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Все записи, новые первыми.</summary>
    public List<WordRecord> List()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {_table} ORDER BY id DESC";
        using var reader = cmd.ExecuteReader();
        var list = new List<WordRecord>();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    /// <summary>Самая новая запись или null.</summary>
    public WordRecord? Latest()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {_table} ORDER BY id DESC LIMIT 1";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public bool ContainsLemma(string lemma)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {_table} WHERE lemma = $lemma";
        cmd.Parameters.AddWithValue("$lemma", lemma);
        return cmd.ExecuteScalar() is not null;
    }

    public bool DeleteById(long id) => Delete("id = $v", id);

    public bool DeleteByLemma(string lemma) => Delete("lemma = $v", lemma);

    /// <summary>Оставить только <paramref name="keep"/> самых новых записей.</summary>
    public void Trim(int keep)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_table} WHERE id NOT IN (SELECT id FROM {_table} ORDER BY id DESC LIMIT $keep)";
        cmd.Parameters.AddWithValue("$keep", keep);
        cmd.ExecuteNonQuery();
    }

    public int Clear()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_table}";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Вернуть освобождённое место на диске (после очистки). Занятая база — не ошибка: просто останется как есть.</summary>
    public void Compact()
    {
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // другое соединение держит базу; размер файла — справочная цифра
        }
    }

    public int Count()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {_table}";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private bool Delete(string where, object value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"DELETE FROM {_table} WHERE {where}";
        cmd.Parameters.AddWithValue("$v", value);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static WordRecord Read(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        DateTimeOffset.TryParse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : default);

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }
}
