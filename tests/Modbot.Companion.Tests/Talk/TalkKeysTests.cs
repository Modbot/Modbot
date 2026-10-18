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
    public void ABareLetterIsAllowedBecauseItIsPressedNotClaimed()
    {
        Assert.Equal(new ShortcutKeys(0, 'M'), TalkKeys.Read("m"));
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
