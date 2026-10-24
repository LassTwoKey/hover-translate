using HoverTranslate.Core.Hotkeys;
using HoverTranslate.Core.Storage;
using HoverTranslate.Core.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace HoverTranslate.Core.Tests;

public sealed class AppSettingsTests : UserDbFixture
{
    private AppSettings Create(out SettingsStore store)
    {
        store = new SettingsStore(DbPath);
        return new AppSettings(store);
    }

    [Fact]
    public void Defaults()
    {
        var s = Create(out _);
        Assert.Equal(100, s.PopupFontPercent);
        Assert.Equal(90, s.PopupOpacityPercent);
        Assert.Equal(PopupTheme.System, s.PopupTheme);
        Assert.Equal(0, s.PopupAutoCloseSeconds);
        Assert.True(s.PopupExcludeFromCapture); // скрыта от снимков экрана, пока не выключат
        Assert.True(s.HistoryEnabled);
        Assert.True(s.CacheEnabled);
        Assert.Null(s.SpeechVoice);
        Assert.Empty(s.ExcludedProcesses);
    }

    [Fact]
    public void Values_AreReadOnEveryAccess_SoChangesApplyWithoutRestart()
    {
        var s = Create(out var store);
        store.Set(SettingKeys.PopupFontPercent, "120");
        Assert.Equal(120, s.PopupFontPercent);
        store.Set(SettingKeys.PopupFontPercent, "110");
        Assert.Equal(110, s.PopupFontPercent);
    }

    [Theory]
    [InlineData("200", 150)]
    [InlineData("10", 80)]
    [InlineData("abc", 100)]
    [InlineData("", 100)]
    [InlineData("-5", 80)]
    public void FontPercent_ClampedOrDefault(string stored, int expected)
    {
        var s = Create(out var store);
        store.Set(SettingKeys.PopupFontPercent, stored);
        Assert.Equal(expected, s.PopupFontPercent);
    }

    [Theory]
    [InlineData("light", PopupTheme.Light)]
    [InlineData("DARK", PopupTheme.Dark)]
    [InlineData("system", PopupTheme.System)]
    [InlineData("что-то", PopupTheme.System)]
    public void Theme_Parsed(string stored, PopupTheme expected)
    {
        var s = Create(out var store);
        store.Set(SettingKeys.PopupTheme, stored);
        Assert.Equal(expected, s.PopupTheme);
    }

    [Fact]
    public void Booleans_ExplicitValueWinsOverDefault()
    {
        var s = Create(out var store);
        store.Set(SettingKeys.PopupExcludeFromCapture, "false");
        store.Set(SettingKeys.HistoryEnabled, "false");
        store.Set(SettingKeys.CacheEnabled, "false");
        Assert.False(s.PopupExcludeFromCapture);
        Assert.False(s.HistoryEnabled);
        Assert.False(s.CacheEnabled);
        store.Set(SettingKeys.PopupExcludeFromCapture, "мусор");
        Assert.True(s.PopupExcludeFromCapture); // непонятное значение — умолчание
    }

    [Fact]
    public void SpeechVoice_EmptyMeansAutomatic()
    {
        var s = Create(out var store);
        store.Set(SettingKeys.SpeechVoice, "");
        Assert.Null(s.SpeechVoice);
        store.Set(SettingKeys.SpeechVoice, "voice-id");
        Assert.Equal("voice-id", s.SpeechVoice);
    }

    [Fact]
    public void ExcludedProcesses_ParsedFromStore()
    {
        var s = Create(out var store);
        store.Set(SettingKeys.ExcludedProcesses, "game\nC:\\Apps\\Tool.exe\nGAME");
        Assert.Equal(["game", "Tool"], s.ExcludedProcesses);
    }
}

public sealed class ProcessExclusionsTests
{
    [Theory]
    [InlineData("game.exe", "game")]
    [InlineData("Game.EXE", "Game")]
    [InlineData("  game  ", "game")]
    [InlineData("\"C:\\Games\\My Game\\game.exe\"", "game")]
    [InlineData("C:/x/y.exe", "y")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize(string? input, string expected) => Assert.Equal(expected, ProcessExclusions.Normalize(input));

    [Fact]
    public void Parse_SplitsByNewlineAndSemicolon_NoDuplicatesIgnoringCase()
    {
        Assert.Equal(["a", "B", "c"], ProcessExclusions.Parse("a\nB;A.exe\r\n\nb\n c "));
        Assert.Empty(ProcessExclusions.Parse(null));
        Assert.Empty(ProcessExclusions.Parse(" \n ; "));
    }

    [Fact]
    public void Format_RoundTrips() =>
        Assert.Equal(["one", "two"], ProcessExclusions.Parse(ProcessExclusions.Format(["one.exe", "two", "ONE"])));

    [Theory]
    [InlineData("game", "Game", true)]
    [InlineData("game\nother", "other", true)]
    [InlineData("game", "gamer", false)]
    [InlineData("game", "?", false)]   // процесс неизвестен (повышенные права) — не исключаем
    [InlineData("?", "?", false)]
    [InlineData("", "game", false)]
    [InlineData(null, "game", false)]
    public void Contains(string? stored, string process, bool expected) =>
        Assert.Equal(expected, ProcessExclusions.Contains(stored, process));
}

public sealed class HotkeyValidatorTests
{
    private static string? Check(string candidate, string? other = null) =>
        HotkeyValidator.Validate(Hotkey.Parse(candidate), "Перевод области", other is null ? null : Hotkey.Parse(other));

    [Theory]
    [InlineData("Alt+Q")]
    [InlineData("Ctrl+Alt+Q")]
    [InlineData("Ctrl+Shift+Q")]
    [InlineData("Alt+F1")]
    [InlineData("Ctrl+F5")]
    [InlineData("Shift+F2")]
    [InlineData("Win+Alt+T")]
    public void Acceptable(string text) => Assert.Null(Check(text));

    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+7")]
    [InlineData("Shift+A")]
    [InlineData("Shift+Space")]
    public void OnlyCtrlOrShift_WithTypingKey_Rejected(string text) => Assert.NotNull(Check(text));

    [Theory]
    [InlineData("Alt+F4")]
    [InlineData("alt+tab")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Ctrl+Shift+Esc")]
    [InlineData("Win+L")]
    public void Reserved_Rejected(string text) => Assert.Contains("зарезервировано", Check(text));

    [Fact]
    public void SameAsOtherAction_Rejected()
    {
        string? error = Check("Alt+W", other: "alt+w");
        Assert.NotNull(error);
        Assert.Contains("Перевод области", error);
    }

    [Fact]
    public void DifferentFromOtherAction_Accepted() => Assert.Null(Check("Alt+Q", other: "Alt+W"));

    [Fact]
    public void NoModifier_Rejected() =>
        Assert.NotNull(HotkeyValidator.Validate(new Hotkey(HotkeyModifiers.None, 'Q'), "x", null));
}

public sealed class DiskUsageTests : UserDbFixture
{
    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1023, "1023 Б")]
    [InlineData(1024, "1 КБ")]
    [InlineData(1536, "1,5 КБ")]
    [InlineData(1024 * 1024, "1 МБ")]
    [InlineData(5 * 1024 * 1024 + 512 * 1024, "5,5 МБ")]
    public void Format(long bytes, string expected) => Assert.Equal(expected, DiskUsage.Format(bytes));

    [Fact]
    public void DatabaseSize_SumsWalAndShm_MissingIsZero()
    {
        Assert.Equal(0, DiskUsage.DatabaseSize(DbPath));
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllBytes(DbPath, new byte[100]);
        File.WriteAllBytes(DbPath + "-wal", new byte[20]);
        File.WriteAllBytes(DbPath + "-shm", new byte[3]);
        Assert.Equal(123, DiskUsage.DatabaseSize(DbPath));
    }
}

public sealed class TranslationCacheLimitsTests : UserDbFixture
{
    private SqliteTranslationCache Create(Func<bool>? enabled = null, int max = 1000) =>
        new(Path.Combine(Path.GetDirectoryName(DbPath)!, "cache.db"), NullLogger<SqliteTranslationCache>.Instance, enabled, max);

    [Fact]
    public void Clear_RemovesEverything_AndShrinksFile()
    {
        using var cache = Create();
        for (int i = 0; i < 300; i++) cache.Put("Sentence number " + i + " with some padding text to take space.", new string('я', 200));
        Assert.Equal(300, cache.Count);
        string file = Path.Combine(Path.GetDirectoryName(DbPath)!, "cache.db");
        long before = DiskUsage.DatabaseSize(file);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.Get("Sentence number 5 with some padding text to take space."));
        Assert.True(DiskUsage.DatabaseSize(file) < before, "после очистки файл должен уменьшиться");
        cache.Put("again", "снова"); // после очистки кэш работает
        Assert.Equal("снова", cache.Get("again"));
    }

    [Fact]
    public void Disabled_NeitherReadsNorWrites_ButKeepsContent()
    {
        bool enabled = true;
        using var cache = Create(() => enabled);
        cache.Put("Hello.", "Привет.");
        enabled = false;
        Assert.Null(cache.Get("Hello."));
        cache.Put("Other.", "Другое.");
        enabled = true;
        Assert.Equal("Привет.", cache.Get("Hello."));
        Assert.Null(cache.Get("Other."));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Overflow_DropsOldestKeepsNewest()
    {
        using var cache = Create(max: 400);
        // 200 записей — первая проверка предела: пока в пределах
        for (int i = 0; i < 200; i++) cache.Put("s" + i, "t" + i);
        Assert.Equal(200, cache.Count);
        // ещё 400 — всего 600; на 400-й записи проверка сработает и оставит 90 % предела
        for (int i = 200; i < 600; i++) cache.Put("s" + i, "t" + i);
        Assert.InRange(cache.Count, 360, 400 + 200); // между проверками может накопиться до TrimEvery записей сверх предела
        Assert.Equal("t599", cache.Get("s599")); // новые на месте
    }

    [Fact]
    public void Overflow_TrimRemovesOldestByCreationTime()
    {
        var path = Path.Combine(Path.GetDirectoryName(DbPath)!, "cache.db");
        using (var cache = Create(max: 100))
        {
            for (int i = 0; i < 150; i++) cache.Put("old" + i, "x"); // автоматическая проверка — только на 200-й записи
        }
        // время создания по порядку строки источника: «old0» — самая старая, «old99» — самая новая
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE translations SET created = 1000 + (SELECT COUNT(*) FROM translations t2 WHERE t2.source < translations.source)";
            cmd.ExecuteNonQuery();
        }
        using var again = Create(max: 100);
        Assert.Equal(150, again.Count);
        again.Trim(); // 150 > 100: остаётся 90 % предела, самые старые удалены
        Assert.Equal(90, again.Count);
        Assert.Null(again.Get("old0"));
        Assert.Equal("x", again.Get("old99"));
        again.Trim(); // в пределах — ничего не удаляется
        Assert.Equal(90, again.Count);
    }
}

public sealed class AutostartTests : IDisposable
{
    private readonly string _subKey = @"Software\HoverTranslateTests\" + Guid.NewGuid().ToString("N");

    private Autostart Create(string exe) => new(exe, _subKey, "HoverTranslate");

    [Fact]
    public void EnableDisable_WritesQuotedPathToRunKey()
    {
        var autostart = Create(@"C:\Program Files\HoverTranslate\HoverTranslate.exe");
        Assert.False(autostart.IsEnabled);
        autostart.SetEnabled(true);
        Assert.True(autostart.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey))
            Assert.Equal("\"C:\\Program Files\\HoverTranslate\\HoverTranslate.exe\"", key!.GetValue("HoverTranslate"));
        autostart.SetEnabled(false);
        Assert.False(autostart.IsEnabled);
        autostart.SetEnabled(false); // повторное отключение — не ошибка
    }

    [Fact]
    public void EntryForAnotherPath_IsNotEnabled_AndIsReplacedOnEnable()
    {
        Create(@"C:\old\HoverTranslate.exe").SetEnabled(true);
        var current = Create(@"C:\new\HoverTranslate.exe");
        Assert.False(current.IsEnabled);
        current.SetEnabled(true);
        Assert.True(current.IsEnabled);
        Assert.False(Create(@"C:\old\HoverTranslate.exe").IsEnabled);
    }

    [Fact]
    public void Disable_WhenKeyMissing_NoError() => Create(@"C:\x.exe").SetEnabled(false);

    [Fact]
    public void Enable_WithoutPath_Throws() => Assert.Throws<InvalidOperationException>(() => Create("").SetEnabled(true));

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\HoverTranslateTests", throwOnMissingSubKey: false);
}
