using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HoverTranslate.Core.Translation;

/// <summary>
/// Перевод текста: разбиение на абзацы и предложения, кэш по каждому предложению, модель — для промахов.
/// Модель грузится в фоне (<see cref="StartLoading"/>); до этого <see cref="State"/> = Loading.
/// Модель переводит по одному предложению за раз: отменённый запрос освобождает её на следующем шаге декодера.
/// </summary>
public sealed partial class Translator : ITranslator, IDisposable
{
    private readonly Func<ISentenceEngine> _loadEngine;
    private readonly ITranslationCache _cache;
    private readonly ILogger<Translator> _log;
    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private ISentenceEngine? _engine;
    private Task? _loading;
    private volatile TranslatorState _state = TranslatorState.Loading;

    public TranslatorState State => _state;

    /// <summary>Модель загрузилась (или не загрузилась) — вызывается в фоновом потоке.</summary>
    public event EventHandler<TranslatorState>? StateChanged;

    public Translator(ITranslationCache cache, ILogger<Translator> log)
        : this(() => new MarianEngine(AppPaths.ModelDir), cache, log)
    {
    }

    public Translator(Func<ISentenceEngine> loadEngine, ITranslationCache cache, ILogger<Translator> log)
    {
        _loadEngine = loadEngine;
        _cache = cache;
        _log = log;
    }

    /// <summary>Загрузка модели в фоне; повторный вызов возвращает ту же задачу.</summary>
    public Task StartLoading() => _loading ??= Task.Run(Load);

    private void Load()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var engine = _loadEngine();
            long loadMs = sw.ElapsedMilliseconds;
            engine.Translate("Hello.", CancellationToken.None); // прогрев: первый прогон заметно медленнее
            _engine = engine;
            _state = TranslatorState.Ready;
            LogLoaded(loadMs, sw.ElapsedMilliseconds - loadMs);
        }
        catch (Exception ex)
        {
            _state = TranslatorState.Unavailable;
            LogLoadFailed(ex, AppPaths.ModelDir);
        }
        StateChanged?.Invoke(this, _state);
    }

    public Task<string> TranslateAsync(string en, CancellationToken ct)
    {
        if (_engine is not { } engine)
            throw new InvalidOperationException(_state == TranslatorState.Loading ? "Модель перевода ещё загружается" : "Модель перевода недоступна");
        // Весь перевод — в пуле потоков: вызывающий обычно UI-поток.
        return Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            int hits = 0, total = 0;
            var result = new List<List<string>>();
            foreach (var paragraph in TextSegmenter.Split(en))
            {
                var translated = new List<string>();
                foreach (string sentence in paragraph)
                {
                    ct.ThrowIfCancellationRequested();
                    total++;
                    if (_cache.Get(sentence) is { } cached)
                    {
                        hits++;
                        translated.Add(cached);
                        continue;
                    }
                    string ru = await TranslateSentenceAsync(engine, sentence, ct).ConfigureAwait(false);
                    if (ru.Length > 0) _cache.Put(sentence, ru);
                    translated.Add(ru);
                }
                result.Add(translated);
            }
            string text = TextSegmenter.Join(result);
            LogTranslated(total, hits, sw.Elapsed.TotalMilliseconds);
            return text;
        }, ct);
    }

    private async Task<string> TranslateSentenceAsync(ISentenceEngine engine, string sentence, CancellationToken ct)
    {
        await _engineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return engine.Translate(sentence, ct);
        }
        finally
        {
            _engineGate.Release();
        }
    }

    public void Dispose()
    {
        // Дожидаемся загрузки, чтобы не оставить сессии ONNX неосвобождёнными.
        try
        {
            _loading?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // ошибка загрузки уже в логе
        }
        _engineGate.Wait(TimeSpan.FromSeconds(5));
        _engine?.Dispose();
        _engine = null;
    }

    [LoggerMessage(LogLevel.Information, "Модель перевода загружена за {LoadMs} мс, прогрев {WarmUpMs} мс")]
    private partial void LogLoaded(long loadMs, long warmUpMs);

    [LoggerMessage(LogLevel.Error, "Модель перевода не загрузилась ({Dir}); перевод предложений отключён")]
    private partial void LogLoadFailed(Exception ex, string dir);

    [LoggerMessage(LogLevel.Information, "Перевод: предложений {Total}, из кэша {Hits}, {Ms:0} мс")]
    private partial void LogTranslated(int total, int hits, double ms);
}
