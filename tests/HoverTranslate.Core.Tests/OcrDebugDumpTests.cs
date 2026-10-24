using HoverTranslate.Core.Capture;
using HoverTranslate.Core.Models;
using HoverTranslate.Core.TextSource;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoverTranslate.Core.Tests;

public sealed class OcrDebugDumpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ht-ocr-dump-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static OcrLineBox[] Lines() =>
    [
        new([new OcrWordBox("Hello", new ScreenRect(10, 20, 40, 12)), new OcrWordBox("world.", new ScreenRect(55, 20, 44, 12))]),
    ];

    private static (BgraImage Source, GrayImage Gray) Images() =>
        (new BgraImage(new byte[4 * 4 * 4], 4, 4), new GrayImage(new byte[8 * 8], 8, 8));

    [Fact]
    public async Task Save_WritesSourceOcrInputAndText()
    {
        var dump = new OcrDebugDump(NullLogger<OcrDebugDump>.Instance) { Folder = _dir };
        var (source, gray) = Images();

        await dump.SaveAsync(source, gray, "Hello world.", Lines(), "окно: тест");

        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(3, files.Length);
        Assert.EndsWith("-ocr.png", files[0]);
        Assert.EndsWith("-src.png", files[1]);
        Assert.EndsWith(".txt", files[2]);
        string text = await File.ReadAllTextAsync(Directory.GetFiles(_dir, "*.txt").Single(), TestContext.Current.CancellationToken);
        Assert.Contains("окно: тест", text);
        Assert.Contains("Hello world.", text);
        Assert.Contains("world. [55,20 44×12]", text);
    }

    [Fact]
    public async Task Save_KeepsOnlyLastMaxFiles()
    {
        var dump = new OcrDebugDump(NullLogger<OcrDebugDump>.Instance) { Folder = _dir };
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < OcrDebugDump.MaxFiles + 10; i++)
            await File.WriteAllTextAsync(Path.Combine(_dir, $"2000010{i % 10}-{i:D6}-000.txt"), "старый", TestContext.Current.CancellationToken);
        var (source, gray) = Images();

        await dump.SaveAsync(source, gray, "", [], "");

        Assert.Equal(OcrDebugDump.MaxFiles, Directory.GetFiles(_dir).Length);
        // новые файлы (имя начинается с сегодняшней даты) не удаляются
        Assert.Equal(3, Directory.GetFiles(_dir).Count(f => Path.GetFileName(f).StartsWith(DateTime.Now.ToString("yyyy"), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Save_FailureDoesNotThrow()
    {
        // папка — на самом деле файл: создать каталог нельзя
        await File.WriteAllTextAsync(_dir, "x", TestContext.Current.CancellationToken);
        var dump = new OcrDebugDump(NullLogger<OcrDebugDump>.Instance) { Folder = Path.Combine(_dir, "sub") };
        var (source, gray) = Images();

        await dump.SaveAsync(source, gray, "", [], "");

        File.Delete(_dir);
    }

    [Theory]
    [InlineData(WindowMode.Windowed, "окно 800×600")]
    [InlineData(WindowMode.Borderless, "borderless")]
    [InlineData(WindowMode.Exclusive, "exclusive")]
    public void WindowModeInfo_Text(WindowMode mode, string expected) =>
        Assert.Contains(expected, new WindowModeInfo(mode, 800, 600).ToString());

    [Fact]
    public void CaptureReport_Text()
    {
        Assert.Equal("WGC", new CaptureReport("WGC", false, null).ToString());
        Assert.Equal("BitBlt, кадр пустой; WGC: пустой кадр", new CaptureReport("BitBlt", true, "WGC: пустой кадр").ToString());
    }

    [Fact]
    public void DescribeMode_OnLiveDesktop_DoesNotThrow()
    {
        var info = WindowInfo.DescribeMode(new ScreenPoint(10, 10));
        Assert.False(string.IsNullOrEmpty(info.ToString()));
    }
}
