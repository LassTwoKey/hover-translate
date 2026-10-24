using System.Net;
using System.Text;
using System.Text.Json;
using HoverTranslate.Core.Storage;
using HoverTranslate.Core.Translation;
using HoverTranslate.Core.Translation.Cloud;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoverTranslate.Core.Tests;

/// <summary>Общий каркас: настоящая SQLite-база настроек во временном файле и подставной HTTP-обработчик.</summary>
public abstract class CloudTestBase : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"ht-cloud-{Guid.NewGuid():N}.db");

    protected SettingsStore Settings { get; }

    protected FakeHandler Handler { get; } = new();

    protected CloudTestBase() => Settings = new SettingsStore(_db);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string f in new[] { _db, _db + "-wal", _db + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    protected GeminiClient NewClient() =>
        new(new HttpClient(Handler) { BaseAddress = GeminiClient.BaseAddress }, NullLogger<GeminiClient>.Instance);

    protected static string Reply(string text) =>
        JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });

    protected static string ApiError(string message) => JsonSerializer.Serialize(new { error = new { message } });

    /// <summary>Подставной сервер: ответы по очереди, а когда они кончились — последний; все запросы запоминаются.</summary>
    protected sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        private Func<HttpRequestMessage, HttpResponseMessage>? _last;

        public List<(Uri Uri, string Body, string? Key)> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string body) =>
            _responses.Enqueue(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        public void EnqueueException(Exception ex) => _responses.Enqueue(_ => throw ex);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("x-goog-api-key", out var keys);
            Requests.Add((request.RequestUri!, body, keys?.FirstOrDefault()));
            if (_responses.Count > 0) _last = _responses.Dequeue();
            return (_last ?? throw new InvalidOperationException("нет подготовленного ответа"))(request);
        }
    }
}

public sealed class SecretProtectorTests
{
    [Fact]
    public void RoundTrip_AndCiphertextHidesTheSecret()
    {
        const string key = "AIzaSy-test-key_123";
        string protectedValue = SecretProtector.Protect(key);

        Assert.DoesNotContain(key, protectedValue);
        Assert.DoesNotContain(key, Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue)));
        Assert.Equal(key, SecretProtector.Unprotect(protectedValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не base64!")]
    [InlineData("AAAA")]
    public void Unprotect_BadValue_ReturnsNull(string? value) => Assert.Null(SecretProtector.Unprotect(value));
}

public sealed class CloudKeyStoreTests : CloudTestBase
{
    [Fact]
    public void Save_StoresEncrypted_AndSurvivesNewInstance()
    {
        new CloudKeyStore(Settings).Save("  secret-key  ");

        string? raw = Settings.Get(SettingKeys.CloudKeyProtected);
        Assert.NotNull(raw);
        Assert.DoesNotContain("secret-key", raw);
        var reopened = new CloudKeyStore(new SettingsStore(Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid() + ".db")));
        Assert.False(reopened.HasStoredKey); // другая база — ключа нет
        Assert.Equal("secret-key", new CloudKeyStore(Settings).GetKey());
    }

    [Fact]
    public void Delete_RemovesKey()
    {
        var store = new CloudKeyStore(Settings);
        store.Save("k");
        Assert.True(store.HasStoredKey);

        store.Delete();

        Assert.False(store.HasStoredKey);
    }

    [Fact]
    public void Save_Empty_Throws() => Assert.Throws<ArgumentException>(() => new CloudKeyStore(Settings).Save("   "));

    [Fact]
    public void CorruptedStoredValue_MeansNoKey()
    {
        Settings.Set(SettingKeys.CloudKeyProtected, "garbage");
        Assert.False(new CloudKeyStore(Settings).HasStoredKey);
    }
}

public sealed class CloudUsageTests : CloudTestBase
{
    [Fact]
    public void Counts_PerDay_AndTotal()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0);
        var usage = new CloudUsage(Settings, () => now);

        usage.Increment();
        usage.Increment();
        Assert.Equal(2, usage.Today);

        now = now.AddDays(1);
        Assert.Equal(0, usage.Today);
        usage.Increment();
        Assert.Equal(1, usage.Today);
        Assert.Equal(3, usage.Total);
    }
}

public sealed class GeminiClientTests : CloudTestBase
{
    [Fact]
    public async Task Translate_SendsKeyInHeaderOnly_AndParsesAnswer()
    {
        Handler.Enqueue(HttpStatusCode.OK, Reply("  Привет!  "));

        string ru = await NewClient().TranslateAsync("SECRET-KEY", "gemini-test", "Hello!", [], CancellationToken.None);

        Assert.Equal("Привет!", ru);
        var request = Assert.Single(Handler.Requests);
        Assert.Equal("SECRET-KEY", request.Key);
        Assert.DoesNotContain("SECRET-KEY", request.Uri.ToString());
        Assert.Equal("/v1beta/models/gemini-test:generateContent", request.Uri.AbsolutePath);
        Assert.DoesNotContain("SECRET-KEY", request.Body);
    }

    [Fact]
    public async Task Translate_RequestHasInstructionTemperatureAndContext()
    {
        Handler.Enqueue(HttpStatusCode.OK, Reply("ok"));

        await NewClient().TranslateAsync("k", "m", "I did.", ["Did you do it?", "Well?"], CancellationToken.None);

        using var doc = JsonDocument.Parse(Handler.Requests[0].Body);
        var root = doc.RootElement;
        Assert.Contains("Russian", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.InRange(root.GetProperty("generationConfig").GetProperty("temperature").GetDouble(), 0, 0.5);
        string prompt = root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains("Did you do it?", prompt);
        Assert.Contains("Well?", prompt);
        Assert.EndsWith("I did.", prompt);
    }

    [Fact]
    public async Task Translate_NoContext_SendsTextAsIs()
    {
        Handler.Enqueue(HttpStatusCode.OK, Reply("ok"));

        await NewClient().TranslateAsync("k", "m", "Just this.", [], CancellationToken.None);

        using var doc = JsonDocument.Parse(Handler.Requests[0].Body);
        Assert.Equal("Just this.", doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(400, "API key not valid. Please pass a valid API key.", CloudErrorKind.InvalidKey)]
    [InlineData(401, "", CloudErrorKind.InvalidKey)]
    [InlineData(403, "", CloudErrorKind.InvalidKey)]
    [InlineData(404, "models/x is not found", CloudErrorKind.ModelNotFound)]
    [InlineData(429, "quota", CloudErrorKind.RateLimited)]
    [InlineData(500, "", CloudErrorKind.Server)]
    [InlineData(418, "", CloudErrorKind.Other)]
    public async Task Translate_HttpErrors_AreClassified(int status, string message, CloudErrorKind expected)
    {
        Handler.Enqueue((HttpStatusCode)status, ApiError(message));

        var ex = await Assert.ThrowsAsync<CloudException>(() => NewClient().TranslateAsync("k", "m", "x", [], CancellationToken.None));

        Assert.Equal(expected, ex.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ex.UserMessage));
    }

    [Fact]
    public async Task Translate_503_RetriesOnceThenSucceeds()
    {
        Handler.Enqueue(HttpStatusCode.ServiceUnavailable, ApiError("overloaded"));
        Handler.Enqueue(HttpStatusCode.OK, Reply("Привет"));

        string ru = await NewClient().TranslateAsync("k", "m", "Hi", [], CancellationToken.None);

        Assert.Equal("Привет", ru);
        Assert.Equal(2, Handler.Requests.Count);
    }

    [Fact]
    public async Task Translate_503Twice_IsServerError()
    {
        Handler.Enqueue(HttpStatusCode.ServiceUnavailable, ApiError("overloaded"));

        var ex = await Assert.ThrowsAsync<CloudException>(() => NewClient().TranslateAsync("k", "m", "Hi", [], CancellationToken.None));

        Assert.Equal(CloudErrorKind.Server, ex.Kind);
        Assert.Equal(2, Handler.Requests.Count);
    }

    [Fact]
    public async Task Translate_NetworkFailure_IsNetworkError()
    {
        Handler.EnqueueException(new HttpRequestException("no route"));

        var ex = await Assert.ThrowsAsync<CloudException>(() => NewClient().TranslateAsync("k", "m", "Hi", [], CancellationToken.None));

        Assert.Equal(CloudErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task Translate_UserCancellation_IsNotWrapped()
    {
        Handler.EnqueueException(new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewClient().TranslateAsync("k", "m", "Hi", [], cts.Token));
    }

    [Fact]
    public async Task Translate_InternalTimeout_IsTimeoutError()
    {
        // отмена не от пользователя — как сработавший таймаут запроса
        Handler.EnqueueException(new TaskCanceledException("timeout"));

        var ex = await Assert.ThrowsAsync<CloudException>(() => NewClient().TranslateAsync("k", "m", "Hi", [], CancellationToken.None));

        Assert.Equal(CloudErrorKind.Timeout, ex.Kind);
    }

    [Theory]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"SAFETY\"}]}")]
    public async Task Translate_NoText_IsBlocked(string json)
    {
        Handler.Enqueue(HttpStatusCode.OK, json);

        var ex = await Assert.ThrowsAsync<CloudException>(() => NewClient().TranslateAsync("k", "m", "x", [], CancellationToken.None));

        Assert.Equal(CloudErrorKind.Blocked, ex.Kind);
    }

    [Fact]
    public async Task Translate_GarbageBody_IsOtherError()
    {
        Handler.Enqueue(HttpStatusCode.OK, "<html>");

        var ex = await Assert.ThrowsAsync<CloudException>(() => NewClient().TranslateAsync("k", "m", "x", [], CancellationToken.None));

        Assert.Equal(CloudErrorKind.Other, ex.Kind);
    }
}

public sealed class CloudTranslatorTests : CloudTestBase
{
    private readonly MemoryCache _cache = new();
    private readonly CloudKeyStore _keys;
    private readonly CloudUsage _usage;
    private DateTime _now = new(2026, 10, 23, 12, 0, 0);

    public CloudTranslatorTests()
    {
        _keys = new CloudKeyStore(Settings, _ => null); // не зависит от GEMINI_API_KEY на машине
        _usage = new CloudUsage(Settings, () => _now);
    }

    private CloudTranslator Create() =>
        new(NewClient(), _keys, _usage, _cache, Settings, NullLogger<CloudTranslator>.Instance, () => _now);

    [Fact]
    public async Task NoKey_Throws_AndSendsNothing()
    {
        var ex = await Assert.ThrowsAsync<CloudException>(() => Create().TranslateAsync("Hello.", CancellationToken.None));

        Assert.Equal(CloudErrorKind.NoKey, ex.Kind);
        Assert.Empty(Handler.Requests);
    }

    [Fact]
    public async Task Translate_UsesCache_ForRepeatedText_AndCountsOnlyRealRequests()
    {
        _keys.Save("k");
        Handler.Enqueue(HttpStatusCode.OK, Reply("Привет."));
        var translator = Create();

        string first = await translator.TranslateAsync("Hello.", CancellationToken.None);
        string second = await translator.TranslateAsync("Hello.", CancellationToken.None);

        Assert.Equal("Привет.", first);
        Assert.Equal(first, second);
        Assert.Single(Handler.Requests);
        Assert.Equal(1, _usage.Today);
    }

    [Fact]
    public async Task Cache_IsSeparateFromLocalTranslations()
    {
        _keys.Save("k");
        _cache.Put("Hello.", "Здравствуйте (локально).");
        Handler.Enqueue(HttpStatusCode.OK, Reply("Привет (облако)."));

        string ru = await Create().TranslateAsync("Hello.", CancellationToken.None);

        Assert.Equal("Привет (облако).", ru);
        Assert.Equal("Здравствуйте (локально).", _cache.Get("Hello."));
    }

    [Fact]
    public async Task ModelNotFound_FallsBackToNextModel()
    {
        _keys.Save("k");
        Settings.Set(SettingKeys.CloudModel, "gemini-old-model");
        Handler.Enqueue(HttpStatusCode.NotFound, ApiError("not found"));
        Handler.Enqueue(HttpStatusCode.OK, Reply("Привет."));

        string ru = await Create().TranslateAsync("Hello.", CancellationToken.None);

        Assert.Equal("Привет.", ru);
        Assert.Contains("gemini-old-model", Handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains(CloudDefaults.FallbackModels[0], Handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task AllModelsNotFound_ThrowsModelNotFound()
    {
        _keys.Save("k");
        Handler.Enqueue(HttpStatusCode.NotFound, ApiError("not found"));

        var ex = await Assert.ThrowsAsync<CloudException>(() => Create().TranslateAsync("Hello.", CancellationToken.None));

        Assert.Equal(CloudErrorKind.ModelNotFound, ex.Kind);
        Assert.Equal(0, _usage.Today);
    }

    [Fact]
    public async Task RateLimit_IsNotRetriedWithAnotherModel_AndNotCounted()
    {
        _keys.Save("k");
        Handler.Enqueue(HttpStatusCode.TooManyRequests, ApiError("quota"));

        var ex = await Assert.ThrowsAsync<CloudException>(() => Create().TranslateAsync("Hello.", CancellationToken.None));

        Assert.Equal(CloudErrorKind.RateLimited, ex.Kind);
        Assert.Single(Handler.Requests);
        Assert.Equal(0, _usage.Today);
    }

    [Fact]
    public async Task PreviousLines_AreSentAsContext_OnlyRecentOnes()
    {
        _keys.Save("k");
        Handler.Enqueue(HttpStatusCode.OK, Reply("а"));
        var translator = Create();
        await translator.TranslateAsync("Line one.", CancellationToken.None);
        _now = _now.AddMinutes(1);
        await translator.TranslateAsync("Line two.", CancellationToken.None);
        _now = _now.AddMinutes(1);
        await translator.TranslateAsync("Line three.", CancellationToken.None);

        string prompt = PromptOf(Handler.Requests[2].Body);
        Assert.Contains("Line one.", prompt);
        Assert.Contains("Line two.", prompt);
        Assert.EndsWith("Line three.", prompt);

        _now = _now.AddMinutes(30); // всё устарело
        await translator.TranslateAsync("Line four.", CancellationToken.None);
        Assert.Equal("Line four.", PromptOf(Handler.Requests[3].Body));
    }

    [Fact]
    public async Task Test_ReportsSuccessAndReasons()
    {
        var translator = Create();
        Handler.Enqueue(HttpStatusCode.OK, Reply("Привет!"));
        Assert.Null(await translator.TestAsync("typed-key", CancellationToken.None));
        Assert.Equal("typed-key", Handler.Requests[0].Key);
        Assert.Equal(0, _usage.Today); // проверка не расходует счётчик

        Handler.Enqueue(HttpStatusCode.BadRequest, ApiError("API key not valid"));
        Assert.Contains("ключ", await translator.TestAsync("bad", CancellationToken.None));

        Assert.Equal("ключ не задан", await translator.TestAsync(null, CancellationToken.None));
    }

    [Fact]
    public void IsActive_NeedsSwitchAndKey()
    {
        var translator = Create();
        Assert.False(((ICloudTranslator)translator).IsActive);
        Settings.Set(SettingKeys.CloudEnabled, "true");
        Assert.False(((ICloudTranslator)translator).IsActive); // ключа нет
        _keys.Save("k");
        Assert.True(((ICloudTranslator)translator).IsActive);
    }

    private static string PromptOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
    }

    private sealed class MemoryCache : ITranslationCache
    {
        private readonly Dictionary<string, string> _items = [];

        public string? Get(string source, string? scope = null) => _items.GetValueOrDefault(Key(source, scope));

        public void Put(string source, string target, string? scope = null) => _items[Key(source, scope)] = target;

        private static string Key(string source, string? scope) => (scope ?? "") + "|" + TextSegmenter.Normalize(source);

        public int Count => _items.Count;

        public void Clear() => _items.Clear();
    }
}

public sealed class TranslationCacheScopeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ht-scope-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void LocalAndCloudEntries_DoNotMix()
    {
        using var cache = new SqliteTranslationCache(_path, NullLogger<SqliteTranslationCache>.Instance);

        cache.Put("Hello.", "локально");
        cache.Put("Hello.", "облако", "gemini-x");

        Assert.Equal("локально", cache.Get("Hello."));
        Assert.Equal("облако", cache.Get("Hello.", "gemini-x"));
        Assert.Null(cache.Get("Hello.", "gemini-y"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void LocalKey_IsUnchangedByScopeSupport() =>
        Assert.Equal(SqliteTranslationCache.Hash("Hello."), SqliteTranslationCache.Hash("  Hello. ", null));
}
