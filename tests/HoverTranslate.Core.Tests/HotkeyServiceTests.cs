using HoverTranslate.Core.Hotkeys;
using HoverTranslate.Core.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace HoverTranslate.Core.Tests;

/// <summary>
/// Настоящий RegisterHotKey. Два сервиса на двух служебных окнах моделируют «две программы»: окно может
/// зарегистрировать сочетание, только если его не держит другое. Сочетания — Ctrl+Alt+Shift+F21/F22, которыми никто не пользуется.
/// </summary>
public sealed class HotkeyServiceTests : IDisposable
{
    private static readonly Hotkey KeyA = Hotkey.Parse("Ctrl+Alt+Shift+F21");
    private static readonly Hotkey KeyB = Hotkey.Parse("Ctrl+Alt+Shift+F22");

    private readonly MessageWindow _windowOne = MessageWindow.Start("test-1");
    private readonly MessageWindow _windowTwo = MessageWindow.Start("test-2");
    private readonly HotkeyService _one;
    private readonly HotkeyService _two;

    public HotkeyServiceTests()
    {
        _one = new HotkeyService(_windowOne, NullLogger<HotkeyService>.Instance);
        _two = new HotkeyService(_windowTwo, NullLogger<HotkeyService>.Instance);
    }

    [Fact]
    public void Register_Succeeds_AndReportsAssignedHotkey()
    {
        Assert.True(_one.Register(HotkeyAction.Lookup, KeyA));
        Assert.Equal(KeyA, _one.GetRegistered(HotkeyAction.Lookup));
        Assert.Null(_one.GetRegistered(HotkeyAction.AreaTranslate));
    }

    [Fact]
    public void Register_HotkeyHeldByAnotherProgram_Fails_AndKeepsPreviousOne()
    {
        _one.Register(HotkeyAction.Lookup, KeyA);
        Assert.True(_two.Register(HotkeyAction.Lookup, KeyB));

        Assert.False(_two.Register(HotkeyAction.Lookup, KeyA));
        Assert.Equal(KeyB, _two.GetRegistered(HotkeyAction.Lookup)); // прежнее сочетание осталось
        Assert.False(_one.CanRegister(KeyB)); // а оно по-прежнему занято у второго окна
    }

    [Fact]
    public void CanRegister_FreeHotkey_StaysFree()
    {
        Assert.True(_one.CanRegister(KeyA));
        Assert.True(_two.Register(HotkeyAction.Lookup, KeyA)); // проба не заняла сочетание
    }

    [Fact]
    public void CanRegister_HotkeyHeldByAnotherProgram_False_OwnHotkey_True()
    {
        _one.Register(HotkeyAction.Lookup, KeyA);
        Assert.False(_two.CanRegister(KeyA));
        Assert.True(_one.CanRegister(KeyA)); // своё сочетание не считается занятым
    }

    [Fact]
    public void Suspend_ReleasesHotkeys_ResumeTakesThemBack()
    {
        _one.Register(HotkeyAction.Lookup, KeyA);
        Assert.False(_two.CanRegister(KeyA));

        _one.SetSuspended("тест", true);
        Assert.True(_two.CanRegister(KeyA), "приостановленное сочетание должно быть свободно для других программ");
        Assert.Equal(KeyA, _one.GetRegistered(HotkeyAction.Lookup)); // назначение помнится

        _one.SetSuspended("тест", false);
        Assert.False(_two.CanRegister(KeyA));
    }

    [Fact]
    public void Suspend_SeveralReasons_ResumesOnlyWhenAllCleared()
    {
        _one.Register(HotkeyAction.Lookup, KeyA);
        _one.SetSuspended("исключение", true);
        _one.SetSuspended("запись сочетания", true);

        _one.SetSuspended("исключение", false);
        Assert.True(_two.CanRegister(KeyA), "вторая причина ещё действует");

        _one.SetSuspended("запись сочетания", false);
        Assert.False(_two.CanRegister(KeyA));
    }

    [Fact]
    public void Register_WhileSuspended_AppliedOnResume()
    {
        _one.SetSuspended("тест", true);
        Assert.True(_one.Register(HotkeyAction.Lookup, KeyA)); // регистрация откладывается
        Assert.True(_two.CanRegister(KeyA));

        _one.SetSuspended("тест", false);
        Assert.False(_two.CanRegister(KeyA));
        Assert.Equal(KeyA, _one.GetRegistered(HotkeyAction.Lookup));
    }

    [Fact]
    public void Register_ChangeHotkey_ReleasesOldOne()
    {
        _one.Register(HotkeyAction.Lookup, KeyA);
        Assert.True(_one.Register(HotkeyAction.Lookup, KeyB));
        Assert.True(_two.CanRegister(KeyA));
        Assert.False(_two.CanRegister(KeyB));
    }

    [Fact]
    public void Unregister_ReleasesHotkey()
    {
        _one.Register(HotkeyAction.Lookup, KeyA);
        _one.Unregister(HotkeyAction.Lookup);
        Assert.Null(_one.GetRegistered(HotkeyAction.Lookup));
        Assert.True(_two.CanRegister(KeyA));
    }

    [Fact]
    public void SuspendWithoutHotkeys_IsHarmless()
    {
        _one.SetSuspended("тест", true);
        _one.SetSuspended("тест", true);
        _one.SetSuspended("тест", false);
        Assert.True(_one.Register(HotkeyAction.Lookup, KeyA));
    }

    public void Dispose()
    {
        _one.Dispose();
        _two.Dispose();
        _windowOne.Dispose();
        _windowTwo.Dispose();
    }
}
