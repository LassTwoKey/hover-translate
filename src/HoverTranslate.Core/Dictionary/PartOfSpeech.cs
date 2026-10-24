namespace HoverTranslate.Core.Dictionary;

/// <summary>Русские подписи частей речи (коды wiktextract).</summary>
public static class PartOfSpeech
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["noun"] = "сущ.",
        ["verb"] = "глаг.",
        ["adj"] = "прил.",
        ["adv"] = "нареч.",
        ["pron"] = "мест.",
        ["prep"] = "предл.",
        ["conj"] = "союз",
        ["intj"] = "межд.",
        ["det"] = "опред.",
        ["article"] = "артикль",
        ["num"] = "числ.",
        ["particle"] = "част.",
        ["phrase"] = "выраж.",
        ["prep_phrase"] = "выраж.",
        ["proverb"] = "посл.",
        ["contraction"] = "сокр.",
        ["abbrev"] = "сокр.",
        ["name"] = "имя собств.",
        ["postp"] = "послелог",
    };

    public static string Russian(string pos) => Names.TryGetValue(pos, out var name) ? name : pos;
}
