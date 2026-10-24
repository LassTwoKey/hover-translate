using HoverTranslate.Core.Capture;

namespace HoverTranslate.Core.Tests;

public sealed class ImageOpsTests
{
    private static BgraImage Pixel(byte b, byte g, byte r) => new([b, g, r, 255], 1, 1);

    [Fact]
    public void Gray_IsMinOfChannels()
    {
        Assert.Equal(40, ImageOps.ToGrayScaled(Pixel(b: 170, g: 170, r: 40), 1).Pixels[0]);
        Assert.Equal(255, ImageOps.ToGrayScaled(Pixel(255, 255, 255), 1).Pixels[0]);
        Assert.Equal(0, ImageOps.ToGrayScaled(Pixel(0, 0, 0), 1).Pixels[0]);
    }

    [Fact]
    public void Gray_WhiteTextOnColoredBackground_KeepsContrast()
    {
        // Светлый текст на бирюзовом фоне: по яркости разница ~100, по min(R,G,B) — больше 200.
        byte text = ImageOps.ToGrayScaled(Pixel(255, 255, 255), 1).Pixels[0];
        byte background = ImageOps.ToGrayScaled(Pixel(b: 200, g: 190, r: 20), 1).Pixels[0];
        Assert.True(text - background > 200);
    }
}
