using System.Text;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.Speech;
using HoverTranslate.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoverTranslate.Core.Tests;

/// <summary>Временный user.db; пул соединений сбрасывается, чтобы файл можно было удалить.</summary>
public abstract class UserDbFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ht-tests-" + Guid.NewGuid().ToString("N"));

    protected string DbPath => Path.Combine(_dir, "user.db");

    protected UserDbFixture() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог — не критично
        }
    }

    protected static LookupResult Result(string lemma, string? translation = "перевод", string? sentence = "A sentence.", string word = "")
    {
        var entry = new DictEntry(lemma, null, null,
            [new Sense("noun", "a definition of " + lemma, translation is null ? [] : [translation, "второй"], [])], []);
        return new LookupResult(word.Length > 0 ? word : lemma, lemma, sentence, null, entry, "game.exe", TextSourceKind.Ocr);
    }
}

public sealed class FavoritesStoreTests : UserDbFixture
{
    [Fact]
    public void Toggle_AddsAndRemoves()
    {
        var store = new FavoritesStore(DbPath);
        Assert.True(store.Toggle(Result("run")));
        Assert.True(store.IsFavorite("run"));
        Assert.Equal(1, store.Count);
        Assert.False(store.Toggle(Result("run")));
        Assert.False(store.IsFavorite("run"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Toggle_StoresFieldsOfFirstSenseWithTranslation()
    {
        var store = new FavoritesStore(DbPath);
        store.Toggle(Result("run", "бежать", "I run daily.", word: "ran"));
        var r = Assert.Single(store.List());
        Assert.Equal("run", r.Lemma);
        Assert.Equal("ran", r.Word);
        Assert.Equal("бежать, второй", r.Translation);
        Assert.Equal("a definition of run", r.Definition);
        Assert.Equal("I run daily.", r.Sentence);
        Assert.Equal("game.exe", r.SourceApp);
        Assert.True(r.At > DateTimeOffset.Now.AddMinutes(-1));
    }

    [Fact]
    public void Add_DuplicateLemma_Ignored()
    {
        var store = new FavoritesStore(DbPath);
        var record = WordRecord.From(Result("book"));
        Assert.True(store.Add(record));
        Assert.False(store.Add(record));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void List_NewestFirst_AndSearch()
    {
        var store = new FavoritesStore(DbPath);
        store.Toggle(Result("apple", "яблоко"));
        store.Toggle(Result("book", "КНИГА"));
        store.Toggle(Result("cat", "кот", sentence: "The cat sat."));
        Assert.Equal(["cat", "book", "apple"], store.List().Select(r => r.Lemma));
        Assert.Equal(["book"], store.List("книга").Select(r => r.Lemma)); // регистр кириллицы не важен
        Assert.Equal(["cat"], store.List("sat the").Select(r => r.Lemma)); // все слова запроса, в любом порядке
        Assert.Empty(store.List("zzz"));
    }

    [Fact]
    public void Remove_ById_ByLemma_Clear()
    {
        var store = new FavoritesStore(DbPath);
        store.Toggle(Result("a1"));
        store.Toggle(Result("b2"));
        store.Toggle(Result("c3"));
        long id = store.List().First(r => r.Lemma == "b2").Id;
        Assert.True(store.Remove(id));
        Assert.False(store.Remove(id));
        Assert.True(store.RemoveByLemma("a1"));
        Assert.Equal(["c3"], store.List().Select(r => r.Lemma));
        store.Clear();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Changed_RaisedOnlyWhenSomethingChanged()
    {
        var store = new FavoritesStore(DbPath);
        int raised = 0;
        store.Changed += (_, _) => raised++;
        store.Toggle(Result("x1"));
        store.Remove(12345);
        store.RemoveByLemma("x1");
        store.Clear(); // уже пусто
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ExistingDatabase_WithPreviousSchema_Reads()
    {
        // таблица, созданная прежней версией, должна читаться без миграции
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE favorites (id INTEGER PRIMARY KEY, lemma TEXT NOT NULL UNIQUE, word TEXT NOT NULL, translation TEXT,
                    definition TEXT, sentence TEXT, sentence_ru TEXT, source_app TEXT, added_at TEXT NOT NULL);
                INSERT INTO favorites (lemma, word, added_at) VALUES ('old', 'old', '2026-10-21T10:00:00.0000000+03:00');
                """;
            cmd.ExecuteNonQuery();
        }
        var store = new FavoritesStore(DbPath);
        var r = Assert.Single(store.List());
        Assert.Equal("old", r.Lemma);
        Assert.Null(r.Translation);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.FromHours(3)), r.At);
    }
}

public sealed class HistoryStoreTests : UserDbFixture
{
    private HistoryStore Create(out SettingsStore settings)
    {
        settings = new SettingsStore(DbPath);
        return new HistoryStore(DbPath, settings, NullLogger<HistoryStore>.Instance);
    }

    [Fact]
    public void Record_StoresWordTranslationSentenceAndApp()
    {
        var history = Create(out _);
        history.Record(Result("run", "бежать", "I run daily.", word: "ran"));
        var r = Assert.Single(history.List());
        Assert.Equal(("run", "ran", "бежать, второй", "I run daily.", "game.exe"), (r.Lemma, r.Word, r.Translation, r.Sentence, r.SourceApp));
    }

    [Fact]
    public void Record_WordNotInDictionary_StillRecorded()
    {
        var history = Create(out _);
        history.Record(new LookupResult("zorbify", "zorbify", "Zorbify it.", null, null, "app.exe", TextSourceKind.Uia));
        var r = Assert.Single(history.List());
        Assert.Equal("zorbify", r.Lemma);
        Assert.Null(r.Translation);
    }

    [Fact]
    public void Record_SameWordInSameSentenceTwiceInARow_OneRecord()
    {
        var history = Create(out _);
        history.Record(Result("run", sentence: "S1."));
        history.Record(Result("run", sentence: "S1."));
        Assert.Equal(1, history.Count);
        history.Record(Result("run", sentence: "S2."));
        history.Record(Result("run", sentence: "S1."));
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void Record_Disabled_NothingWritten()
    {
        var history = Create(out var settings);
        settings.Set(SettingKeys.HistoryEnabled, "false");
        Assert.False(history.Enabled);
        history.Record(Result("run"));
        Assert.Equal(0, history.Count);
        settings.Set(SettingKeys.HistoryEnabled, "true");
        Assert.True(history.Enabled);
        history.Record(Result("run"));
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Record_EnabledByDefault() => Assert.True(Create(out _).Enabled);

    [Fact]
    public void Record_TrimsToMaxRecords_KeepsNewest()
    {
        var history = new HistoryStore(DbPath, new SettingsStore(DbPath), NullLogger<HistoryStore>.Instance, maxRecords: 10);
        for (int i = 0; i < 13; i++)
            history.Record(Result("w" + i, sentence: "s" + i));
        Assert.Equal(10, history.Count);
        Assert.Equal("w12", history.List()[0].Lemma);
        Assert.DoesNotContain(history.List(), r => r.Lemma is "w0" or "w1" or "w2");
        Assert.Contains(history.List(), r => r.Lemma == "w3");
    }

    [Fact]
    public void Remove_Clear_RaiseChanged()
    {
        var history = Create(out _);
        int raised = 0;
        history.Changed += (_, _) => raised++;
        history.Record(Result("a1"));
        history.Record(Result("b2"));
        Assert.True(history.Remove(history.List()[0].Id));
        history.Clear();
        history.Clear(); // уже пусто — события нет
        Assert.Equal(4, raised);
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void HistoryAndFavorites_ShareFileWithoutInterference()
    {
        var history = Create(out _);
        var favorites = new FavoritesStore(DbPath);
        history.Record(Result("run"));
        favorites.Add(history.List()[0]);
        history.Clear();
        Assert.True(favorites.IsFavorite("run"));
        Assert.Equal("a definition of run", favorites.List()[0].Definition);
    }
}

public sealed class AnkiExporterTests
{
    private static WordRecord Rec(string lemma, string? translation = null, string? definition = null, string? sentence = null) =>
        new(1, lemma, lemma, translation, definition, sentence, null, null, DateTimeOffset.Now);

    [Fact]
    public void Txt_FieldsSeparatedByTab_NoHeader() =>
        Assert.Equal("run\tбежать\tmove fast\tI run.\r\n", AnkiExporter.Build([Rec("run", "бежать", "move fast", "I run.")], ExportFormat.Txt));

    [Fact]
    public void Csv_FieldsSeparatedBySemicolon() =>
        Assert.Equal("run;бежать;move fast;I run.\r\n", AnkiExporter.Build([Rec("run", "бежать", "move fast", "I run.")], ExportFormat.Csv));

    [Fact]
    public void Csv_QuotesFieldsWithSeparatorOrQuote() =>
        Assert.Equal("a;\"x; y\";\"say \"\"hi\"\"\";\r\n", AnkiExporter.Build([Rec("a", "x; y", "say \"hi\"")], ExportFormat.Csv));

    [Fact]
    public void Txt_TabsAndNewlinesInsideFields_BecomeSpaces() =>
        Assert.Equal("a\tx y\tl1 l2\t\r\n", AnkiExporter.Build([Rec("a", "x\ty", "l1\r\nl2")], ExportFormat.Txt));

    [Fact]
    public void EmptyFields_KeepColumns() =>
        Assert.Equal("word;;;\r\n", AnkiExporter.Build([Rec("word")], ExportFormat.Csv));

    [Fact]
    public void Write_Csv_HasBom_Txt_HasNot_BothUtf8()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ht-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string csv = Path.Combine(dir, "a.csv"), txt = Path.Combine(dir, "a.txt");
            AnkiExporter.Write(csv, [Rec("run", "бежать")], ExportFormat.Csv);
            AnkiExporter.Write(txt, [Rec("run", "бежать")], ExportFormat.Txt);
            Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(csv).Take(3));
            Assert.NotEqual(0xEF, File.ReadAllBytes(txt)[0]);
            Assert.Equal("run\tбежать\t\t\r\n", File.ReadAllText(txt, Encoding.UTF8));
            Assert.Equal("run;бежать;;\r\n", File.ReadAllText(csv, Encoding.UTF8).TrimStart('﻿'));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class VoiceChoiceTests
{
    private static readonly VoiceInfo Zira = new("zira", "Microsoft Zira", "en-US");
    private static readonly VoiceInfo David = new("david", "Microsoft David", "en-US");
    private static readonly VoiceInfo Hazel = new("hazel", "Microsoft Hazel", "en-GB");
    private static readonly VoiceInfo Irina = new("irina", "Microsoft Irina", "ru-RU");

    [Fact]
    public void English_OnlyEnglish_UsFirst()
    {
        var english = VoiceChoice.English([Hazel, Irina, Zira, David]);
        Assert.Equal(["david", "zira", "hazel"], english.Select(v => v.Id));
    }

    [Fact]
    public void Resolve_Auto_FirstUsVoice() =>
        Assert.Equal("david", VoiceChoice.Resolve(VoiceChoice.English([Hazel, Zira, David]), null, out bool missing)?.Id);

    [Fact]
    public void Resolve_SavedInstalled_UsesSaved()
    {
        var voice = VoiceChoice.Resolve(VoiceChoice.English([Hazel, Zira, David]), "hazel", out bool missing);
        Assert.Equal("hazel", voice?.Id);
        Assert.False(missing);
    }

    [Fact]
    public void Resolve_SavedRemoved_FallsBackAndReports()
    {
        var voice = VoiceChoice.Resolve(VoiceChoice.English([Zira]), "gone", out bool missing);
        Assert.Equal("zira", voice?.Id);
        Assert.True(missing);
    }

    [Fact]
    public void Resolve_NoEnglishVoices_Null()
    {
        Assert.Null(VoiceChoice.Resolve(VoiceChoice.English([Irina]), null, out bool missing));
        Assert.False(missing);
    }
}
