using HoverTranslate.Core.Storage;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.Core.Translation.Cloud;

/// <summary>Облачный перевод для карточки: что доступно и сам перевод одного предложения или области.</summary>
public interface ICloudTranslator
{
    /// <summary>Есть ключ Gemini (в настройках или в переменной окружения разработчика).</summary>
    bool HasKey { get; }

    /// <summary>Облако включено в настройках.</summary>
    bool IsEnabled { get; }

    /// <summary>Облако включено и ключ есть: предложения и области переводятся через Gemini сами.</summary>
    bool IsActive => IsEnabled && HasKey;

    /// <summary>Перевод через Gemini (с кэшем). Кэшированный ответ запроса не тратит.</summary>
    /// <exception cref="CloudException">Нет ключа, ошибка сети, лимит, отказ — карточка тогда остаётся с локальным переводом.</exception>
    Task<string> TranslateAsync(string text, CancellationToken ct);
}

/// <summary>
/// Gemini за кэшем: в облако уходят только предложения и области (не отдельные слова), повторный текст берётся из кэша.
/// Предыдущие реплики, которые пользователь недавно переводил, передаются как контекст — так диалог переводится связнее.
/// При 404 по модели пробуются запасные (<see cref="CloudDefaults.FallbackModels"/>); остальные ошибки отдаются вызывающему,
/// который показывает локальный перевод и подсказку. Каждый настоящий запрос увеличивает <see cref="Usage"/>.
/// </summary>
public sealed class CloudTranslator : ICloudTranslator
{
    /// <summary>Сколько последних реплик передавать как контекст и как долго они считаются «недавними».</summary>
    public const int MaxContextLines = 3;

    public static readonly TimeSpan ContextLifetime = TimeSpan.FromMinutes(5);

    private readonly GeminiClient _client;
    private readonly CloudKeyStore _keys;
    private readonly ITranslationCache _cache;
    private readonly AppSettings _settings;
    private readonly ILogger<CloudTranslator> _log;
    private readonly Func<DateTime> _now;
    private readonly Lock _contextLock = new();
    private readonly List<(string Text, DateTime At)> _recent = [];

    public CloudTranslator(GeminiClient client, CloudKeyStore keys, CloudUsage usage, ITranslationCache cache, ISettingsStore settings,
        ILogger<CloudTranslator> log, Func<DateTime>? now = null)
    {
        _client = client;
        _keys = keys;
        Usage = usage;
        _cache = cache;
        _settings = new AppSettings(settings);
        _log = log;
        _now = now ?? (() => DateTime.Now);
    }

    public CloudKeyStore Keys => _keys;

    public CloudUsage Usage { get; }

    public bool HasKey => _keys.HasKey;

    public bool IsEnabled => _settings.CloudEnabled;

    public async Task<string> TranslateAsync(string text, CancellationToken ct)
    {
        // Переводы строк (абзацы области) сохраняются: модель вернёт те же абзацы; для кэша пробелы нормализуются сами.
        string source = text.Replace("\r\n", "\n").Trim();
        if (source.Length == 0) return "";
        string primary = _settings.CloudModel;

        if (_cache.Get(source, primary) is { } cached)
        {
            Remember(source);
            return cached;
        }
        string key = _keys.GetKey() ?? throw new CloudException(CloudErrorKind.NoKey, "ключ Gemini не задан");

        var context = RecentContext(source);
        CloudException? notFound = null;
        foreach (string model in Candidates(primary))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string ru = await _client.TranslateAsync(key, model, source, context, ct).ConfigureAwait(false);
                Usage.Increment();
                _cache.Put(source, ru, primary);
                Remember(source);
                return ru;
            }
            catch (CloudException ex) when (ex.Kind == CloudErrorKind.ModelNotFound)
            {
                _log.LogWarning("Gemini: модель {Model} недоступна (404), пробуем запасную", model);
                notFound = ex;
            }
            catch (CloudException ex)
            {
                _log.LogWarning("Gemini: {Kind} — {Message}", ex.Kind, ex.UserMessage);
                throw;
            }
        }
        throw notFound ?? new CloudException(CloudErrorKind.ModelNotFound, $"модель «{primary}» недоступна");
    }

    /// <summary>
    /// Проверка ключа из окна настроек: настоящий короткий запрос. <paramref name="key"/> null — проверяется сохранённый.
    /// Возвращает null при успехе или текст причины. Запрос идёт мимо кэша и счётчика.
    /// </summary>
    public async Task<string?> TestAsync(string? key, CancellationToken ct)
    {
        key = string.IsNullOrWhiteSpace(key) ? _keys.GetKey() : key.Trim();
        if (key is null) return "ключ не задан";
        try
        {
            string primary = _settings.CloudModel;
            CloudException? last = null;
            foreach (string model in Candidates(primary))
            {
                try
                {
                    await _client.TranslateAsync(key, model, "Hello!", [], ct).ConfigureAwait(false);
                    return null;
                }
                catch (CloudException ex) when (ex.Kind == CloudErrorKind.ModelNotFound)
                {
                    last = ex;
                }
            }
            return last?.UserMessage ?? "модель недоступна";
        }
        catch (CloudException ex)
        {
            return ex.UserMessage;
        }
    }

    /// <summary>Основная модель и запасные без повторов.</summary>
    internal static IEnumerable<string> Candidates(string primary) =>
        new[] { primary }.Concat(CloudDefaults.FallbackModels).Distinct(StringComparer.OrdinalIgnoreCase);

    private List<string> RecentContext(string current)
    {
        var cutoff = _now() - ContextLifetime;
        lock (_contextLock)
        {
            _recent.RemoveAll(r => r.At < cutoff);
            return _recent.Where(r => r.Text != current).Select(r => r.Text).TakeLast(MaxContextLines).ToList();
        }
    }

    private void Remember(string source)
    {
        lock (_contextLock)
        {
            _recent.RemoveAll(r => r.Text == source);
            _recent.Add((source, _now()));
            if (_recent.Count > MaxContextLines + 2) _recent.RemoveAt(0);
        }
    }
}
