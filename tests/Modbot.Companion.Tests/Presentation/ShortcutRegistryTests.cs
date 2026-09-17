using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// The keyboard's rules, with no window: a key typed into a text box is text, a page's keys
/// wait while an overlay is open, chords hold their first key, and the last set to claim a key
/// is the one that fires.
/// </summary>
public class ShortcutRegistryTests
{
    private static Shortcut Key(string keys, string label = "", ShortcutGroup group = ShortcutGroup.General, bool page = false, bool hidden = false)
        => new(keys, label.Length == 0 ? keys : label, group, () => { }, page, hidden);

    private static ShortcutRegistry Registry(params Shortcut[] shortcuts)
    {
        var registry = new ShortcutRegistry();
        registry.Set("window", shortcuts);
        return registry;
    }

    [Theory]
    [InlineData("k", true, false, false, "mod+k")]
    [InlineData("K", true, false, false, "mod+k")]
    [InlineData("?", false, false, true, "?")]
    [InlineData("j", false, false, true, "j")]
    [InlineData("enter", false, false, true, "shift+enter")]
    [InlineData("escape", false, false, false, "escape")]
    [InlineData("k", true, true, false, "mod+alt+k")]
    public void AKeyPressIsOneToken(string key, bool mod, bool alt, bool shift, string expected)
    {
        // Shift is what it took to type `?` on one keyboard and not another, so it is left off
        // printable keys and kept only where there is no character to speak for it.
        Assert.Equal(expected, KeyTokens.Token(key, mod, alt, shift));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AModifierOnItsOwnIsNoToken(string? key)
        => Assert.Null(KeyTokens.Token(key, mod: true, alt: false, shift: false));

    [Theory]
    [InlineData("mod+k", "Ctrl K")]
    [InlineData("g e", "G then E")]
    [InlineData("?", "?")]
    [InlineData("escape", "Esc")]
    [InlineData("arrowdown", "↓")]
    [InlineData("shift+enter", "Shift Enter")]
    public void KeysAreDescribedTheWayAPersonReadsThem(string keys, string expected)
        => Assert.Equal(expected, KeyTokens.Describe(keys));

    [Fact]
    public void ASingleKeyRuns()
    {
        var registry = Registry(Key("j"));

        var outcome = registry.Decide("j", pending: null, typing: false, panelOpen: false);

        Assert.Equal("j", outcome.Run?.Keys);
        Assert.Null(outcome.Pending);
    }

    [Fact]
    public void AnUnknownKeyDoesNothing()
    {
        var registry = Registry(Key("j"));

        Assert.Equal(ShortcutOutcome.Nothing, registry.Decide("x", null, false, false));
    }

    [Fact]
    public void ALetterTypedIntoATextBoxIsALetter()
    {
        var registry = Registry(Key("j"), Key("?"), Key("f"));

        Assert.Equal(ShortcutOutcome.Nothing, registry.Decide("j", null, typing: true, panelOpen: false));
        Assert.Equal(ShortcutOutcome.Nothing, registry.Decide("?", null, typing: true, panelOpen: false));
        Assert.Equal(ShortcutOutcome.Nothing, registry.Decide("f", null, typing: true, panelOpen: false));
    }

    [Fact]
    public void CtrlKeysAndEscapeStillFireInATextBox()
    {
        // Ctrl+K in the pairing box still opens the palette, and Esc in the palette's own box
        // still closes it; neither is something a person types.
        var registry = Registry(Key("mod+k"), Key("escape"));

        Assert.Equal("mod+k", registry.Decide("mod+k", null, typing: true, panelOpen: false).Run?.Keys);
        Assert.Equal("escape", registry.Decide("escape", null, typing: true, panelOpen: false).Run?.Keys);
    }

    [Fact]
    public void AChordStartedInATextBoxDoesNotStart()
    {
        var registry = Registry(Key("g e"));

        Assert.Equal(ShortcutOutcome.Nothing, registry.Decide("g", null, typing: true, panelOpen: false));
    }

    [Fact]
    public void APagesKeysWaitWhileAPanelIsOpen()
    {
        var registry = Registry(Key("j", page: true), Key("mod+k"));

        Assert.Equal(ShortcutOutcome.Nothing, registry.Decide("j", null, typing: false, panelOpen: true));
        Assert.Equal("mod+k", registry.Decide("mod+k", null, typing: false, panelOpen: true).Run?.Keys);
    }

    [Fact]
    public void TheFirstKeyOfAChordIsHeldAndTheSecondRunsIt()
    {
        var registry = Registry(Key("g e"), Key("g s"));

        var first = registry.Decide("g", null, false, false);
        Assert.Null(first.Run);
        Assert.Equal("g", first.Pending);

        var second = registry.Decide("e", first.Pending, false, false);
        Assert.Equal("g e", second.Run?.Keys);
        Assert.Null(second.Pending);
    }

    [Fact]
    public void AChordThatGoesNowhereFallsThroughToTheSingleKey()
    {
        // `g` then `j` still moves the list rather than being swallowed.
        var registry = Registry(Key("g e"), Key("j"));

        var outcome = registry.Decide("j", pending: "g", typing: false, panelOpen: false);

        Assert.Equal("j", outcome.Run?.Keys);
    }

    [Fact]
    public void ThePendingKeyItselfStillRunsWhenItIsAlsoAKey()
    {
        // A key that starts a chord is held rather than run, so `g` alone does nothing until the
        // second key or the timeout; that is the price of chords and the test pins it.
        var registry = Registry(Key("g e"), Key("g"));

        var outcome = registry.Decide("g", null, false, false);

        Assert.Null(outcome.Run);
        Assert.Equal("g", outcome.Pending);
    }

    [Fact]
    public void TheLastSetToClaimAKeyWins()
    {
        var registry = new ShortcutRegistry();
        var windowRan = false;
        var pageRan = false;
        registry.Set("window", [new Shortcut("escape", "Close", ShortcutGroup.General, () => windowRan = true)]);
        registry.Set("page", [new Shortcut("escape", "Drop the selection", ShortcutGroup.Lists, () => pageRan = true)]);

        registry.Decide("escape", null, false, false).Run!.Run();

        Assert.True(pageRan);
        Assert.False(windowRan);
    }

    [Fact]
    public void ReplacingASetTakesItsOldKeysAway()
    {
        var registry = new ShortcutRegistry();
        registry.Set("page", [Key("j"), Key("k")]);
        registry.Set("page", [Key("f")]);

        Assert.Equal(ShortcutOutcome.Nothing, registry.Decide("j", null, false, false));
        Assert.Equal("f", registry.Decide("f", null, false, false).Run?.Keys);

        registry.Set("page", []);
        Assert.Equal(ShortcutOutcome.Nothing, registry.Decide("f", null, false, false));
    }

    [Fact]
    public void TheSheetListsOneRowPerKeyWithoutTheHiddenOnes()
    {
        var registry = new ShortcutRegistry();
        registry.Set("window", [Key("mod+k", "Search"), Key("g e", "Events", ShortcutGroup.GoTo)]);
        registry.Set("page", [Key("j", "Next row", ShortcutGroup.Lists), Key("arrowdown", "Next row", ShortcutGroup.Lists, hidden: true)]);
        registry.Set("later", [Key("mod+k", "Search and commands")]);

        var listed = registry.Listed;

        Assert.Equal(["mod+k", "g e", "j"], listed.Select(s => s.Keys));
        Assert.Equal("Search and commands", listed.Single(s => s.Keys == "mod+k").Label);
        Assert.Equal([ShortcutGroup.General, ShortcutGroup.GoTo, ShortcutGroup.Lists], listed.Select(s => s.Group));
    }
}
