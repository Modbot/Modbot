using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The desktop overlay's settings: the shortcut that brings it up over VRChat, how solid it is,
/// and whether a press should open it or close it.
/// </summary>
/// <remarks>
/// A shortcut is claimed across the whole machine, so what counts as one and what does not is
/// worth pinning down here rather than finding out when a moderator's letter key stops working in
/// every other program.
/// </remarks>
public class DesktopOverlayShortcutTests
{
    [Theory]
    [InlineData("mod+alt+m", ShortcutKeys.Ctrl | ShortcutKeys.Alt, 'M')]
    [InlineData("mod+m", ShortcutKeys.Ctrl, 'M')]
    [InlineData("alt+a", ShortcutKeys.Alt, 'A')]
    [InlineData("mod+shift+z", ShortcutKeys.Ctrl | ShortcutKeys.Shift, 'Z')]
    [InlineData("mod+7", ShortcutKeys.Ctrl, '7')]
    public void ALetterOrDigitWithAModifierIsRead(string shortcut, int modifiers, char key)
    {
        var keys = DesktopOverlayKeys.Read(shortcut);

        Assert.NotNull(keys);
        Assert.Equal(modifiers, keys.Value.Modifiers);
        Assert.Equal((int)key, keys.Value.Key);
    }

    [Theory]
    [InlineData("mod+f1", 0x70)]
    [InlineData("mod+f12", 0x7B)]
    [InlineData("alt+space", 0x20)]
    [InlineData("mod+insert", 0x2D)]
    [InlineData("mod+alt+arrowdown", 0x28)]
    [InlineData("mod+pagedown", 0x22)]
    public void TheNamedKeysAreRead(string shortcut, int key)
    {
        var keys = DesktopOverlayKeys.Read(shortcut);

        Assert.NotNull(keys);
        Assert.Equal(key, keys.Value.Key);
    }

    [Fact]
    public void TheOrderTheModifiersAreWrittenInDoesNotMatter()
        => Assert.Equal(DesktopOverlayKeys.Read("mod+alt+m"), DesktopOverlayKeys.Read("alt+mod+m"));

    [Fact]
    public void ItIsReadWhateverCaseItIsWrittenIn()
        => Assert.Equal(DesktopOverlayKeys.Read("mod+alt+m"), DesktopOverlayKeys.Read("MOD+Alt+M"));

    [Theory]
    // A bare key would swallow that key in every program on the machine, the game included.
    [InlineData("m")]
    [InlineData("f5")]
    // Only modifiers is not a combination.
    [InlineData("mod+alt")]
    [InlineData("mod")]
    // Escape closes the overlay, so it cannot also open it.
    [InlineData("mod+escape")]
    // A chord is something a window can wait for and the operating system cannot.
    [InlineData("g e")]
    [InlineData("mod+g mod+e")]
    // Two ordinary keys.
    [InlineData("mod+m+n")]
    // Nothing anybody has.
    [InlineData("mod+f25")]
    [InlineData("mod+wibble")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void WhatCannotBeAskedForIsRefused(string? shortcut)
    {
        Assert.Null(DesktopOverlayKeys.Read(shortcut));
        Assert.False(DesktopOverlayKeys.CanBeUsed(shortcut));
    }

    [Fact]
    public void TheDefaultIsOneTheClientCouldActuallyRegister()
        => Assert.True(DesktopOverlayKeys.CanBeUsed(DesktopOverlaySettings.DefaultShortcut));

    [Fact]
    public void ADefaultThatCouldNotBeRegisteredWouldLeaveNoWayIn()
    {
        // A settings file naming a combination nobody could ask for falls back rather than
        // leaving the moderator with an overlay and no key that opens it.
        var settings = DesktopOverlaySettings.Default with { Shortcut = "wibble" };

        Assert.Equal(DesktopOverlaySettings.DefaultShortcut, settings.ShortcutOrDefault);
    }

    [Fact]
    public void AShortcutThatWasAskedForIsTheOneUsed()
    {
        var settings = DesktopOverlaySettings.Default with { Shortcut = "mod+shift+f9" };

        Assert.Equal("mod+shift+f9", settings.ShortcutOrDefault);
    }
}

/// <summary>Opacity, and what a key press does to the window.</summary>
public class DesktopOverlaySettingsTests
{
    [Theory]
    [InlineData(0, 20)]
    [InlineData(-5, 20)]
    [InlineData(20, 20)]
    [InlineData(65, 65)]
    [InlineData(100, 100)]
    [InlineData(400, 100)]
    public void OpacityStaysWhereItCanBeSeen(int given, int expected)
        => Assert.Equal(expected, DesktopOverlaySettings.ClampOpacity(given));

    [Fact]
    public void TheGroundsAlphaFollowsTheOpacity()
    {
        Assert.Equal(255, (DesktopOverlaySettings.Default with { Opacity = 100 }).Alpha);
        Assert.Equal(127, (DesktopOverlaySettings.Default with { Opacity = 50 }).Alpha);

        // Out of range in the file still lands inside the range on screen.
        Assert.Equal(51, (DesktopOverlaySettings.Default with { Opacity = 0 }).Alpha);
    }

    [Fact]
    public void TheShortcutTurnsTheWindowOver()
    {
        var on = DesktopOverlaySettings.Default with { On = true };

        Assert.True(on.NextShowing(showing: false));
        Assert.False(on.NextShowing(showing: true));
    }

    [Fact]
    public void EscapeIsNotAShortcutTheClientWillAskFor()
    {
        // Escape is how VRChat opens its own menu. The window used to close on it, which put the
        // two in a fight the game could not win; claiming it across the whole machine would be
        // worse still, because it would take the menu key away in every program.
        Assert.False(DesktopOverlayKeys.CanBeUsed("escape"));
        Assert.False(DesktopOverlayKeys.CanBeUsed("mod+escape"));
    }

    [Fact]
    public void SwitchedOffNothingOpensIt()
    {
        var off = DesktopOverlaySettings.Default with { On = false };

        Assert.False(off.NextShowing(showing: false));
        Assert.False(off.NextShowing(showing: true));
    }

    [Fact]
    public void ItIsOffUntilSomebodyTurnsItOn()
    {
        Assert.False(DesktopOverlaySettings.Default.On);
        Assert.Equal("mod+alt+m", DesktopOverlaySettings.Default.Shortcut);
        Assert.Equal(90, DesktopOverlaySettings.Default.Opacity);
    }
}

/// <summary>What the Settings page says when the shortcut did not get registered.</summary>
public class DesktopOverlayStatusTests
{
    [Fact]
    public void ARegisteredShortcutHasNothingToSay()
    {
        var status = new DesktopOverlayStatus(DesktopOverlaySettings.Default, ShortcutState.Registered, Showing: false);

        Assert.Null(status.Problem);
    }

    [Fact]
    public void ATakenShortcutIsNamedInTheWordsAPersonReadsIt()
    {
        var status = new DesktopOverlayStatus(DesktopOverlaySettings.Default, ShortcutState.Taken, Showing: false);

        Assert.Equal("Ctrl Alt M is already taken by another program.", status.Problem);
    }

    [Theory]
    [InlineData(ShortcutState.NotUnderstood)]
    [InlineData(ShortcutState.NotOnThisSystem)]
    public void EveryOtherFailureSaysWhatFailed(ShortcutState state)
    {
        var status = new DesktopOverlayStatus(DesktopOverlaySettings.Default, state, Showing: false);

        Assert.False(string.IsNullOrWhiteSpace(status.Problem));
    }

    [Fact]
    public void SwitchedOffIsNotAFailure()
    {
        Assert.Null(DesktopOverlayStatus.None.Problem);
        Assert.False(DesktopOverlayStatus.None.Showing);
    }
}
