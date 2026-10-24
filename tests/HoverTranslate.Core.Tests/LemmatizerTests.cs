using HoverTranslate.Core.Dictionary;

namespace HoverTranslate.Core.Tests;

public sealed class LemmatizerTests
{
    [Theory]
    [InlineData("cats", "cat")]
    [InlineData("boxes", "box")]
    [InlineData("wishes", "wish")]
    [InlineData("cities", "city")]
    [InlineData("leaves", "leaf")]
    [InlineData("knives", "knife")]
    [InlineData("goes", "go")]
    [InlineData("walked", "walk")]
    [InlineData("liked", "like")]
    [InlineData("stopped", "stop")]
    [InlineData("tried", "try")]
    [InlineData("walking", "walk")]
    [InlineData("making", "make")]
    [InlineData("running", "run")]
    [InlineData("lying", "lie")]
    [InlineData("happier", "happy")]
    [InlineData("happiest", "happy")]
    [InlineData("bigger", "big")]
    [InlineData("biggest", "big")]
    [InlineData("later", "late")]
    [InlineData("quickly", "quick")]
    [InlineData("happily", "happy")]
    [InlineData("world's", "world")]
    [InlineData("children's", "children")]
    [InlineData("they're", "they")]
    [InlineData("e-mail", "email")]
    public void Candidates_ContainLemma(string word, string lemma) => Assert.Contains(lemma, Lemmatizer.Candidates(word));

    [Fact]
    public void Candidates_DoubledConsonantFirst() => Assert.Equal("stop", Lemmatizer.Candidates("stopped")[0]);

    [Fact]
    public void Candidates_PluralFirst() => Assert.Equal("cat", Lemmatizer.Candidates("cats")[0]);

    [Theory]
    [InlineData("glass")]
    [InlineData("bus")]
    [InlineData("a")]
    public void Candidates_NoBogusStripping(string word) => Assert.DoesNotContain(word[..^1], Lemmatizer.Candidates(word));

    [Fact]
    public void Candidates_NeverContainWordItself() => Assert.DoesNotContain("run", Lemmatizer.Candidates("run"));

    [Fact]
    public void Candidates_LowercaseInput() => Assert.Contains("cat", Lemmatizer.Candidates("CATS"));
}
