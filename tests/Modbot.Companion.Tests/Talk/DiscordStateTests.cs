using Modbot.Companion.Talk;

namespace Modbot.Companion.Tests.Talk;

/// <summary>
/// What Modbot remembers about Discord after its own key presses (design spec 2026-10-17, §3.1).
/// </summary>
public class DiscordStateTests
{
    [Fact]
    public void DiscordStartsOpen()
    {
        Assert.False(DiscordState.Start.Muted);
        Assert.False(DiscordState.Start.Deafened);
        Assert.True(DiscordState.Start.MicrophoneOpen);
    }

    [Fact]
    public void ToggleMuteFlipsMuted()
    {
        var muted = DiscordState.Start.AfterMutePress();

        Assert.True(muted.Muted);
        Assert.False(muted.MicrophoneOpen);
        Assert.False(muted.AfterMutePress().Muted);
    }

    [Fact]
    public void GoingDeafMutesAndRemembersWhatMutedWas()
    {
        var deaf = DiscordState.Start.AfterDeafenPress();

        Assert.True(deaf.Deafened);
        Assert.True(deaf.Muted);
        Assert.False(deaf.MutedBeforeDeafen);
        Assert.False(deaf.MicrophoneOpen);
    }

    [Fact]
    public void ComingBackFromDeafOpensWhatWasOpen()
    {
        var back = DiscordState.Start.AfterDeafenPress().AfterDeafenPress();

        Assert.False(back.Deafened);
        Assert.False(back.Muted);
        Assert.True(back.MicrophoneOpen);
    }

    [Fact]
    public void ComingBackFromDeafKeepsWhatWasMuted()
    {
        var back = DiscordState.Start.AfterMutePress().AfterDeafenPress().AfterDeafenPress();

        Assert.False(back.Deafened);
        Assert.True(back.Muted);
    }

    [Fact]
    public void ToggleMuteWhileDeafOpensEverything()
    {
        var after = DiscordState.Start.AfterMutePress().AfterDeafenPress().AfterMutePress();

        Assert.Equal(DiscordState.Start, after);
    }
}
