using HoverTranslate.Core.Translation;

namespace HoverTranslate.Core.Tests;

public class SentencePreprocessorTests
{
    [Theory]
    [InlineData("Nooooo, don't go!", "No, don't go!")]
    [InlineData("Heeeey, wait up!", "Hey, wait up!")]
    [InlineData("NOOOOO!", "NO!")]
    [InlineData("Hmmmmm... I see.", "Hmm... I see.")]
    [InlineData("Shhhh, be quiet.", "Shh, be quiet.")]
    [InlineData("Zzzzz", "Zz")]
    [InlineData("Guuuhh...", "Guhh...")]
    public void Clean_CollapsesElongatedWords(string input, string expected) =>
        Assert.Equal(expected, SentencePreprocessor.Clean(input));

    [Theory]
    [InlineData("I'm gonna go now.")]
    [InlineData("Wooow, look at that")]
    [InlineData("Hmm, Mm")]
    [InlineData("aa bb cc")]
    [InlineData("Ahh, Mrs Fenwick, this is, um...")]
    [InlineData("1000000 and 111")]
    [InlineData("")]
    [InlineData("Hi")]
    public void Clean_LeavesNormalTextAlone(string input)
    {
        // «Wooow» — три «o», сжимается; остальное не меняется.
        string expected = input == "Wooow, look at that" ? "Wow, look at that" : input;
        Assert.Equal(expected, SentencePreprocessor.Clean(input));
    }
}
