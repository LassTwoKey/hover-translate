using HoverTranslate.Core.Models;
using HoverTranslate.Core.TextSource;

namespace HoverTranslate.Core.Tests;

public sealed class OcrCorrectorTests
{
    // Словарь-заглушка: слово -> zipf. Всё остальное «не слово».
    private static readonly Dictionary<string, double> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["your"] = 6.3, ["history"] = 5.0, ["holiday"] = 4.8, ["gently"] = 4.3, ["heavy"] = 5.0, ["slowly"] = 4.8,
        ["vaguely"] = 3.1, ["garden"] = 4.7, ["candle"] = 3.9, ["mug"] = 3.6, ["of"] = 7.3, ["the"] = 7.7,
        ["small"] = 5.4, ["jacket"] = 4.5, ["game"] = 5.8, ["same"] = 5.8, ["rare"] = 1.2, ["rarely"] = 1.5, ["well"] = 6.0, ["known"] = 5.0, ["lur"] = 1.0,
    };

    private static readonly OcrCorrector Corrector = new(w => Words.TryGetValue(w, out double z) ? z : null);

    [Theory]
    [InlineData("historu", "history")]
    [InlineData("holidau", "holiday")]
    [InlineData("gentlu", "gently")]
    [InlineData("heavu", "heavy")]
    [InlineData("slowlu", "slowly")]
    [InlineData("gour", "your")]
    [InlineData("uour", "your")]
    [InlineData("Gardan", "Garden")]
    [InlineData("candla", "candle")]
    [InlineData("weguelu", "vaguely")]        // три замены
    [InlineData("gaRden", "garden")]           // смешанный регистр
    [InlineData("Uour", "Your")]               // заглавная буква переносится
    public void FixesConfusedLetters(string raw, string expected) => Assert.Equal(expected, Corrector.CorrectToken(raw));

    [Theory]
    [InlineData("historu,", "history,")]
    [InlineData("(holidau)", "(holiday)")]
    [InlineData("\"gour", "\"your")]
    public void KeepsPunctuationAround(string raw, string expected) => Assert.Equal(expected, Corrector.CorrectToken(raw));

    [Theory]
    [InlineData("history")]      // уже слово
    [InlineData("game")]
    [InlineData("same")]         // тоже слово: словарём не отличить
    [InlineData("Fenwick")]    // имя: замены не дают слова
    [InlineData("Marlowe")]
    [InlineData("Dana's")]       // апостроф: не трогаем
    [InlineData("ur")]           // короче трёх букв
    [InlineData("OF")]           // капс не трогаем
    [InlineData("12.5")]
    [InlineData("...")]
    public void LeavesOtherTokensAlone(string raw) => Assert.Equal(raw, Corrector.CorrectToken(raw));

    [Fact]
    public void RareReplacementIsNotAccepted()
    {
        // «rarelu» -> «rarely» (zipf 1.5) ниже порога: слишком редкое слово, чтобы доверять замене
        Assert.Equal("rarelu", Corrector.CorrectToken("rarelu"));
    }

    [Fact]
    public void SingleR_BecomesArticle() => Assert.Equal("A", Corrector.CorrectToken("R"));

    [Fact]
    public void HyphenParts_AreFixedSeparately() => Assert.Equal("small-history", Corrector.CorrectToken("small-historu"));

    [Fact]
    public void Correct_KeepsBoundsAndLength()
    {
        var box = new ScreenRect(10, 20, 80, 25);
        var lines = new[] { new OcrLineBox([new OcrWordBox("historu", box), new OcrWordBox("mug", box)]) };
        var fixedLines = Corrector.Correct(lines, out var changes);

        Assert.Equal("history", fixedLines[0].Words[0].Text);
        Assert.Equal(box, fixedLines[0].Words[0].Bounds);
        Assert.Same(lines[0].Words[1], fixedLines[0].Words[1]);
        Assert.Equal([("historu", "history")], changes);
    }

    [Fact]
    public void Correct_UnchangedLine_IsSameInstance()
    {
        var lines = new[] { new OcrLineBox([new OcrWordBox("mug", new ScreenRect(0, 0, 10, 10))]) };
        Assert.Same(lines[0], Corrector.Correct(lines, out var changes)[0]);
        Assert.Empty(changes);
    }

    [Fact]
    public void KnownShare_CountsOnlyRealWords()
    {
        var box = new ScreenRect(0, 0, 10, 10);
        var lines = new[]
        {
            new OcrLineBox([new OcrWordBox("garden", box), new OcrWordBox("jacket", box), new OcrWordBox("xqzv", box),
                            new OcrWordBox("ab", box), new OcrWordBox("12.5", box), new OcrWordBox("mug,", box)]),
        };
        var (share, total) = Corrector.KnownShare(lines);
        Assert.Equal(5, total);          // garden, jacket, xqzv, ab, mug (числа без букв не считаются)
        Assert.Equal(0.6, share);
    }

    [Fact]
    public void KnownShare_SingleLettersAndMixedTokens()
    {
        var box = new ScreenRect(0, 0, 10, 10);
        var lines = new[]
        {
            // «I» и «a» — слова; «i», «s» — обрывки; «l.deiqht», «H2O» — буквы со знаками; «3rd» и «5» — не считаются
            new OcrLineBox([new OcrWordBox("I", box), new OcrWordBox("a", box), new OcrWordBox("i", box), new OcrWordBox("s,", box),
                            new OcrWordBox("l.deiqht", box), new OcrWordBox("H2O", box), new OcrWordBox("3rd", box), new OcrWordBox("5", box)]),
        };
        var (share, total) = Corrector.KnownShare(lines);
        Assert.Equal(6, total);
        Assert.Equal(2 / 6.0, share, precision: 6);
    }

    [Fact]
    public void KnownShare_HyphenatedGarbageIsNotAWord()
    {
        var box = new ScreenRect(0, 0, 10, 10);
        var lines = new[]
        {
            new OcrLineBox([new OcrWordBox("ur-lur", box), new OcrWordBox("ur-II-Ir", box), new OcrWordBox("well-known", box), new OcrWordBox("mug", box)]),
        };
        var (share, total) = Corrector.KnownShare(lines);
        Assert.Equal(4, total);
        Assert.Equal(0.5, share); // «well-known» и «mug»
    }

    [Fact]
    public void KnownShare_NoWords_IsOne()
    {
        var (share, total) = Corrector.KnownShare([]);
        Assert.Equal(0, total);
        Assert.Equal(1, share);
    }
}
