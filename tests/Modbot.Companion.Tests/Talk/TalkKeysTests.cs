using Modbot.Companion.Presentation;
using Modbot.Companion.Talk;

namespace Modbot.Companion.Tests.Talk;

/// <summary>
/// The keys a person records for Discord, turned into the keys Windows presses.
/// </summary>
public class TalkKeysTests
{
    [Fact]
    public void AFunctionKeyNeedsNoModifier()
    {
        Assert.Equal(new ShortcutKeys(0, 0x7C), TalkKeys.Read("f13"));
        Assert.Equal(new ShortcutKeys(0, 0x87), TalkKeys.Read("f24"));
    }

    [Fact]
    public void CtrlShiftMIsTheTwoModifiersAndTheLetter()
    {
        Assert.Equal(
            new ShortcutKeys(ShortcutKeys.Ctrl | ShortcutKeys.Shift, 'M'),
            TalkKeys.Read("mod+shift+m"));
    }

    [Fact]
    public void TheOrderOfTheModifiersDoesNotMatter()
    {
        Assert.Equal(TalkKeys.Read("mod+alt+x"), TalkKeys.Read("alt+mod+x"));
    }

    [Fact]
    public void ABareLetterCanBePressedForDiscord()
    {
        Assert.Equal(new ShortcutKeys(0, 'M'), TalkKeys.Read("m"));
    }

    [Theory]
    [InlineData("f13")]
    [InlineData("f1")]
    [InlineData("shift+f9")]
    [InlineData("mod+m")]
    [InlineData("alt+5")]
    [InlineData("mod+shift+m")]
    [InlineData("mod+space")]
    public void AHotkeyMayBeAFunctionKeyOrNeedCtrlOrAlt(string key)
    {
        Assert.True(TalkKeys.CanBeHotkey(key));
        Assert.Equal(TalkKeys.Read(key), TalkKeys.ReadHotkey(key));
    }

    [Theory]
    [InlineData("m")]
    [InlineData("shift+m")]
    [InlineData("5")]
    [InlineData("num0")]
    [InlineData("space")]
    [InlineData("enter")]
    [InlineData("arrowup")]
    [InlineData("shift+space")]
    public void AHotkeyThatWouldTakeATypingKeyFromEveryProgramIsRefused(string key)
    {
        Assert.NotNull(TalkKeys.Read(key));
        Assert.Null(TalkKeys.ReadHotkey(key));
        Assert.False(TalkKeys.CanBeHotkey(key));
    }

    [Fact]
    public void WhatCannotBePressedIsNotAHotkeyEither()
    {
        Assert.Null(TalkKeys.ReadHotkey("mod+nosuchkey"));
        Assert.Null(TalkKeys.ReadHotkey(null));
    }

    [Theory]
    [InlineData("num0", 0x60)]
    [InlineData("num5", 0x65)]
    [InlineData("num9", 0x69)]
    public void TheNumberPadIsItsOwnKeys(string key, int code)
    {
        Assert.Equal(new ShortcutKeys(0, code), TalkKeys.Read(key));
    }

    [Fact]
    public void CapitalsAndSpacesAroundItAreForgiven()
    {
        Assert.Equal(TalkKeys.Read("f13"), TalkKeys.Read("  F13 "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mod")]
    [InlineData("mod+alt")]
    [InlineData("g e")]
    [InlineData("a+b")]
    [InlineData("mod+nosuchkey")]
    [InlineData("f25")]
    [InlineData("num10")]
    public void WhatCannotBePressedIsRefused(string? key)
    {
        Assert.Null(TalkKeys.Read(key));
        Assert.False(TalkKeys.CanBePressed(key));
    }

    [Theory]
    [InlineData("mod+shift+m", "Ctrl Shift M")]
    [InlineData("f13", "F13")]
    [InlineData("num0", "Num 0")]
    [InlineData("alt+f4", "Alt F4")]
    public void AKeyIsDescribedTheWayAPersonReadsIt(string key, string described)
    {
        Assert.Equal(described, TalkKeys.Describe(key));
    }
}
