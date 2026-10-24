using HoverTranslate.Core.Models;
using HoverTranslate.Core.TextSource;
using static HoverTranslate.Core.Tests.OcrFixtures;

namespace HoverTranslate.Core.Tests;

public class OcrLayoutTests
{
    private const double Tolerance = 10;

    [Fact]
    public void FindWord_CursorInsideWord_ReturnsThatWord()
    {
        var lines = Paragraph(100, 100, "The quick brown fox jumps over the lazy dog.");
        var (x, y) = CenterOf(lines, "brown");

        var pos = OcrLayout.FindWord(lines, x, y, Tolerance);

        Assert.NotNull(pos);
        Assert.Equal("brown", WordAt(lines, pos.Value));
    }

    [Fact]
    public void FindWord_CursorInGapBetweenWords_ReturnsNearest()
    {
        // «quick» занимает x 140..190, пробел 190..200, «brown» 200..250.
        var lines = Paragraph(100, 100, "The quick brown fox");

        var pos = OcrLayout.FindWord(lines, 192, 110, Tolerance);

        Assert.Equal("quick", WordAt(lines, pos!.Value));
    }

    [Fact]
    public void FindWord_CursorSlightlyBelowLine_WithinTolerance_Found()
    {
        var lines = Paragraph(100, 100, "Save your progress");
        var (x, _) = CenterOf(lines, "progress");

        var pos = OcrLayout.FindWord(lines, x, 100 + LineHeight + 6, Tolerance);

        Assert.Equal("progress", WordAt(lines, pos!.Value));
    }

    [Fact]
    public void FindWord_CursorFarFromText_ReturnsNull()
    {
        var lines = Paragraph(100, 100, "Save your progress");

        Assert.Null(OcrLayout.FindWord(lines, 100, 160, Tolerance));
        Assert.Null(OcrLayout.FindWord(lines, 600, 110, Tolerance));
    }

    [Fact]
    public void FindWord_SkipsPunctuationOnlyBoxes()
    {
        // «—» по центру x=165; «Ready» кончается на 150, «set» начинается на 180 — оба в 15 px.
        var lines = Paragraph(100, 100, "Ready — set go");
        var (x, y) = CenterOf(lines, "—");

        Assert.Null(OcrLayout.FindWord(lines, x, y, Tolerance)); // тире не выбирается, а слова дальше допуска
        var pos = OcrLayout.FindWord(lines, x, y, tolerance: 20);
        Assert.Equal("Ready", WordAt(lines, pos!.Value));
    }

    [Fact]
    public void FindWord_EmptyResult_ReturnsNull() =>
        Assert.Null(OcrLayout.FindWord([], 10, 10, Tolerance));

    [Fact]
    public void Sentence_SpansLines_CutAtBoundaries()
    {
        var lines = Paragraph(100, 100,
            "It was late. The weather was terrible, so",
            "we stayed at home and played board games.",
            "Nobody complained.");
        var pos = OcrLayout.FindWord(lines, CenterOf(lines, "stayed").X, CenterOf(lines, "stayed").Y, Tolerance)!.Value;

        string sentence = OcrLayout.FindSentence(lines, pos);

        Assert.Equal("The weather was terrible, so we stayed at home and played board games.", sentence);
    }

    [Fact]
    public void Sentence_QuestionAndExclamation_AreBoundaries()
    {
        var lines = Paragraph(100, 100, "Are you ready? Press the button! Then wait.");
        var (x, y) = CenterOf(lines, "button!");

        string sentence = OcrLayout.FindSentence(lines, OcrLayout.FindWord(lines, x, y, Tolerance)!.Value);

        Assert.Equal("Press the button!", sentence);
    }

    [Fact]
    public void Sentence_Ellipsis_IsBoundary()
    {
        var lines = Paragraph(100, 100, "Loading… Please wait for the server.");
        var (x, y) = CenterOf(lines, "server.");

        string sentence = OcrLayout.FindSentence(lines, OcrLayout.FindWord(lines, x, y, Tolerance)!.Value);

        Assert.Equal("Please wait for the server.", sentence);
    }

    [Fact]
    public void Context_IgnoresAdjacentColumn()
    {
        // Две колонки на одной высоте: правая не должна попасть в контекст левой.
        var left = Paragraph(100, 100, "Left column text goes", "on and on here.");
        var right = Paragraph(600, 100, "Right column words", "should stay apart.");
        var lines = left.Concat(right).ToList();
        var (x, y) = CenterOf(lines, "goes");

        var context = OcrLayout.BuildContext(lines, OcrLayout.FindWord(lines, x, y, Tolerance)!.Value);

        Assert.Equal("Left column text goes on and on here.", context.Text);
    }

    [Fact]
    public void Context_IgnoresHeadingWithDifferentFontSize()
    {
        var heading = Line(100, 60, "CHAPTER ONE", charWidth: 20, height: 36);
        var body = Paragraph(100, 104, "She looked forward to meeting", "her new colleagues.");
        var lines = new[] { heading }.Concat(body).ToList();
        var (x, y) = CenterOf(lines, "meeting");

        var context = OcrLayout.BuildContext(lines, OcrLayout.FindWord(lines, x, y, Tolerance)!.Value);

        Assert.Equal("She looked forward to meeting her new colleagues.", context.Text);
    }

    [Fact]
    public void Context_StopsAtLargeVerticalGap()
    {
        var first = Paragraph(100, 100, "First paragraph ends here");
        var second = Paragraph(100, 200, "Second paragraph starts here");
        var lines = first.Concat(second).ToList();
        var (x, y) = CenterOf(lines, "Second");

        var context = OcrLayout.BuildContext(lines, OcrLayout.FindWord(lines, x, y, Tolerance)!.Value);

        Assert.Equal("Second paragraph starts here", context.Text);
    }

    [Fact]
    public void Context_LimitedToThreeNeighborLinesEachSide()
    {
        var lines = Paragraph(100, 100, "l1", "l2", "l3", "l4", "target", "l6", "l7", "l8", "l9");
        var (x, y) = CenterOf(lines, "target");

        var context = OcrLayout.BuildContext(lines, OcrLayout.FindWord(lines, x, y, Tolerance)!.Value);

        Assert.Equal("l2 l3 l4 target l6 l7 l8", context.Text);
    }

    [Fact]
    public void Context_JoinsHyphenatedLineBreak()
    {
        var lines = Paragraph(100, 100, "This is an inter-", "national agreement.");
        var (x, y) = CenterOf(lines, "agreement.");

        var context = OcrLayout.BuildContext(lines, OcrLayout.FindWord(lines, x, y, Tolerance)!.Value);

        Assert.Equal("This is an international agreement.", context.Text);
        Assert.Equal("agreement.", context.Text.Substring(context.WordOffset, context.WordLength));
    }

    [Fact]
    public void Context_WordOffsetPointsToHitWord_WhenWordRepeats()
    {
        var lines = Paragraph(100, 100, "the cat saw the", "dog and the bird.");
        // Третье «the» во второй строке.
        var second = lines[1].Words.First(w => w.Text == "the");

        var pos = OcrLayout.FindWord(lines, second.Bounds.CenterX, second.Bounds.CenterY, Tolerance)!.Value;
        var context = OcrLayout.BuildContext(lines, pos);

        Assert.Equal("the cat saw the dog and the bird.", context.Text);
        Assert.Equal(24, context.WordOffset);
    }

    [Fact]
    public void Context_UnorderedLines_StillAssembledTopToBottom()
    {
        var lines = Paragraph(100, 100, "one two", "three four", "five six");
        lines.Reverse(); // OCR может вернуть строки не по порядку
        var (x, y) = CenterOf(lines, "four");

        var context = OcrLayout.BuildContext(lines, OcrLayout.FindWord(lines, x, y, Tolerance)!.Value);

        Assert.Equal("one two three four five six", context.Text);
    }

    // ---- WiderRegion: снимок 800 px вокруг курсора на мониторе шириной 1920

    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);
    private static readonly ScreenRect Region = new(560, 800, 800, 240);

    private static ScreenRect? Wider(List<OcrLineBox> lines, string word)
    {
        var (x, y) = CenterOf(lines, word);
        return OcrLayout.WiderRegion(lines, new ScreenPoint((int)x, (int)y), Region, Monitor, Tolerance);
    }

    [Fact]
    public void WiderRegion_LineEndsAtRightEdge_ReturnsFullMonitorWidth()
    {
        var lines = Paragraph(900, 900, "she's too big for mine... What should we do? and the rest of the sentence continues far", "next line");
        // строка начинается в кадре и заканчивается за его правым краем (1360)
        var wide = Wider(lines, "mine...");

        Assert.Equal(new ScreenRect(0, 800, 1920, 240), wide);
    }

    [Fact]
    public void WiderRegion_LineEndsAtLeftEdge_ReturnsFullMonitorWidth()
    {
        var lines = Paragraph(560, 900, "ng equal parts excited and nervou");

        Assert.NotNull(Wider(lines, "excited"));
    }

    [Fact]
    public void WiderRegion_TextInsideRegion_ReturnsNull()
    {
        var lines = Paragraph(700, 900, "A short line in the middle.");

        Assert.Null(Wider(lines, "short"));
    }

    [Fact]
    public void WiderRegion_RegionAlreadyFullWidth_ReturnsNull()
    {
        var lines = Paragraph(0, 900, "x".PadRight(190, 'x'));
        var (x, y) = CenterOf(lines, lines[0].Words[0].Text);

        Assert.Null(OcrLayout.WiderRegion(lines, new ScreenPoint((int)x, (int)y), Monitor, Monitor, Tolerance));
    }

    [Fact]
    public void WiderRegion_NoWordUnderPoint_ReturnsNull()
    {
        var lines = Paragraph(560, 900, "text at the edge of the shot");

        Assert.Null(OcrLayout.WiderRegion(lines, new ScreenPoint(1300, 1000), Region, Monitor, Tolerance));
    }

    // ---- SplitGluedWords

    private static string[] Split(params string[] texts) =>
        OcrLayout.SplitGluedWords(texts.Select((t, i) => new OcrWordBox(t, new ScreenRect(i * 200, 0, t.Length * 10, 20))).ToArray())
            .Select(w => w.Text).ToArray();

    [Theory]
    [InlineData("Fenwick,this", "Fenwick, this")]
    [InlineData("wait!Go", "wait! Go")]
    [InlineData("it.l", "it. I")]
    [InlineData("home.Then", "home. Then")]
    [InlineData("a,b,c", "a, b, c")]
    public void SplitGluedWords_SplitsGluedTokens(string raw, string expected) =>
        Assert.Equal(expected, string.Join(' ', Split(raw)));

    [Theory]
    [InlineData("e.g")]
    [InlineData("U.S.A")]
    [InlineData("3.14")]
    [InlineData("don't")]
    [InlineData("broad-shouldered")]
    [InlineData("www.site.com")]
    [InlineData("Hello,")]
    [InlineData("1,000")]
    [InlineData("it.")]
    public void SplitGluedWords_LeavesNormalTokens(string raw) =>
        Assert.Equal([raw], Split(raw));

    [Fact]
    public void SplitGluedWords_BoundsAreSplitByCharacters()
    {
        var parts = OcrLayout.SplitGluedWords([new OcrWordBox("ab,cdef", new ScreenRect(100, 10, 70, 20))]);

        Assert.Equal(2, parts.Count);
        Assert.Equal(new ScreenRect(100, 10, 30, 20), parts[0].Bounds);   // «ab,» — 3 из 7 символов
        Assert.Equal(new ScreenRect(130, 10, 40, 20), parts[1].Bounds);   // «cdef»
    }

    [Fact]
    public void SplitGluedWords_NoGlued_ReturnsSameInstance()
    {
        IReadOnlyList<OcrWordBox> words = [new OcrWordBox("fine", new ScreenRect(0, 0, 40, 20))];

        Assert.Same(words, OcrLayout.SplitGluedWords(words));
    }

    // ---- имена говорящих и значки

    [Fact]
    public void Context_SpeakerLabelAbove_IsNotPartOfSentence()
    {
        var lines = new List<OcrLineBox>
        {
            Line(100, 100, "Dana's mom:"),
            Line(100, 128, "'No no, it's fine... You did sleep quite a"),
            Line(100, 156, "while, after all.'"),
        };
        var (x, y) = CenterOf(lines, "quite");
        var pos = OcrLayout.FindWord(lines, x, y, Tolerance)!.Value;

        Assert.Equal("'No no, it's fine... You did sleep quite a while, after all.'", OcrLayout.FindSentence(lines, pos));
    }

    [Fact]
    public void Context_SpeakerLabelBelow_IsNotPartOfSentence()
    {
        var lines = new List<OcrLineBox>
        {
            Line(100, 100, "It hasn't been opened in a while, but it should work!'"),
            Line(100, 128, "Marlowe:"),
        };
        var (x, y) = CenterOf(lines, "opened");
        var pos = OcrLayout.FindWord(lines, x, y, Tolerance)!.Value;

        Assert.Equal("It hasn't been opened in a while, but it should work!'", OcrLayout.FindSentence(lines, pos));
    }

    [Theory]
    [InlineData("Dana's mom:", true)]
    [InlineData("Marlowe:", true)]
    [InlineData("Note this is a long line of text:", false)]
    [InlineData("Marlowe", false)]
    [InlineData(":", false)]
    public void IsSpeakerLabel_Detects(string text, bool expected) =>
        Assert.Equal(expected, OcrLayout.IsSpeakerLabel(Line(0, 0, text)));

    [Theory]
    [InlineData("when Dana suddenly comes back. o", "when Dana suddenly comes back.")]
    [InlineData("comes back. 0", "comes back.")]
    [InlineData("it was nice! •", "it was nice!")]
    [InlineData("back. I", "back. I")]
    [InlineData("see you. So", "see you. So")]
    [InlineData("and then o", "and then o")]
    public void Context_TrailingIcon_IsDropped(string text, string expected)
    {
        var lines = new List<OcrLineBox> { Line(100, 100, text) };
        var pos = OcrLayout.FindWord(lines, lines[0].Words[0].Bounds.CenterX, lines[0].Words[0].Bounds.CenterY, Tolerance)!.Value;

        Assert.Equal(expected, OcrLayout.BuildContext(lines, pos).Text);
    }

    [Fact]
    public void Context_TrailingIcon_NeverDropsWordUnderCursor()
    {
        var lines = new List<OcrLineBox> { Line(100, 100, "comes back. o") };
        var (x, y) = CenterOf(lines, "o");
        var pos = OcrLayout.FindWord(lines, x, y, Tolerance)!.Value;

        Assert.Equal("comes back. o", OcrLayout.BuildContext(lines, pos).Text);
    }
}
