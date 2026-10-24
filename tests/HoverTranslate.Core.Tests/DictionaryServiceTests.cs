using HoverTranslate.Core.Dictionary;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoverTranslate.Core.Tests;

/// <summary>Поиск статьи на маленькой базе той же схемы, что собирает tools/DataBuilder.</summary>
public sealed class DictionaryServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ht-dict-{Guid.NewGuid():N}.db");
    private readonly DictionaryService _dict;

    public DictionaryServiceTests()
    {
        using (var db = new SqliteConnection($"Data Source={_path}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE lemmas (id INTEGER PRIMARY KEY, lemma TEXT, lemma_lc TEXT, first_word TEXT, word_count INT,
                                     ipa TEXT, cefr TEXT, zipf REAL, synonyms TEXT);
                CREATE TABLE senses (id INTEGER PRIMARY KEY, lemma_id INT, ord INT, pos TEXT, definition_en TEXT,
                                     translations_ru TEXT, examples TEXT);
                CREATE TABLE forms (form_lc TEXT, lemma_id INT, tags TEXT, PRIMARY KEY (form_lc, lemma_id)) WITHOUT ROWID;

                INSERT INTO lemmas VALUES (1, 'run', 'run', 'run', 1, '/ɹʌn/', 'A1', 5.49, '["sprint"]');
                INSERT INTO senses VALUES (1, 1, 0, 'verb', 'To move quickly on foot.', '["бегать","бежать"]', '["He runs fast."]');
                INSERT INTO senses VALUES (2, 1, 1, 'noun', 'An act of running.', NULL, NULL);
                INSERT INTO lemmas VALUES (2, 'ran', 'ran', 'ran', 1, NULL, NULL, 4.87, NULL);
                INSERT INTO senses VALUES (3, 2, 0, 'noun', '(nautical) Yarns coiled on a winch.', NULL, NULL);
                INSERT INTO forms VALUES ('ran', 1, 'past');
                INSERT INTO forms VALUES ('runs', 1, 'present singular third-person');

                INSERT INTO lemmas VALUES (3, 'better', 'better', 'better', 1, NULL, 'A1', 5.76, NULL);
                INSERT INTO senses VALUES (4, 3, 0, 'adj', 'Superior.', '["лучше"]', NULL);
                INSERT INTO lemmas VALUES (4, 'good', 'good', 'good', 1, NULL, 'A1', 6.12, NULL);
                INSERT INTO senses VALUES (5, 4, 0, 'adj', 'Positive.', '["хороший"]', NULL);
                INSERT INTO forms VALUES ('better', 4, 'comparative');

                INSERT INTO lemmas VALUES (5, 'Polish', 'polish', 'polish', 1, NULL, NULL, 4.0, NULL);
                INSERT INTO senses VALUES (6, 5, 0, 'adj', 'Of Poland.', '["польский"]', NULL);
                INSERT INTO lemmas VALUES (6, 'polish', 'polish', 'polish', 1, NULL, NULL, 4.0, NULL);
                INSERT INTO senses VALUES (7, 6, 0, 'verb', 'To make shiny.', '["полировать"]', NULL);

                -- «running» — своя статья (прил./сущ.), и форма глагола «run»
                INSERT INTO lemmas VALUES (7, 'running', 'running', 'running', 1, NULL, NULL, 5.23, NULL);
                INSERT INTO senses VALUES (8, 7, 0, 'adj', 'Consecutively; in a row.', '["подряд"]', NULL);
                INSERT INTO senses VALUES (9, 7, 1, 'noun', 'The act of running a race.', '["бег"]', NULL);
                INSERT INTO forms VALUES ('running', 1, 'participle present');

                -- фразовые глаголы
                INSERT INTO lemmas VALUES (10, 'give', 'give', 'give', 1, NULL, 'A1', 5.71, NULL);
                INSERT INTO senses VALUES (10, 10, 0, 'verb', 'To transfer possession.', '["давать"]', NULL);
                INSERT INTO forms VALUES ('gave', 10, 'past');
                INSERT INTO lemmas VALUES (11, 'give up', 'give up', 'give', 2, NULL, 'B1', 5.63, NULL);
                INSERT INTO senses VALUES (11, 11, 0, 'verb', 'To stop trying.', '["сдаваться","бросать"]', NULL);
                INSERT INTO forms VALUES ('gave up', 11, 'past');
                INSERT INTO lemmas VALUES (12, 'up', 'up', 'up', 1, NULL, 'A1', 6.4, NULL);
                INSERT INTO senses VALUES (12, 12, 0, 'adv', 'Towards the top.', '["вверх"]', NULL);
                INSERT INTO lemmas VALUES (13, 'look', 'look', 'look', 1, NULL, 'A1', 5.9, NULL);
                INSERT INTO senses VALUES (13, 13, 0, 'verb', 'To try to see.', '["смотреть"]', NULL);
                INSERT INTO forms VALUES ('looking', 13, 'participle present');
                INSERT INTO lemmas VALUES (14, 'look forward to', 'look forward to', 'look', 3, NULL, 'B1', 5.04, NULL);
                INSERT INTO senses VALUES (14, 14, 0, 'verb', 'To anticipate with pleasure.', '["с нетерпением ждать"]', NULL);
                INSERT INTO lemmas VALUES (15, 'look it', 'look it', 'look', 2, NULL, NULL, 3.0, NULL);
                INSERT INTO senses VALUES (15, 15, 0, 'verb', '(slang) Some junk phrase.', NULL, NULL);
                INSERT INTO lemmas VALUES (16, 'up and', 'up and', 'up', 2, NULL, NULL, 6.35, NULL);
                INSERT INTO senses VALUES (16, 16, 0, 'adv', '(colloquial) Abruptly.', '["взять и"]', NULL);

                -- значения по контексту
                INSERT INTO lemmas VALUES (20, 'bank', 'bank', 'bank', 1, NULL, 'A2', 5.4, NULL);
                INSERT INTO senses VALUES (20, 20, 0, 'noun', 'An institution where one can place and borrow money.', '["банк"]', NULL);
                INSERT INTO senses VALUES (21, 20, 1, 'noun', 'The edge of a river or lake.', '["берег"]', '["We sat on the bank of the river."]');
                INSERT INTO senses VALUES (22, 20, 2, 'verb', '(obsolete) To sit upon a bench.', '["сидеть"]', NULL);
                INSERT INTO senses VALUES (23, 20, 3, 'verb', 'To deposit money in a bank.', '["класть в банк"]', NULL);

                -- для правил
                INSERT INTO lemmas VALUES (30, 'cat', 'cat', 'cat', 1, NULL, 'A1', 5.0, NULL);
                INSERT INTO senses VALUES (30, 30, 0, 'noun', 'A small domesticated feline.', '["кошка"]', NULL);
                INSERT INTO lemmas VALUES (31, 'stop', 'stop', 'stop', 1, NULL, 'A1', 5.5, NULL);
                INSERT INTO senses VALUES (31, 31, 0, 'verb', 'To cease moving.', '["останавливаться"]', NULL);
                INSERT INTO lemmas VALUES (32, 'happy', 'happy', 'happy', 1, NULL, 'A1', 5.6, NULL);
                INSERT INTO senses VALUES (32, 32, 0, 'adj', 'Feeling joy.', '["счастливый"]', NULL);
                INSERT INTO lemmas VALUES (33, 'bake', 'bake', 'bake', 1, NULL, 'A2', 4.0, NULL);
                INSERT INTO senses VALUES (33, 33, 0, 'verb', 'To cook in an oven.', '["печь"]', NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        _dict = new DictionaryService(_path, NullLogger<DictionaryService>.Instance);
    }

    [Fact]
    public void ExactLemma_FullEntry()
    {
        var m = _dict.Lookup("run")!;

        Assert.Equal("run", m.Lemma);
        Assert.Equal("/ɹʌn/", m.Entry.Ipa);
        Assert.Equal("A1", m.Entry.Cefr);
        Assert.Equal(["sprint"], m.Entry.Synonyms);
        Assert.Equal(2, m.Entry.Senses.Count);
        Assert.Equal(["бегать", "бежать"], m.Entry.Senses[0].TranslationsRu);
        Assert.Equal(["He runs fast."], m.Entry.Senses[0].Examples);
        Assert.Empty(m.Entry.Senses[1].TranslationsRu);
    }

    [Fact]
    public void InflectedForm_FoundViaFormsTable() => Assert.Equal("run", _dict.Lookup("runs")!.Lemma);

    [Fact]
    public void FormWithTranslation_BeatsRareHomonymWithout() => Assert.Equal("run", _dict.Lookup("ran")!.Lemma);

    [Fact]
    public void OwnLemma_BeatsBaseForm() => Assert.Equal("better", _dict.Lookup("better")!.Lemma);

    [Fact]
    public void CaseMatters_ForHomographs()
    {
        Assert.Equal("Polish", _dict.Lookup("Polish")!.Lemma);
        Assert.Equal("polish", _dict.Lookup("polish")!.Lemma);
    }

    [Fact]
    public void UpperCaseOnScreen_FindsLowercaseLemma() => Assert.Equal("run", _dict.Lookup("RUN")!.Lemma);

    [Fact]
    public void Unknown_ReturnsNull() => Assert.Null(_dict.Lookup("qwertyuiop"));

    // ------------------------------------------------------------------ правила

    [Theory]
    [InlineData("cats", "cat")]
    [InlineData("cat's", "cat")]
    [InlineData("CATS", "cat")]
    [InlineData("stopped", "stop")]
    [InlineData("stopping", "stop")]
    [InlineData("happier", "happy")]
    [InlineData("happiest", "happy")]
    [InlineData("baking", "bake")]
    [InlineData("baked", "bake")]
    public void Rules_FindLemmaForUnlistedForm(string word, string lemma) => Assert.Equal(lemma, _dict.Lookup(word)!.Lemma);

    // ------------------------------------------------------------------ контекст: часть речи

    [Fact]
    public void Running_WithoutContext_OwnEntry() => Assert.Equal("running", _dict.Lookup("running")!.Lemma);

    [Fact]
    public void Running_AfterAuxiliary_IsVerbRun()
    {
        const string s = "She was running late for the meeting.";
        Assert.Equal("run", Words(_dict.Lookup("running", s, s.IndexOf("running", StringComparison.Ordinal)))[0]);
    }

    [Fact]
    public void Running_AfterArticle_StaysNoun()
    {
        const string s = "The running of the race was fun.";
        Assert.Equal("running", Words(_dict.Lookup("running", s, s.IndexOf("running", StringComparison.Ordinal)))[0]);
    }

    // ------------------------------------------------------------------ фразы

    [Fact]
    public void PhrasalVerb_InflectedViaForms_ShownFirst()
    {
        const string s = "She gave up and went home.";
        var matches = _dict.Lookup("gave", s, s.IndexOf("gave", StringComparison.Ordinal));

        Assert.Equal(["give up", "give"], Words(matches));
        Assert.Equal(MatchKind.Phrase, matches[0].Kind);
        Assert.Equal(MatchKind.Word, matches[1].Kind);
    }

    [Fact]
    public void PhrasalVerb_FoundFromSecondWord()
    {
        const string s = "She gave up and went home.";
        Assert.Equal(["give up", "up"], Words(_dict.Lookup("up", s, s.IndexOf("up", StringComparison.Ordinal))));
    }

    [Fact]
    public void ThreeWordPhrase_FirstWordLemmatized()
    {
        const string s = "I'm looking forward to the weekend.";
        Assert.Equal(["look forward to", "look"], Words(_dict.Lookup("looking", s, s.IndexOf("looking", StringComparison.Ordinal))));
    }

    [Fact]
    public void Phrase_NotAcrossPunctuation()
    {
        const string s = "Whatever she gave, up there it was cold.";
        Assert.Equal(["give"], Words(_dict.Lookup("gave", s, s.IndexOf("gave", StringComparison.Ordinal))));
    }

    [Fact]
    public void FunctionWordPhrase_DroppedWhenIdiomaticFound()
    {
        const string s = "She gave up and went home.";
        Assert.Equal(["give up", "up"], Words(_dict.Lookup("up", s, s.IndexOf("up", StringComparison.Ordinal))));
    }

    [Fact]
    public void FunctionWordPhrase_ShownAfterWord()
    {
        const string s = "He up and left.";
        Assert.Equal(["up", "up and"], Words(_dict.Lookup("up", s, s.IndexOf("up", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("give up", true)]
    [InlineData("look forward to", true)]
    [InlineData("river bank", true)]
    [InlineData("up and", false)]
    [InlineData("at it", false)]
    [InlineData("see you", false)]
    [InlineData("give me", false)]
    public void IsIdiomatic(string phrase, bool expected) => Assert.Equal(expected, DictionaryService.IsIdiomatic(phrase));

    [Fact]
    public void Phrase_WithoutTranslation_Ignored()
    {
        const string s = "Look it up.";
        Assert.Equal(["look"], Words(_dict.Lookup("Look", s, 0)));
    }

    [Fact]
    public void Phrase_UnknownOffset_FallsBackToTextSearch()
    {
        const string s = "She gave up and went home.";
        Assert.Equal(["give up", "give"], Words(_dict.Lookup("gave", s, -1)));
    }

    // ------------------------------------------------------------------ контекст: выбор значения

    [Fact]
    public void Senses_RankedByContextOverlap()
    {
        const string s = "They walked along the river bank at sunset.";
        var entry = _dict.Lookup("bank", s, s.IndexOf("bank", StringComparison.Ordinal))[0].Entry;
        Assert.Equal(["берег"], entry.Senses[0].TranslationsRu);
    }

    [Fact]
    public void Senses_MoneyContext_KeepsInstitution()
    {
        const string s = "I need to borrow money from the bank.";
        var entry = _dict.Lookup("bank", s, s.IndexOf("bank", StringComparison.Ordinal))[0].Entry;
        Assert.Equal(["банк"], entry.Senses[0].TranslationsRu);
    }

    [Fact]
    public void Senses_ObsoleteSensePushedDown()
    {
        var senses = _dict.Lookup("bank")!.Entry.Senses;
        Assert.StartsWith("(obsolete)", senses[^1].DefinitionEn, StringComparison.Ordinal);
    }

    private static List<string> Words(IReadOnlyList<DictionaryMatch> matches) => matches.Select(m => m.Lemma).ToList();

    [Fact]
    public void MissingDatabase_NotAvailable()
    {
        using var missing = new DictionaryService(Path.Combine(Path.GetTempPath(), "нет-такого.db"), NullLogger<DictionaryService>.Instance);
        Assert.False(missing.IsAvailable);
        Assert.Null(missing.Lookup("run"));
    }

    public void Dispose()
    {
        _dict.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
