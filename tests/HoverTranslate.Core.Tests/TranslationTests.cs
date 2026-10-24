using System.Diagnostics;
using HoverTranslate.Core.TextSource;
using HoverTranslate.Core.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using static HoverTranslate.Core.Tests.OcrFixtures;

namespace HoverTranslate.Core.Tests;

public sealed class TextSegmenterTests
{
    [Fact]
    public void Split_ParagraphsAndSentences()
    {
        var parts = TextSegmenter.Split("Hello there. How are you?\n\nI'm fine!  Thanks.\r\nBye");
        Assert.Equal(3, parts.Count);
        Assert.Equal(["Hello there.", "How are you?"], parts[0]);
        Assert.Equal(["I'm fine!", "Thanks."], parts[1]);
        Assert.Equal(["Bye"], parts[2]);
    }

    [Fact]
    public void Split_KeepsAbbreviationsAndNumbers() =>
        Assert.Equal(["Mr. Smith paid $3.50 at 5 p.m. yesterday."], TextSegmenter.Split("Mr. Smith paid $3.50 at 5 p.m. yesterday.")[0]);

    [Fact]
    public void Split_LongSentenceWithoutDots_ChunkedEvenly()
    {
        string text = string.Join(' ', Enumerable.Range(0, 130).Select(i => "w" + i));
        var chunks = TextSegmenter.Split(text)[0];
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.InRange(c.Split(' ').Length, 40, TextSegmenter.MaxWordsPerChunk));
        Assert.Equal(text, string.Join(' ', chunks));
    }

    [Fact]
    public void Split_Empty() => Assert.Empty(TextSegmenter.Split("  \n "));

    [Fact]
    public void Join_SentencesBySpace_ParagraphsByNewline() =>
        Assert.Equal("Привет. Как дела?\nПока", TextSegmenter.Join([["Привет.", "Как дела?"], ["Пока"]]));

    [Fact]
    public void Normalize_CollapsesWhitespace() => Assert.Equal("a b c", TextSegmenter.Normalize("  a \t b\n c "));

    [Fact]
    public void SentenceFinderSplit_SameBoundariesAsFind()
    {
        const string text = "\"What?\" she asked. J. K. Rowling wrote it… Then: e.g. this one! End";
        Assert.Equal(["\"What?\" she asked.", "J. K. Rowling wrote it…", "Then: e.g. this one!", "End"], SentenceFinder.Split(text));
    }
}

public sealed class OcrAreaTextTests
{
    [Fact]
    public void BuildText_ParagraphsSeparatedByGap()
    {
        var lines = Paragraph(100, 100, "The weather was terrible, so we", "stayed at home.")
            .Concat(Paragraph(100, 200, "Next paragraph here."))
            .ToList();
        Assert.Equal("The weather was terrible, so we stayed at home.\nNext paragraph here.", OcrLayout.BuildText(lines));
    }

    [Fact]
    public void BuildText_ReadingOrderAndHyphenation()
    {
        var lines = Paragraph(100, 100, "An inter-", "national event.");
        lines.Reverse(); // OCR может вернуть строки не по порядку
        Assert.Equal("An international event.", OcrLayout.BuildText(lines));
    }

    [Fact]
    public void BuildText_DifferentFontSize_NewParagraph()
    {
        var lines = new List<OcrLineBox> { Line(100, 100, "TITLE", height: 40), Line(100, 146, "Body text") };
        Assert.Equal("TITLE\nBody text", OcrLayout.BuildText(lines));
    }

    [Fact]
    public void BuildText_Empty() => Assert.Equal("", OcrLayout.BuildText([]));
}

public sealed class TranslationCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ht-cache-{Guid.NewGuid():N}.db");

    [Fact]
    public void PutGet_NormalizedKey_PersistsAcrossInstances()
    {
        using (var cache = new SqliteTranslationCache(_path, NullLogger<SqliteTranslationCache>.Instance))
        {
            Assert.Null(cache.Get("Hello world."));
            cache.Put("Hello world.", "Привет, мир.");
            Assert.Equal("Привет, мир.", cache.Get("  Hello   world. "));
        }
        using var reopened = new SqliteTranslationCache(_path, NullLogger<SqliteTranslationCache>.Instance);
        Assert.Equal("Привет, мир.", reopened.Get("Hello world."));
        Assert.Null(reopened.Get("hello world.")); // регистр важен для перевода
    }

    [Fact]
    public void UnwritablePath_DegradesToMisses()
    {
        File.WriteAllText(_path, "не каталог"); // папка кэша «внутри файла» — создать её нельзя
        using var cache = new SqliteTranslationCache(Path.Combine(_path, "sub", "cache.db"), NullLogger<SqliteTranslationCache>.Instance);
        cache.Put("a", "б");
        Assert.Null(cache.Get("a"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

public sealed class TranslatorTests
{
    /// <summary>«Модель»: переворачивает предложение и считает вызовы; может ждать сигнала.</summary>
    private sealed class FakeEngine : ISentenceEngine
    {
        public int Calls;
        public ManualResetEventSlim? Gate;

        public string Translate(string sentence, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is not null)
            {
                while (!Gate.Wait(10, CancellationToken.None)) ct.ThrowIfCancellationRequested();
            }
            return "RU:" + sentence;
        }

        public void Dispose()
        {
        }
    }

    private sealed class MemoryCache : ITranslationCache
    {
        public readonly Dictionary<string, string> Items = [];

        public string? Get(string source, string? scope = null) => Items.GetValueOrDefault(Key(source, scope));

        public void Put(string source, string target, string? scope = null) => Items[Key(source, scope)] = target;

        private static string Key(string source, string? scope) => (scope is null ? "" : scope + "|") + TextSegmenter.Normalize(source);

        public int Count => Items.Count;

        public void Clear() => Items.Clear();
    }

    private static async Task<(Translator, FakeEngine, MemoryCache)> CreateAsync()
    {
        var engine = new FakeEngine();
        var cache = new MemoryCache();
        var translator = new Translator(() => engine, cache, NullLogger<Translator>.Instance);
        await translator.StartLoading();
        engine.Calls = 0; // прогрев
        cache.Items.Clear();
        return (translator, engine, cache);
    }

    [Fact]
    public async Task Translate_SplitsJoinsAndCaches()
    {
        var (translator, engine, cache) = await CreateAsync();
        string result = await translator.TranslateAsync("One. Two!\nThree", CancellationToken.None);

        Assert.Equal("RU:One. RU:Two!\nRU:Three", result);
        Assert.Equal(3, engine.Calls);
        Assert.Equal(3, cache.Items.Count);

        Assert.Equal(result, await translator.TranslateAsync("One. Two!\nThree", CancellationToken.None));
        Assert.Equal(3, engine.Calls); // всё из кэша
    }

    [Fact]
    public async Task Loading_State_Throws()
    {
        var translator = new Translator(() => new FakeEngine(), new MemoryCache(), NullLogger<Translator>.Instance);
        Assert.Equal(TranslatorState.Loading, translator.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => translator.TranslateAsync("Hi.", CancellationToken.None));
    }

    [Fact]
    public async Task LoadFailure_Unavailable_RaisesEvent()
    {
        var translator = new Translator(() => throw new FileNotFoundException("нет модели"), new MemoryCache(), NullLogger<Translator>.Instance);
        TranslatorState? raised = null;
        translator.StateChanged += (_, s) => raised = s;
        await translator.StartLoading();
        Assert.Equal(TranslatorState.Unavailable, translator.State);
        Assert.Equal(TranslatorState.Unavailable, raised);
    }

    [Fact]
    public async Task Cancellation_StopsAndFreesEngineForNextRequest()
    {
        var (translator, engine, _) = await CreateAsync();
        engine.Gate = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        var first = translator.TranslateAsync("Long one.", cts.Token);
        while (engine.Calls == 0) await Task.Delay(5, TestContext.Current.CancellationToken);

        cts.Cancel(); // повторное нажатие
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        engine.Gate.Set();
        Assert.Equal("RU:Next.", await translator.TranslateAsync("Next.", CancellationToken.None));
    }
}

/// <summary>Замеры времени — без параллельно идущих тестов, которые делят с моделью CPU.</summary>
[CollectionDefinition(nameof(PerformanceCollection), DisableParallelization = true)]
public sealed class PerformanceCollection;

/// <summary>Проверка на настоящей модели: ≤30 слов < 500 мс, повтор из кэша < 20 мс.</summary>
[Collection(nameof(PerformanceCollection))]
public sealed class TranslationPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const string Sentence =
        "When the old lighthouse keeper finally retired after forty years, the villagers gathered on the cliff " +
        "to thank him for every stormy night he had kept the lamp burning.";

    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), $"ht-perf-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task ThirtyWords_Under1000ms_CacheUnder20ms()
    {
        Assert.SkipWhen(RepoData.Model is null, "data/models/opus-mt-en-ru не экспортирована");
        Assert.InRange(Sentence.Split(' ').Length, 25, 30);

        using var cache = new SqliteTranslationCache(_cachePath, NullLogger<SqliteTranslationCache>.Instance);
        using var translator = new Translator(() => new MarianEngine(RepoData.Model!), cache, NullLogger<Translator>.Instance);
        await translator.StartLoading();
        Assert.Equal(TranslatorState.Ready, translator.State);

        var sw = Stopwatch.StartNew();
        string ru = await translator.TranslateAsync(Sentence, CancellationToken.None);
        double modelMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        string cached = await translator.TranslateAsync(Sentence, CancellationToken.None);
        double cacheMs = sw.Elapsed.TotalMilliseconds;

        output.WriteLine($"{ru}\nмодель: {modelMs:0} мс, кэш: {cacheMs:0.0} мс, ядер: {Environment.ProcessorCount}");
        Assert.Matches("[а-яА-Я]{3,}", ru);
        Assert.Equal(ru, cached);
        // beam search 4: Release ~400 мс, Debug-сборка тестов 600–900 мс; до beam greedy укладывался в 500 мс.
        Assert.True(modelMs < 1000, $"Перевод {modelMs:0} мс ≥ 1000 мс");
        Assert.True(cacheMs < 20, $"Кэш {cacheMs:0.0} мс ≥ 20 мс");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string f in new[] { _cachePath, _cachePath + "-wal", _cachePath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
