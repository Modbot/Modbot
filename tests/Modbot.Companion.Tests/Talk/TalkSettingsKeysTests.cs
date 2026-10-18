using Modbot.Companion.Talk;

namespace Modbot.Companion.Tests.Talk;

/// <summary>
/// The one-key-one-slot rule: a key Modbot presses for Discord must never also be one of Modbot's
/// own hotkeys, or a hotkey that is still held when Modbot presses it.
/// </summary>
public class TalkSettingsKeysTests
{
    [Fact]
    public void ADiscordKeyTakesTheSameMainKeyOutOfAHotkeyWhateverIsHeldWithIt()
    {
        var set = TalkSettings.Default
            .WithKey(TalkKeySlot.SwapHotkey, "mod+f13")
            .WithKey(TalkKeySlot.DiscordMute, "f13");

        Assert.Equal("f13", set.DiscordMuteKey);
        Assert.Null(set.SwapHotkey);
    }

    [Fact]
    public void AHotkeyTakesTheSameMainKeyOutOfADiscordKeyWhateverIsHeldWithIt()
    {
        var set = TalkSettings.Default
            .WithKey(TalkKeySlot.DiscordDeafen, "f14")
            .WithKey(TalkKeySlot.MuteHotkey, "alt+f14");

        Assert.Equal("alt+f14", set.MuteHotkey);
        Assert.Null(set.DiscordDeafenKey);
    }

    [Fact]
    public void ADifferentMainKeyLeavesBothAlone()
    {
        var set = TalkSettings.Default
            .WithKey(TalkKeySlot.DiscordMute, "f13")
            .WithKey(TalkKeySlot.SwapHotkey, "mod+f14");

        Assert.Equal("f13", set.DiscordMuteKey);
        Assert.Equal("mod+f14", set.SwapHotkey);
    }

    [Fact]
    public void TwoDiscordKeysOnlyClashWhenTheyAreTheSameKeys()
    {
        var set = TalkSettings.Default
            .WithKey(TalkKeySlot.DiscordMute, "mod+m")
            .WithKey(TalkKeySlot.DiscordDeafen, "mod+shift+m");

        Assert.Equal("mod+m", set.DiscordMuteKey);
        Assert.Equal("mod+shift+m", set.DiscordDeafenKey);

        var same = set.WithKey(TalkKeySlot.DiscordDeafen, "mod+m");

        Assert.Null(same.DiscordMuteKey);
        Assert.Equal("mod+m", same.DiscordDeafenKey);
    }

    [Fact]
    public void ACleanedFileKeepsTheDiscordKeyAndDropsTheHotkeyOnTheSameMainKey()
    {
        var cleaned = new TalkSettings(true, "f13", null, "mod+f13").Cleaned();

        Assert.Equal("f13", cleaned.DiscordMuteKey);
        Assert.Null(cleaned.SwapHotkey);
    }

    [Fact]
    public void ACleanedFileDropsAHotkeyThatIsABareTypingKeyButKeepsItAsADiscordKey()
    {
        var cleaned = new TalkSettings(true, "m", "n", "k", "shift+j", "f9").Cleaned();

        Assert.Equal("m", cleaned.DiscordMuteKey);
        Assert.Equal("n", cleaned.DiscordDeafenKey);
        Assert.Null(cleaned.SwapHotkey);
        Assert.Null(cleaned.MuteHotkey);
        Assert.Equal("f9", cleaned.DeafenHotkey);
    }
}
