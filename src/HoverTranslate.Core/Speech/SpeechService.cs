using HoverTranslate.Core.Storage;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace HoverTranslate.Core.Speech;

public interface ISpeechService
{
    /// <summary>Произнести текст английским голосом; предыдущая фраза прерывается.</summary>
    Task SpeakAsync(string text);

    /// <summary>Установленные английские голоса (en-US первыми). Список читается из системы при каждом обращении.</summary>
    IReadOnlyList<VoiceInfo> Voices { get; }

    /// <summary>Голос, которым сейчас читается текст, с учётом настройки; null — английского голоса в системе нет.</summary>
    VoiceInfo? CurrentVoice { get; }
}

/// <summary>Озвучка через Windows.Media.SpeechSynthesis (офлайн-голоса Windows). Голос — настройка <see cref="SettingKeys.SpeechVoice"/>.</summary>
public sealed partial class SpeechService : ISpeechService, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ILogger<SpeechService> _log;
    private readonly SpeechSynthesizer _synth = new();
    private readonly MediaPlayer _player = new() { AutoPlay = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _warnedMissing;

    public SpeechService(ISettingsStore settings, ILogger<SpeechService> log)
    {
        _settings = settings;
        _log = log;
        if (Voices.Count == 0) LogNoVoice();
    }

    public IReadOnlyList<VoiceInfo> Voices =>
        VoiceChoice.English(SpeechSynthesizer.AllVoices.Select(v => new VoiceInfo(v.Id, v.DisplayName, v.Language)));

    public VoiceInfo? CurrentVoice => VoiceChoice.Resolve(Voices, _settings.Get(SettingKeys.SpeechVoice), out _);

    public async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ApplyVoice();
            var stream = await _synth.SynthesizeTextToStreamAsync(text);
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
        }
        catch (Exception ex)
        {
            LogFailed(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Выбранный голос; если он удалён из системы — автоматический. Без английских голосов остаётся голос Windows по умолчанию.</summary>
    private void ApplyVoice()
    {
        var voices = Voices;
        var chosen = VoiceChoice.Resolve(voices, _settings.Get(SettingKeys.SpeechVoice), out bool missing);
        if (missing && !_warnedMissing)
        {
            _warnedMissing = true;
            LogVoiceMissing(_settings.Get(SettingKeys.SpeechVoice) ?? "");
        }
        if (chosen is null) return;
        var native = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == chosen.Id);
        if (native is not null) _synth.Voice = native;
    }

    public void Dispose()
    {
        _player.Dispose();
        _synth.Dispose();
        _gate.Dispose();
    }

    [LoggerMessage(LogLevel.Warning, "Английский голос не установлен — используется голос Windows по умолчанию")]
    private partial void LogNoVoice();

    [LoggerMessage(LogLevel.Warning, "Выбранный голос {Id} больше не установлен — выбран голос автоматически")]
    private partial void LogVoiceMissing(string id);

    [LoggerMessage(LogLevel.Warning, "Не удалось озвучить текст")]
    private partial void LogFailed(Exception ex);
}
