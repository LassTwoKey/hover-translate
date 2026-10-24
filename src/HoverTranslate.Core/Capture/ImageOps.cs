namespace HoverTranslate.Core.Capture;

/// <summary>Изображение в оттенках серого, 1 байт на пиксель.</summary>
public sealed class GrayImage(byte[] pixels, int width, int height)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
}

public static class ImageOps
{
    /// <summary>
    /// BGRA -> серый с билинейным увеличением в <paramref name="scale"/> раз (для мелкого текста в OCR).
    /// Серый берётся как min(R,G,B), а не как яркость: светлый (белый) текст остаётся светлым, а цветной фон —
    /// тёмным, поэтому текст игры поверх волнистого цветного фона отделяется от него (на 15 снимках из игр
    /// слово читалось 6/15 при яркости и 13/15 при min). Тёмный текст на светлом фоне от этого не меняется.
    /// </summary>
    public static GrayImage ToGrayScaled(BgraImage src, double scale)
    {
        int sw = src.Width, sh = src.Height;
        var gray = new byte[sw * sh];
        var bgra = src.Pixels;
        for (int i = 0, p = 0; i < gray.Length; i++, p += 4)
            gray[i] = Math.Min(bgra[p], Math.Min(bgra[p + 1], bgra[p + 2]));
        if (Math.Abs(scale - 1) < 0.01)
            return new GrayImage(gray, sw, sh);

        int dw = Math.Max(1, (int)Math.Round(sw * scale)), dh = Math.Max(1, (int)Math.Round(sh * scale));
        var dst = new byte[dw * dh];
        double inv = 1 / scale;
        for (int y = 0; y < dh; y++)
        {
            double fy = Math.Clamp((y + 0.5) * inv - 0.5, 0, sh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, sh - 1);
            int wy = (int)((fy - y0) * 256);
            int row0 = y0 * sw, row1 = y1 * sw, drow = y * dw;
            for (int x = 0; x < dw; x++)
            {
                double fx = Math.Clamp((x + 0.5) * inv - 0.5, 0, sw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, sw - 1);
                int wx = (int)((fx - x0) * 256);
                int top = gray[row0 + x0] * (256 - wx) + gray[row0 + x1] * wx;
                int bottom = gray[row1 + x0] * (256 - wx) + gray[row1 + x1] * wx;
                dst[drow + x] = (byte)((top * (256 - wy) + bottom * wy) >> 16);
            }
        }
        return new GrayImage(dst, dw, dh);
    }

    /// <summary>
    /// Кадр «пустой» (почти однотонный) — так выглядит чёрный кадр от окна с защитой от захвата
    /// или неудачный BitBlt. Проверяется по выборке пикселей.
    /// </summary>
    public static bool IsBlank(BgraImage image)
    {
        var px = image.Pixels;
        int count = image.Width * image.Height;
        if (count == 0) return true;
        int step = Math.Max(1, count / 4096);
        int min = 255, max = 0;
        for (int i = 0; i < count; i += step)
        {
            int p = i * 4;
            int g = (px[p] + px[p + 1] + px[p + 2]) / 3;
            if (g < min) min = g;
            if (g > max) max = g;
            if (max - min > 12) return false;
        }
        return true;
    }
}
