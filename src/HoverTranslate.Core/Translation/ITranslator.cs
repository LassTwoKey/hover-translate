namespace HoverTranslate.Core.Translation;

public enum TranslatorState
{
    /// <summary>Модель грузится в фоне; popup пока показывает только словарь.</summary>
    Loading,
    Ready,
    /// <summary>Модели нет или она не загрузилась — перевод предложений недоступен.</summary>
    Unavailable,
}

/// <summary>Перевод EN → RU (предложение или несколько абзацев).</summary>
public interface ITranslator
{
    TranslatorState State { get; }

    /// <summary>Модель загружена и готова.</summary>
    bool IsReady => State == TranslatorState.Ready;

    /// <summary>Перевод; длинный текст переводится по предложениям. Отмена — новым нажатием горячей клавиши.</summary>
    Task<string> TranslateAsync(string en, CancellationToken ct);
}

/// <summary>Перевод одного предложения моделью (без кэша и разбиения). Вызывается в фоновом потоке.</summary>
public interface ISentenceEngine : IDisposable
{
    string Translate(string sentence, CancellationToken ct);
}

/// <summary>opus-mt-en-ru: токенизатор + ONNX-модель.</summary>
public sealed class MarianEngine : ISentenceEngine
{
    private readonly MarianTokenizer _tokenizer;
    private readonly MarianModel _model;
    private readonly int _beams;

    /// <param name="beams">Ширина луча: 1 — greedy (быстрее), по умолчанию <see cref="MarianModel.DefaultBeams"/>.</param>
    public MarianEngine(string modelDir, int beams = MarianModel.DefaultBeams)
    {
        _beams = beams;
        _tokenizer = new MarianTokenizer(modelDir);
        // decoder_start_token_id = pad (config.json opus-mt).
        _model = new MarianModel(modelDir, _tokenizer.PadId, _tokenizer.EosId, _tokenizer.PadId);
    }

    public string Translate(string sentence, CancellationToken ct) =>
        _tokenizer.Decode(_model.Generate(_tokenizer.Encode(SentencePreprocessor.Clean(sentence)), _beams, ct));

    public void Dispose() => _model.Dispose();
}
