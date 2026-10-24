using HoverTranslate.Core.TextSource;

namespace HoverTranslate.Core.Tests;

public class SentenceFinderTests
{
    private static string FindAround(string text, string word) => SentenceFinder.Find(text, text.IndexOf(word, StringComparison.Ordinal));

    [Theory]
    [InlineData("First one. Second one here. Third.", "Second", "Second one here.")]
    [InlineData("First one. Second one here. Third.", "First", "First one.")]
    [InlineData("First one. Second one here. Third.", "Third", "Third.")]
    [InlineData("Is it? Yes! No…", "Yes", "Yes!")]
    [InlineData("No terminator at all", "terminator", "No terminator at all")]
    public void Finds_SentenceContainingWord(string text, string word, string expected) =>
        Assert.Equal(expected, FindAround(text, word));

    [Theory]
    [InlineData("'Marlowe forgot her umbrella, and it's too big for mine... What should we do?'", "umbrella")]
    [InlineData("'So... Heavy...'", "Heavy")]
    [InlineData("I guess maybe Dana is a bit broad-shouldered, but even so... I could be wrong.", "broad-shouldered")]
    public void DotEllipsis_InsideReply_IsNotBoundary(string text, string word) =>
        Assert.Equal(text, FindAround(text, word));

    [Fact]
    public void TwoDots_AreAnOrdinaryPeriod() =>
        Assert.Equal("It was all I had on short notice..", FindAround("It was all I had on short notice.. Nobody has touched it since spring.", "notice"));

    [Fact]
    public void DotEllipsis_BeforeQuote_StillEndsSentence() =>
        Assert.Equal("'Hmm...'", FindAround("'Hmm...' 'Fine. Go.'", "Hmm"));

    [Fact]
    public void QuestionWithDots_IsBoundary() =>
        Assert.Equal("Really?...", FindAround("Really?... Yes. Go.", "Really"));

    [Fact]
    public void Abbreviations_AreNotBoundaries() =>
        Assert.Equal("Mr. Smith met Dr. Jones at 5 p.m. today.",
            FindAround("Hello. Mr. Smith met Dr. Jones at 5 p.m. today. Bye.", "Jones"));

    [Fact]
    public void Initials_AreNotBoundaries() =>
        Assert.Equal("The book by J. K. Rowling sold well.", FindAround("The book by J. K. Rowling sold well.", "sold"));

    [Fact]
    public void Decimals_AreNotBoundaries() =>
        Assert.Equal("Pi is 3.14 roughly.", FindAround("Pi is 3.14 roughly.", "roughly"));

    [Fact]
    public void QuotedQuestionFollowedByLowercase_StaysInSentence() =>
        Assert.Equal("\"What?\" she asked quietly.", FindAround("\"What?\" she asked quietly.", "asked"));

    [Fact]
    public void ClosingQuoteAfterTerminator_BelongsToSentence() =>
        Assert.Equal("He said “go home.”", FindAround("He said “go home.” Then he left.", "home"));

    [Fact]
    public void Newline_IsBoundary() =>
        Assert.Equal("Second line here", FindAround("First line\nSecond line here\nThird", "line here"));

    [Fact]
    public void Whitespace_IsCollapsed() =>
        Assert.Equal("Too many spaces here.", FindAround("Too   many \t spaces  here.", "spaces"));

    [Fact]
    public void WordWithAttachedPeriod_EndsSentence() =>
        Assert.Equal("We stayed home.", FindAround("We stayed home. Then slept.", "home."));

    [Fact]
    public void VeryLongSentence_IsTrimmedAroundWord()
    {
        string text = string.Join(" ", Enumerable.Range(0, 300).Select(i => "w" + i)) + " target " +
                      string.Join(" ", Enumerable.Range(0, 300).Select(i => "z" + i));

        string sentence = FindAround(text, "target");

        Assert.Contains("target", sentence);
        Assert.True(sentence.Length <= SentenceFinder.MaxSentenceLength + 2);
        Assert.StartsWith("…", sentence);
        Assert.EndsWith("…", sentence);
    }

    [Fact]
    public void EmptyText_ReturnsEmpty() => Assert.Equal("", SentenceFinder.Find("", 0));

    [Theory]
    [InlineData("First one.  She   gave up\nquickly. Next.", "gave")]
    [InlineData("  Leading spaces before the word here.", "word")]
    public void WordOffset_PointsAtWordInResult(string text, string word)
    {
        string sentence = SentenceFinder.Find(text, text.IndexOf(word, StringComparison.Ordinal), out int offset);
        Assert.Equal(word, sentence.Substring(offset, word.Length));
    }

    [Fact]
    public void WordOffset_InTruncatedSentence()
    {
        string text = string.Join(' ', Enumerable.Repeat("filler", 120)) + " target " + string.Join(' ', Enumerable.Repeat("filler", 120));
        string sentence = SentenceFinder.Find(text, text.IndexOf("target", StringComparison.Ordinal), out int offset);
        Assert.StartsWith("…", sentence, StringComparison.Ordinal);
        Assert.Equal("target", sentence.Substring(offset, 6));
    }
}
