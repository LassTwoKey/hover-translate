using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Models;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HoverTranslate.Core.TextSource;

/// <summary>
/// Отладочный дамп OCR (режим <c>--debug</c>): на каждое распознавание — снимок области (<c>-src.png</c>),
/// картинка, поданная в OCR (<c>-ocr.png</c>), и текст с рамками слов (<c>.txt</c>). Хранятся последние
/// <see cref="MaxFiles"/> файлов; сбой записи в лог, на работу программы не влияет.
/// </summary>
public sealed class OcrDebugDump(ILogger<OcrDebugDump> log)
{
    /// <summary>Сколько файлов держать в папке (по три на распознавание).</summary>
    public const int MaxFiles = 150;

    private readonly Lock _gate = new();

    /// <summary>Папка дампа; по умолчанию <see cref="AppPaths.OcrDebugDir"/>.</summary>
    public string Folder { get; set; } = AppPaths.OcrDebugDir;

    /// <param name="source">Снимок области как захвачен.</param>
    /// <param name="ocrInput">Серая картинка, поданная в OCR.</param>
    /// <param name="rawText">Текст, как его вернул OCR (с переводами строк).</param>
    /// <param name="lines">Строки и слова в координатах экрана.</param>
    /// <param name="header">Обстоятельства: область, способ захвата, режим окна.</param>
    public async Task SaveAsync(BgraImage source, GrayImage ocrInput, string rawText, IReadOnlyList<OcrLineBox> lines, string header)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Folder);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(Folder, name);

            await File.WriteAllBytesAsync(path + "-src.png", await EncodePngAsync(source.Pixels, source.Width, source.Height)).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path + "-ocr.png", await EncodePngAsync(GrayToBgra(ocrInput), ocrInput.Width, ocrInput.Height)).ConfigureAwait(false);
            await File.WriteAllTextAsync(path + ".txt", FormatText(rawText, lines, header), new UTF8Encoding(true)).ConfigureAwait(false);
            Trim();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Не удалось сохранить отладочный снимок OCR в {Dir}", Folder);
        }
    }

    internal static string FormatText(string rawText, IReadOnlyList<OcrLineBox> lines, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine("--- сырой текст OCR");
        sb.AppendLine(rawText);
        sb.AppendLine();
        sb.AppendLine("--- строки: слово [x,y ширина×высота] в пикселях экрана");
        for (int i = 0; i < lines.Count; i++)
            sb.AppendLine($"{i}: " + string.Join("  ", lines[i].Words.Select(w =>
                FormattableString.Invariant($"{w.Text} [{w.Bounds.X:0},{w.Bounds.Y:0} {w.Bounds.Width:0}×{w.Bounds.Height:0}]"))));
        return sb.ToString();
    }

    private static byte[] GrayToBgra(GrayImage gray)
    {
        var bgra = new byte[gray.Pixels.Length * 4];
        for (int i = 0, p = 0; i < gray.Pixels.Length; i++, p += 4)
        {
            bgra[p] = bgra[p + 1] = bgra[p + 2] = gray.Pixels[i];
            bgra[p + 3] = 255;
        }
        return bgra;
    }

    private static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height)
    {
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    /// <summary>Оставляет последние <see cref="MaxFiles"/> файлов: имена начинаются со времени, поэтому порядок имён — порядок записи.</summary>
    private void Trim()
    {
        lock (_gate)
        {
            var files = System.IO.Directory.GetFiles(Folder).Order(StringComparer.Ordinal).ToArray();
            foreach (string old in files.Take(Math.Max(0, files.Length - MaxFiles)))
            {
                try { File.Delete(old); }
                catch (IOException) { /* занят просмотрщиком — удалим в следующий раз */ }
            }
        }
    }
}
