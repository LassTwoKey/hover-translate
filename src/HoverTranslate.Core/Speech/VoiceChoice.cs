namespace HoverTranslate.Core.Speech;

/// <summary>Установленный голос озвучки.</summary>
public sealed record VoiceInfo(string Id, string DisplayName, string Language);

/// <summary>Выбор голоса: сохранённый, если он ещё установлен, иначе en-US, иначе любой английский.</summary>
public static class VoiceChoice
{
    /// <summary>Английские голоса: en-US первыми, затем остальные en-*, внутри групп — по имени.</summary>
    public static IReadOnlyList<VoiceInfo> English(IEnumerable<VoiceInfo> all) => all
        .Where(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        .OrderBy(v => v.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(v => v.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <param name="english">Результат <see cref="English"/>.</param>
    /// <param name="savedId">Идентификатор из настройки; пусто — автоматический выбор.</param>
    /// <param name="savedMissing">Голос задан, но в системе его больше нет (удалён) — выбран автоматически.</param>
    public static VoiceInfo? Resolve(IReadOnlyList<VoiceInfo> english, string? savedId, out bool savedMissing)
    {
        savedMissing = false;
        if (!string.IsNullOrEmpty(savedId))
        {
            var saved = english.FirstOrDefault(v => v.Id == savedId);
            if (saved is not null) return saved;
            savedMissing = true;
        }
        return english.FirstOrDefault();
    }
}
