using HoverTranslate.Core.Models;

namespace HoverTranslate.Core.Storage;

public interface IFavoritesStore
{
    bool IsFavorite(string lemma);

    /// <summary>Добавить или убрать слово из избранного. Возвращает новое состояние.</summary>
    bool Toggle(LookupResult result);

    /// <summary>Добавить запись (например, из истории); false — такое слово уже в избранном.</summary>
    bool Add(WordRecord record);

    bool Remove(long id);

    bool RemoveByLemma(string lemma);

    void Clear();

    int Count { get; }

    /// <summary>Записи, новые первыми; <paramref name="query"/> — поиск по слову, переводу, определению и предложению.</summary>
    IReadOnlyList<WordRecord> List(string? query = null);

    /// <summary>Содержимое изменилось. Вызывается в потоке, который внёс изменение.</summary>
    event EventHandler? Changed;
}

/// <summary>Избранное в user.db.</summary>
public sealed class FavoritesStore : IFavoritesStore
{
    private readonly WordTable _table;

    public event EventHandler? Changed;

    public FavoritesStore() : this(AppPaths.UserDbPath)
    {
    }

    public FavoritesStore(string dbPath)
    {
        _table = new WordTable(dbPath, "favorites", uniqueLemma: true);
    }

    public int Count => _table.Count();

    public bool IsFavorite(string lemma) => _table.ContainsLemma(lemma);

    public bool Toggle(LookupResult result)
    {
        if (_table.DeleteByLemma(result.Lemma))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }
        _table.Insert(WordRecord.From(result));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Add(WordRecord record)
    {
        if (!_table.Insert(record with { Id = 0 })) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Remove(long id) => Notify(_table.DeleteById(id));

    public bool RemoveByLemma(string lemma) => Notify(_table.DeleteByLemma(lemma));

    public void Clear() => Notify(_table.Clear() > 0);

    public IReadOnlyList<WordRecord> List(string? query = null) => query is null
        ? _table.List()
        : _table.List().Where(r => r.Matches(query)).ToList();

    private bool Notify(bool changed)
    {
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }
}
