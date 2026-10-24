using HoverTranslate.Core.Dictionary;
using HoverTranslate.Core.Models;

namespace HoverTranslate.Core.Tests;

/// <summary>Токены предложения, подсказка части речи и ранжирование значений.</summary>
public sealed class ContextTests
{
    [Fact]
    public void Tokenize_WordsWithOffsetsAndBreaks()
    {
        const string s = "“Don't give up,” she said — well-known advice.";
        var tokens = SentenceTokens.Tokenize(s);

        Assert.Equal(["Don't", "give", "up", "she", "said", "well-known", "advice"], tokens.Select(t => t.Text));
        Assert.Equal(s.IndexOf("give", StringComparison.Ordinal), tokens[1].Start);
        Assert.False(tokens[1].BreakBefore);
        Assert.True(tokens[3].BreakBefore);  // «,”» перед she
        Assert.True(tokens[5].BreakBefore);  // тире
        Assert.False(tokens[6].BreakBefore);
    }

    [Fact]
    public void Tokenize_CurlyApostropheNormalized() => Assert.Equal("don't", SentenceTokens.Tokenize("don’t")[0].Lower);

    [Fact]
    public void IndexOf_UsesOffsetForRepeatedWord()
    {
        const string s = "Run, run as fast as you can.";
        var tokens = SentenceTokens.Tokenize(s);
        Assert.Equal(1, SentenceTokens.IndexOf(tokens, "run", s.IndexOf("run", StringComparison.Ordinal)));
        Assert.Equal(0, SentenceTokens.IndexOf(tokens, "run", -1));
        Assert.Equal(-1, SentenceTokens.IndexOf(tokens, "walk", 0));
    }

    [Fact]
    public void IndexOf_ToleratesLeadingQuoteInOffset()
    {
        const string s = "He said “hello” twice.";
        var tokens = SentenceTokens.Tokenize(s);
        Assert.Equal(2, SentenceTokens.IndexOf(tokens, "hello", s.IndexOf('“')));
    }

    [Theory]
    [InlineData("She was running late.", "running", PosHint.Verb)]
    [InlineData("He left the room.", "left", PosHint.Verb)]
    [InlineData("They've finished.", "finished", PosHint.Verb)]
    [InlineData("The building is tall.", "building", PosHint.Nominal)]
    [InlineData("Turn left here.", "left", PosHint.None)]
    [InlineData("Running is fun.", "Running", PosHint.None)]
    [InlineData("John's running shoes.", "running", PosHint.None)]
    [InlineData("It was, running away.", "running", PosHint.None)]
    public void Guess_PreviousWord(string sentence, string word, PosHint expected)
    {
        var tokens = SentenceTokens.Tokenize(sentence);
        Assert.Equal(expected, ContextHints.Guess(tokens, SentenceTokens.IndexOf(tokens, word, -1)));
    }

    [Theory]
    [InlineData("running", "run")]
    [InlineData("rivers", "river")]
    [InlineData("stopped", "stop")]
    [InlineData("making", "mak")]
    [InlineData("make", "mak")]
    [InlineData("glass", "glass")]
    public void Stem_SameForRelatedForms(string word, string stem) => Assert.Equal(stem, SenseRanker.Stem(word));

    [Fact]
    public void Rank_OverlapWinsOverOrder()
    {
        var senses = new[]
        {
            new Sense("noun", "A financial institution.", ["банк"], []),
            new Sense("noun", "The sloping edge of a river.", ["берег"], []),
        };
        const string s = "Fish swim near the river bank.";
        var tokens = SentenceTokens.Tokenize(s);
        var ranked = SenseRanker.Rank(senses, tokens, SentenceTokens.IndexOf(tokens, "bank", -1), 1, PosHint.None);
        Assert.Equal("берег", ranked[0].TranslationsRu[0]);
    }

    [Fact]
    public void Rank_NoContext_KeepsOriginalOrder()
    {
        var senses = new[]
        {
            new Sense("noun", "First.", ["один"], []),
            new Sense("noun", "Second.", ["два"], []),
            new Sense("noun", "Third.", ["три"], []),
        };
        Assert.Equal(senses, SenseRanker.Rank(senses, [], -1, 0, PosHint.None));
    }

    [Fact]
    public void Rank_PosHintPrefersMatchingSense()
    {
        var senses = new[]
        {
            new Sense("noun", "A container.", ["коробка"], []),
            new Sense("verb", "To fight with fists.", ["боксировать"], []),
        };
        var ranked = SenseRanker.Rank(senses, SentenceTokens.Tokenize("They box"), 1, 1, PosHint.Verb);
        Assert.Equal("verb", ranked[0].PartOfSpeech);
    }

    [Fact]
    public void Rank_TranslatedSenseBeforeUntranslated()
    {
        var senses = new[]
        {
            new Sense("noun", "Rare sense.", [], []),
            new Sense("noun", "Common sense.", ["значение"], []),
        };
        Assert.Equal("значение", SenseRanker.Rank(senses, [], -1, 0, PosHint.None)[0].TranslationsRu[0]);
    }
}
