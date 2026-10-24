using HoverTranslate.Core.Hotkeys;

namespace HoverTranslate.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+Q", HotkeyModifiers.Alt, 'Q')]
    [InlineData("alt + q", HotkeyModifiers.Alt, 'Q')]
    [InlineData("Ctrl+Shift+F1", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x70)]
    [InlineData("Win+Alt+Space", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x20)]
    [InlineData("Control+7", HotkeyModifiers.Control, '7')]
    [InlineData("Alt+F24", HotkeyModifiers.Alt, 0x87)]
    public void Parse_Valid(string text, HotkeyModifiers mods, int vk) =>
        Assert.Equal(new Hotkey(mods, vk), Hotkey.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("Q")]           // без модификатора
    [InlineData("Alt")]         // без клавиши
    [InlineData("Alt+Q+W")]     // две клавиши
    [InlineData("Alt+F25")]
    [InlineData("Alt+Foo")]
    [InlineData("Alt++")]
    public void Parse_Invalid(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Theory]
    [InlineData("alt+q", "Alt+Q")]
    [InlineData("Shift+Ctrl+F5", "Ctrl+Shift+F5")]
    [InlineData("Alt+Win+Space", "Alt+Win+Space")]
    public void ToString_IsCanonical(string text, string expected) => Assert.Equal(expected, Hotkey.Parse(text).ToString());

    [Fact]
    public void RoundTrip_Defaults()
    {
        Assert.Equal(Hotkey.DefaultLookup, Hotkey.Parse(Hotkey.DefaultLookup.ToString()));
        Assert.Equal("Alt+Q", Hotkey.DefaultLookup.ToString());
        Assert.Equal("Alt+W", Hotkey.DefaultAreaTranslate.ToString());
    }
}
