using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The button's stick shortcut: its default, the words it puts under the button's label, and how
/// the settings file's names are read.
/// </summary>
public class ButtonShortcutTests
{
    [Fact]
    public void TheDefaultIsTheRightStickPulledBackForFiveSeconds()
    {
        Assert.Equal(ShortcutStick.Right, ButtonShortcut.Default.Stick);
        Assert.Equal(StickDirection.Back, ButtonShortcut.Default.Direction);
        Assert.Equal(5, ButtonShortcut.Default.Seconds);
        Assert.Equal(TimeSpan.FromSeconds(5), ButtonShortcut.Default.Hold);
        Assert.True(ButtonShortcut.Default.IsOn);
    }

    [Fact]
    public void TheDefaultSaysItselfInShortPlainWordsAndTheButtonKeepsThoseWords()
    {
        Assert.Equal("Right stick back 5s", ButtonShortcut.Default.Text);
        Assert.Equal(OverlayButton.DefaultShortcut, ButtonShortcut.Default.Text);
    }

    [Theory]
    [InlineData(ShortcutStick.Right, StickDirection.Forward, 8, "Right stick forward 8s")]
    [InlineData(ShortcutStick.Left, StickDirection.Left, 2, "Left stick left 2s")]
    [InlineData(ShortcutStick.Left, StickDirection.Right, 3, "Left stick right 3s")]
    public void TheWordsFollowTheStickTheWayAndTheTime(ShortcutStick stick, StickDirection way, int seconds, string text)
        => Assert.Equal(text, new ButtonShortcut(stick, way, seconds).Text);

    [Fact]
    public void WithTheStickOffThereAreNoWordsAndNoSticksListenedTo()
    {
        var off = ButtonShortcut.Default with { Stick = ShortcutStick.Off };

        Assert.Equal("", off.Text);
        Assert.False(off.IsOn);
        Assert.Equal(StickDirection.Back, off.Direction);
    }

    [Theory]
    [InlineData("right", ShortcutStick.Right)]
    [InlineData("LEFT", ShortcutStick.Left)]
    [InlineData(" off ", ShortcutStick.Off)]
    [InlineData("", ShortcutStick.Right)]
    [InlineData(null, ShortcutStick.Right)]
    [InlineData("both", ShortcutStick.Right)]
    [InlineData("99", ShortcutStick.Right)]
    public void AStickIsReadFromAnyCaseAndAnythingElseIsTheRightOne(string? text, ShortcutStick stick)
        => Assert.Equal(stick, ButtonShortcut.ParseStick(text));

    [Theory]
    [InlineData("back", StickDirection.Back)]
    [InlineData("Forward", StickDirection.Forward)]
    [InlineData("left", StickDirection.Left)]
    [InlineData("right", StickDirection.Right)]
    [InlineData(null, StickDirection.Back)]
    [InlineData("sideways", StickDirection.Back)]
    [InlineData("42", StickDirection.Back)]
    public void ADirectionIsReadFromAnyCaseAndAnythingElseIsBack(string? text, StickDirection way)
        => Assert.Equal(way, ButtonShortcut.ParseDirection(text));

    [Fact]
    public void WhatIsWrittenIsReadBack()
    {
        foreach (var stick in Enum.GetValues<ShortcutStick>())
            Assert.Equal(stick, ButtonShortcut.ParseStick(ButtonShortcut.Written(stick)));

        foreach (var way in Enum.GetValues<StickDirection>())
            Assert.Equal(way, ButtonShortcut.ParseDirection(ButtonShortcut.Written(way)));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(3, 3)]
    [InlineData(8, 8)]
    [InlineData(500, 30)]
    public void TheHoldTimeIsKeptBetweenTheShortestAndTheLongest(int? seconds, int expected)
        => Assert.Equal(expected, ButtonShortcut.ClampSeconds(seconds));

    [Fact]
    public void TheSettingsPageOffersTheDefaultAmongTheHoldTimes()
        => Assert.Contains(ButtonShortcut.DefaultSeconds, ButtonShortcut.SecondsChoices);
}
