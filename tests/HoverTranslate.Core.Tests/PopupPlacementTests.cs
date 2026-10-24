using HoverTranslate.Core.Models;
using HoverTranslate.Core.Native;
using HoverTranslate.Core.Popup;

namespace HoverTranslate.Core.Tests;

public class PopupPlacementTests
{
    private static readonly ScreenRect Work1080 = new(0, 0, 1920, 1040); // рабочая область над панелью задач

    private static void AssertInside(ScreenRect inner, ScreenRect outer)
    {
        Assert.True(inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom,
            $"{inner} выходит за {outer}");
    }

    [Fact]
    public void BelowWord_WhenSpace()
    {
        var word = new ScreenRect(900, 300, 80, 20);
        var m = PopupMetrics.ForScale(1);

        var layout = PopupPlacement.Place(word, 380, 300, Work1080, m);

        Assert.Equal(ArrowSide.Top, layout.Side);
        Assert.Equal(word.Bottom + m.Gap + m.ArrowHeight, layout.Card.Y);
        Assert.Equal(word.CenterX, layout.Card.CenterX);
        Assert.Equal(word.CenterX, layout.Arrow.CenterX);
        Assert.True(layout.Arrow.Bottom > layout.Card.Y, "стрелка должна касаться карточки");
        Assert.True(layout.Arrow.Y >= word.Bottom, "стрелка не должна закрывать слово");
    }

    [Fact]
    public void AboveWord_WhenNoSpaceBelow()
    {
        var word = new ScreenRect(900, 900, 80, 20);

        var layout = PopupPlacement.Place(word, 380, 300, Work1080, PopupMetrics.ForScale(1));

        Assert.Equal(ArrowSide.Bottom, layout.Side);
        Assert.True(layout.Card.Bottom < word.Y);
        Assert.True(layout.Arrow.Y < layout.Card.Bottom && layout.Arrow.Bottom <= word.Y);
        AssertInside(layout.Card, Work1080);
    }

    [Fact]
    public void NearRightEdge_ShiftedLeft_ArrowStillPointsAtWord()
    {
        var word = new ScreenRect(1880, 300, 30, 20);
        var m = PopupMetrics.ForScale(1);

        var layout = PopupPlacement.Place(word, 380, 300, Work1080, m);

        Assert.Equal(Work1080.Right - m.ScreenMargin, layout.Card.Right);
        // Стрелка прижата к правому краю, но не заходит на скруглённый угол.
        Assert.True(layout.Arrow.Right <= layout.Card.Right - m.CornerRadius);
        Assert.True(layout.Arrow.CenterX > layout.Card.CenterX);
    }

    [Fact]
    public void NearLeftEdge_ShiftedRight()
    {
        var layout = PopupPlacement.Place(new ScreenRect(2, 300, 30, 20), 380, 300, Work1080, PopupMetrics.ForScale(1));

        Assert.Equal(8, layout.Card.X);
        AssertInside(layout.Card, Work1080);
    }

    [Fact]
    public void TallCard_NoSpaceAnywhere_ClampedInsideWorkArea()
    {
        var word = new ScreenRect(900, 500, 80, 20);

        var layout = PopupPlacement.Place(word, 380, 700, Work1080, PopupMetrics.ForScale(1));

        AssertInside(layout.Card, Work1080);
    }

    [Fact]
    public void CardTallerThanWorkArea_HeightLimited()
    {
        var layout = PopupPlacement.Place(new ScreenRect(900, 500, 80, 20), 380, 5000, Work1080, PopupMetrics.ForScale(1));

        AssertInside(layout.Card, Work1080);
        Assert.Equal(Work1080.Height - 16, layout.Card.Height);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ScalesWithDpi(double scale)
    {
        var work = new ScreenRect(0, 0, 3840 * scale / 2, 2100 * scale / 2);
        var word = new ScreenRect(800 * scale, 300 * scale, 60 * scale, 18 * scale);
        var m = PopupMetrics.ForScale(scale);

        var layout = PopupPlacement.Place(word, 380 * scale, 300 * scale, work, m);

        Assert.Equal(380 * scale, layout.Card.Width, 6);
        Assert.Equal(20 * scale, layout.Arrow.Width, 6);
        Assert.Equal(word.Bottom + m.Gap + m.ArrowHeight, layout.Card.Y, 6);
        AssertInside(layout.Card, work);
    }

    [Fact]
    public void SecondaryMonitorWithNegativeCoordinates()
    {
        var work = new ScreenRect(-2560, -300, 2560, 1400);
        var word = new ScreenRect(-2550, 1000, 60, 20); // у левого нижнего края второго монитора

        var layout = PopupPlacement.Place(word, 570, 450, work, PopupMetrics.ForScale(1.5));

        Assert.Equal(ArrowSide.Bottom, layout.Side);
        AssertInside(layout.Card, work);
        Assert.True(layout.Arrow.X >= layout.Card.X);
    }

    [Fact]
    public void ArrowTriangle_OpaqueAtBaseCenter_TransparentAtTopCorners()
    {
        const int w = 20, h = 10;
        var px = new uint[w * h];

        ArrowWindow.FillTriangle(px, w, h, ArrowSide.Top, 0xFF2C2C2C);

        Assert.Equal(0xFFu, px[(h - 1) * w + w / 2] >> 24);      // основание по центру — непрозрачно
        Assert.Equal(0u, px[0] >> 24);                            // левый верхний угол — прозрачен
        Assert.Equal(0u, px[w - 1] >> 24);                        // правый верхний угол — прозрачен
        uint c = px[(h - 1) * w + w / 2];
        Assert.Equal(0x2Cu, (c >> 16) & 0xFF);                    // цвет сохранён (premultiplied при alpha=255)
    }

    [Fact]
    public void ArrowTriangle_BottomSide_IsMirrored()
    {
        const int w = 20, h = 10;
        var px = new uint[w * h];

        ArrowWindow.FillTriangle(px, w, h, ArrowSide.Bottom, 0xFFFFFFFF);

        Assert.Equal(0xFFu, px[w / 2] >> 24);                     // основание сверху
        Assert.Equal(0u, px[(h - 1) * w] >> 24);                  // нижние углы прозрачны
    }
}
