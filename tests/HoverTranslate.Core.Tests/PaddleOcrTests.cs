using HoverTranslate.Core.Capture;
using HoverTranslate.Core.TextSource;

namespace HoverTranslate.Core.Tests;

/// <summary>Чистая логика запасного OCR (PaddleOCR): размеры, рамки из карты вероятностей, CTC, слова, тензор. Без моделей.</summary>
public sealed class PaddleOcrTests
{
    [Theory]
    [InlineData(1000, 300, 2464, 736)]   // короткая сторона растягивается до 736
    [InlineData(1920, 300, 2560, 384)]   // длинная ограничена 2560 (ratio 4/3): 400 -> 384, округление к чётному
    [InlineData(1000, 800, 992, 800)]    // обе стороны ≥ 736: масштаб 1, размеры кратны 32
    [InlineData(50, 30, 1216, 736)]      // крошечный снимок растягивается
    public void DetSize_MultipleOf32(int w, int h, int expectedW, int expectedH)
    {
        var (width, height) = PaddleOcr.DetSize(w, h);
        Assert.Equal((expectedW, expectedH), (width, height));
        Assert.Equal(0, width % 32);
        Assert.Equal(0, height % 32);
    }

    private static float[] Map(int w, int h, Action<float[]> fill)
    {
        var prob = new float[w * h];
        fill(prob);
        return prob;
    }

    private static void Rect(float[] prob, int w, int x0, int y0, int x1, int y1, float value)
    {
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                prob[y * w + x] = value;
    }

    [Fact]
    public void BoxesFromMap_FindsTextBlobAndExpandsIt()
    {
        const int w = 200, h = 100;
        var prob = Map(w, h, p => Rect(p, w, 40, 40, 159, 59, 0.9f));
        var boxes = PaddleOcr.BoxesFromMap(prob, w, h, w, h);

        var box = Assert.Single(boxes);
        // 119×19 по центрам пикселей, расширение distance = 119·19·1,6 / (2·138) ≈ 13,1 с каждой стороны
        Assert.InRange(box.Left, 24, 29);
        Assert.InRange(box.Right, 170, 175);
        Assert.InRange(box.Top, 24, 29);
        Assert.InRange(box.Bottom, 70, 75);
    }

    [Fact]
    public void BoxesFromMap_ScalesToDestination()
    {
        const int w = 100, h = 50;
        var prob = Map(w, h, p => Rect(p, w, 20, 20, 79, 29, 0.9f));
        var small = Assert.Single(PaddleOcr.BoxesFromMap(prob, w, h, w, h));
        var big = Assert.Single(PaddleOcr.BoxesFromMap(prob, w, h, w * 2, h * 2));
        Assert.InRange(big.Left, small.Left * 2 - 2, small.Left * 2 + 2);
        Assert.InRange(big.Bottom, small.Bottom * 2 - 2, small.Bottom * 2 + 2);
    }

    [Fact]
    public void BoxesFromMap_SeparatesTwoLines_AndDropsSpecklesAndWeakBlobs()
    {
        const int w = 300, h = 200;
        var prob = Map(w, h, p =>
        {
            Rect(p, w, 20, 20, 250, 35, 0.9f);   // строка 1
            Rect(p, w, 20, 120, 250, 135, 0.9f); // строка 2
            Rect(p, w, 100, 80, 101, 81, 0.9f);  // точка 2×2: меньше минимального размера
            Rect(p, w, 20, 170, 250, 185, 0.35f); // выше порога 0,3, но средняя вероятность ниже 0,5
        });
        var boxes = PaddleOcr.BoxesFromMap(prob, w, h, w, h);
        Assert.Equal(2, boxes.Count);
        Assert.True(boxes[0].Bottom < boxes[1].Top);
    }

    [Fact]
    public void BoxesFromMap_NothingAboveThreshold_IsEmpty() =>
        Assert.Empty(PaddleOcr.BoxesFromMap(new float[100 * 50], 100, 50, 100, 50));

    // Алфавит заглушки: 0 — «пусто», 1..3 — буквы, 4 — иероглиф (запрещён), 5 — пробел.
    private static readonly string[] Chars = ["blank", "a", "b", "c", "日", " "];
    private static readonly bool[] Allowed = PaddleOcr.BuildAllowed(Chars);

    private static float[] Logits(params int[] best)
    {
        var data = new float[best.Length * Chars.Length];
        for (int t = 0; t < best.Length; t++) data[t * Chars.Length + best[t]] = 0.9f;
        return data;
    }

    [Fact]
    public void BuildAllowed_AsciiAndSpaceYes_HieroglyphNo() => Assert.Equal([true, true, true, true, false, true], Allowed);

    [Fact]
    public void Decode_CollapsesRepeatsAndBlanks()
    {
        // a a _ a b b _ _ c  ->  «aabc»: повтор через «пусто» — отдельный символ
        var chars = PaddleOcr.Decode(Logits(1, 1, 0, 1, 2, 2, 0, 0, 3), 9, Chars.Length, Allowed, Chars, out double score);
        Assert.Equal("aabc", new string(chars.Select(c => c.Ch).ToArray()));
        Assert.Equal([0, 3, 4, 8], chars.Select(c => c.Step).ToArray());
        Assert.Equal(0.9, score, precision: 5);
    }

    [Fact]
    public void Decode_ForbiddenClassIsSkippedInFavorOfNextBest()
    {
        var data = new float[2 * Chars.Length];
        data[0 * Chars.Length + 4] = 0.8f; // иероглиф — запрещён
        data[0 * Chars.Length + 2] = 0.1f; // «b» — лучший из допустимых
        data[1 * Chars.Length + 0] = 0.9f;
        var chars = PaddleOcr.Decode(data, 2, Chars.Length, Allowed, Chars, out _);
        Assert.Equal("b", new string(chars.Select(c => c.Ch).ToArray()));
    }

    [Fact]
    public void Decode_AllBlank_IsEmptyWithZeroScore()
    {
        var chars = PaddleOcr.Decode(Logits(0, 0, 0), 3, Chars.Length, Allowed, Chars, out double score);
        Assert.Empty(chars);
        Assert.Equal(0, score);
    }

    [Fact]
    public void SplitWords_SplitsOnSpaceWithBoundsFromSteps()
    {
        // «ab c»: символы на шагах 4, 8; пробел на 12; «c» на 16; шаг = 10 px
        (char, int)[] chars = [('a', 4), ('b', 8), (' ', 12), ('c', 16)];
        var words = PaddleOcr.SplitWords(chars, stepWidth: 10);

        Assert.Equal(["ab", "c"], words.Select(w => w.Text).ToArray());
        // «ab»: полшага между символами = 2 шага: от (4+0,5−2)·10 = 25 до (8+0,5+2)·10 = 105
        Assert.Equal(25, words[0].Left, precision: 6);
        // «c»: односимвольное слово — два шага в каждую сторону: от (16+0,5−2)·10 = 145
        Assert.Equal(145, words[1].Left, precision: 6);
        Assert.True(words[0].Right <= words[1].Left);
    }

    [Fact]
    public void SplitWords_OverlappingNeighborsMeetInTheMiddle()
    {
        (char, int)[] chars = [('a', 4), ('b', 8), (' ', 9), ('c', 10), ('d', 14)];
        var words = PaddleOcr.SplitWords(chars, stepWidth: 10);
        Assert.Equal(2, words.Count);
        Assert.Equal(words[0].Right, words[1].Left, precision: 6);
    }

    [Fact]
    public void SplitWords_NoLetters_IsEmpty() => Assert.Empty(PaddleOcr.SplitWords([], 10));

    [Fact]
    public void ToTensor_NormalizesBgrAndPadsWithZeros()
    {
        // Один пиксель B=255, G=0, R=51 (BGRA), растянутый до 2×2 и добавленный до ширины 4
        var image = new BgraImage([255, 0, 51, 255], 1, 1);
        var tensor = PaddleOcr.ToTensor(image, new PaddleOcr.TextBox(0, 0, 1, 1), 2, 2, 4);

        Assert.Equal(3 * 2 * 4, tensor.Length);
        Assert.Equal(1f, tensor[0 * 8 + 0], precision: 5);              // B: 255 -> 1
        Assert.Equal(-1f, tensor[1 * 8 + 0], precision: 5);             // G: 0 -> -1
        Assert.Equal(51 / 127.5f - 1f, tensor[2 * 8 + 1 * 4 + 1], precision: 5); // R, нижняя строка
        Assert.Equal(0f, tensor[0 * 8 + 3]);                            // поле справа
        Assert.Equal(0f, tensor[2 * 8 + 4 + 3]);
    }

    [Fact]
    public void ToTensor_CropsTheBox()
    {
        // 2×1: левый пиксель белый, правый чёрный; вырезка правого пикселя
        var image = new BgraImage([255, 255, 255, 255, 0, 0, 0, 255], 2, 1);
        var tensor = PaddleOcr.ToTensor(image, new PaddleOcr.TextBox(1, 0, 2, 1), 1, 1, 1);
        Assert.Equal(-1f, tensor[0], precision: 5);
    }
}
