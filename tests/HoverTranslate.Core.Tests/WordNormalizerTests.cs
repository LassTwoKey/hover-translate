using HoverTranslate.Core.TextSource;

namespace HoverTranslate.Core.Tests;

public class WordNormalizerTests
{
    [Theory]
    [InlineData("hello,", "hello")]
    [InlineData("“Hello!”", "Hello")]
    [InlineData("(well-known)", "well-known")]
    [InlineData("don't", "don't")]
    [InlineData("Don’t.", "Don't")]
    [InlineData("students'", "students")]
    [InlineData("'quoted'", "quoted")]
    [InlineData("--dash--", "dash")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("co\u2010operate", "co-operate")]
    [InlineData("hyphen\u00ADated", "hyphenated")]
    [InlineData("...", "")]
    [InlineData("", "")]
    public void Normalize(string raw, string expected) => Assert.Equal(expected, WordNormalizer.Normalize(raw));

    [Theory]
    [InlineData("word", true)]
    [InlineData("I", true)]
    [InlineData("don't", true)]
    [InlineData("state-of-the-art", true)]
    [InlineData("MP3", true)]
    [InlineData("café", true)]
    [InlineData("123", false)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("слово", false)]
    [InlineData("a--b", false)]
    [InlineData("C:\\path", false)]
    public void IsPlausibleWord(string word, bool expected) => Assert.Equal(expected, WordNormalizer.IsPlausibleWord(word));

    [Fact]
    public void IsPlausibleWord_RejectsTooLong() =>
        Assert.False(WordNormalizer.IsPlausibleWord(new string('a', 46)));
}
