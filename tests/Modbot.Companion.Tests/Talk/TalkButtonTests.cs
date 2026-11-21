using Modbot.Companion.Presentation;
using Modbot.Companion.Talk;

namespace Modbot.Companion.Tests.Talk;

/// <summary>
/// The buttons (swap, Discord Mute, Discord Deafen, VRChat Mute): which side the microphone is on, what the
/// VR panel's row says about it, and what a press does, over a made-up VRChat and keyboard.
/// </summary>
public class TalkButtonTests
{
    private const int MuteCode = 0x7C;
    private const int DeafenCode = 0x7D;

    private static readonly TalkSettings On = new(true, "f13", "f14");

    private sealed class FakeMic(bool? muted) : IVRChatMic
    {
        public bool? Muted { get; private set; } = muted;

        public string? Problem => null;

        public List<string> Log { get; } = [];

        public Task<MicTap> TapAsync(CancellationToken cancellationToken)
        {
            Log.Add("tap");
            Muted = Muted is { } current ? !current : true;
            return Task.FromResult(new MicTap(true, Muted));
        }
    }

    private sealed class FakeKeys(List<string> log) : IKeyPresser
    {
        public bool Press(ShortcutKeys keys)
        {
            log.Add(keys.Key switch { MuteCode => "press mute", DeafenCode => "press deafen", _ => $"press {keys.Key:X}" });
            return true;
        }

        public void ReleaseModifiers(int modifiers) => log.Add($"release {modifiers}");
    }

    private static (TalkController Talk, FakeMic Mic, List<string> Log) Make(bool? vrchatMuted, TalkSettings? settings = null)
    {
        var mic = new FakeMic(vrchatMuted);
        var keys = new FakeKeys(mic.Log);
        var chosen = settings ?? On;

        return (new TalkController(mic, keys, () => chosen, TimeSpan.Zero), mic, mic.Log);
    }

    [Theory]
    [InlineData(true, false, false, TalkSide.Discord)]
    [InlineData(false, true, false, TalkSide.VRChat)]
    [InlineData(false, false, true, TalkSide.VRChat)]
    [InlineData(false, false, false, TalkSide.Neither)]
    [InlineData(true, true, false, TalkSide.Neither)]
    [InlineData(true, false, true, TalkSide.Neither)]
    public void TheSideIsWhereTheMicrophoneIsOpen(bool vrchatMuted, bool discordMuted, bool discordDeafened, TalkSide side)
    {
        Assert.Equal(side, new TalkState(true, vrchatMuted, discordMuted, discordDeafened, null).Side);
    }

    [Fact]
    public void NeitherSideWhileVRChatHasNotSaidWhatTheMicrophoneIs()
    {
        Assert.Equal(TalkSide.Neither, new TalkState(true, null, true, false, null).Side);
    }

    [Fact]
    public async Task SwapFromVRChatTalksInDiscord()
    {
        var (talk, mic, log) = Make(vrchatMuted: false);

        var result = await talk.SwapAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(["tap"], log);
        Assert.True(mic.Muted);
        Assert.Equal(TalkSide.Discord, talk.State.Side);
    }

    [Fact]
    public async Task SwapFromDiscordTalksInVRChat()
    {
        var (talk, mic, log) = Make(vrchatMuted: true);
        Assert.Equal(TalkSide.Discord, talk.State.Side);

        var result = await talk.SwapAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(["press mute", "tap"], log);
        Assert.False(mic.Muted);
        Assert.Equal(TalkSide.VRChat, talk.State.Side);
    }

    [Fact]
    public async Task TwoSwapsGoThereAndBack()
    {
        var (talk, _, _) = Make(vrchatMuted: true);

        await talk.SwapAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TalkSide.VRChat, talk.State.Side);

        await talk.SwapAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TalkSide.Discord, talk.State.Side);
    }

    [Fact]
    public async Task SwapFromNeitherSideTalksInDiscord()
    {
        // Both open: nobody is "in" one place, so the press goes to Discord.
        var (talk, mic, _) = Make(vrchatMuted: false);
        Assert.Equal(TalkSide.Neither, talk.State.Side);

        await talk.SwapAsync(TestContext.Current.CancellationToken);

        Assert.True(mic.Muted);
        Assert.Equal(TalkSide.Discord, talk.State.Side);
    }

    [Fact]
    public async Task SwapWhileOffDoesNothing()
    {
        var (talk, _, log) = Make(vrchatMuted: false, TalkSettings.Default);

        var result = await talk.SwapAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(TalkController.Off, result.Problem);
        Assert.Empty(log);
    }

    [Fact]
    public async Task EachButtonDoesItsOwnThing()
    {
        var (talk, _, log) = Make(vrchatMuted: true);

        await talk.PressButtonAsync(TalkButton.DiscordMute, TestContext.Current.CancellationToken);
        await talk.PressButtonAsync(TalkButton.DiscordDeafen, TestContext.Current.CancellationToken);

        Assert.Equal(["press mute", "press deafen"], log);
        Assert.True(talk.State.DiscordDeafened);

        await talk.PressButtonAsync(TalkButton.Swap, TestContext.Current.CancellationToken);

        Assert.Equal(TalkSide.Discord, talk.State.Side);
    }

    [Fact]
    public async Task VRChatMuteTapsVRChatsMicrophoneAndNeverTouchesDiscord()
    {
        var (talk, mic, log) = Make(vrchatMuted: false);

        var muted = await talk.PressButtonAsync(TalkButton.VRChatMute, TestContext.Current.CancellationToken);

        Assert.True(muted.Ok);
        Assert.Equal(["tap"], log);
        Assert.True(mic.Muted);
        Assert.True(talk.State.VRChatMuted);

        var open = await talk.PressButtonAsync(TalkButton.VRChatMute, TestContext.Current.CancellationToken);

        Assert.True(open.Ok);
        Assert.Equal(["tap", "tap"], log);
        Assert.False(mic.Muted);
        Assert.False(talk.State.DiscordMuted);
        Assert.False(talk.State.DiscordDeafened);
    }

    [Fact]
    public async Task MutingVRChatOnVRChatsSideLeavesItOnVRChatsSide()
    {
        var (talk, _, _) = Make(vrchatMuted: true);
        await talk.SwapAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TalkSide.VRChat, talk.State.Side);

        await talk.PressButtonAsync(TalkButton.VRChatMute, TestContext.Current.CancellationToken);

        Assert.True(talk.State.VRChatMuted);
        Assert.Equal(TalkSide.VRChat, talk.State.Side);
        Assert.True(TalkRowRule.For(talk.State).VRChatMuted);

        await talk.PressButtonAsync(TalkButton.VRChatMute, TestContext.Current.CancellationToken);

        Assert.False(talk.State.VRChatMuted);
        Assert.Equal(TalkSide.VRChat, talk.State.Side);
    }

    [Fact]
    public async Task AnyOtherPressAfterMutingVRChatEndsThatSide()
    {
        var (talk, _, _) = Make(vrchatMuted: true);
        await talk.SwapAsync(TestContext.Current.CancellationToken);
        await talk.PressButtonAsync(TalkButton.VRChatMute, TestContext.Current.CancellationToken);

        await talk.PressButtonAsync(TalkButton.Swap, TestContext.Current.CancellationToken);

        Assert.Equal(TalkSide.Discord, talk.State.Side);
    }

    [Fact]
    public async Task VRChatMuteWhileVRChatSaysNothingSaysWhy()
    {
        var mic = new SilentMic();
        var talk = new TalkController(mic, new FakeKeys([]), () => On, TimeSpan.Zero);

        var result = await talk.PressButtonAsync(TalkButton.VRChatMute, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(TalkController.DidntAnswer, result.Problem);
        Assert.Equal(1, mic.Taps);
    }

    private sealed class SilentMic : IVRChatMic
    {
        public int Taps { get; private set; }

        public bool? Muted => null;

        public string? Problem => null;

        public Task<MicTap> TapAsync(CancellationToken cancellationToken)
        {
            Taps++;
            return Task.FromResult(new MicTap(true, null));
        }
    }

    [Fact]
    public async Task AHotkeyLetsGoOfItsOwnModifiersBeforeAnythingIsPressed()
    {
        var (talk, _, log) = Make(vrchatMuted: true);

        await talk.PressHotkeyAsync(TalkButton.DiscordMute, ShortcutKeys.Ctrl | ShortcutKeys.Shift, TestContext.Current.CancellationToken);

        Assert.Equal([$"release {ShortcutKeys.Ctrl | ShortcutKeys.Shift}", "press mute"], log);
    }

    [Fact]
    public async Task AHotkeyWithNoModifierLetsGoOfNothing()
    {
        var (talk, _, log) = Make(vrchatMuted: true);

        await talk.PressHotkeyAsync(TalkButton.DiscordMute, 0, TestContext.Current.CancellationToken);

        Assert.Equal(["press mute"], log);
    }

    [Fact]
    public async Task APanelButtonLetsGoOfNothing()
    {
        var (talk, _, log) = Make(vrchatMuted: true);

        await talk.PressButtonAsync(TalkButton.DiscordMute, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(log, entry => entry.StartsWith("release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHotkeyWhileItIsOffLetsGoOfNothing()
    {
        var (talk, _, log) = Make(vrchatMuted: true, TalkSettings.Default);

        await talk.PressHotkeyAsync(TalkButton.DiscordMute, ShortcutKeys.Ctrl, TestContext.Current.CancellationToken);

        Assert.Empty(log);
    }

    [Fact]
    public void NoRowWhileItIsOffOrThereIsNoController()
    {
        Assert.False(TalkRowRule.For(null).IsVisible);
        Assert.False(TalkRowRule.For(TalkState.None).IsVisible);
        Assert.False(default(TalkRow).IsVisible);
    }

    [Fact]
    public void TheRowSaysWhereThingsStand()
    {
        var row = TalkRowRule.For(new TalkState(true, true, false, false, "Windows would not send the key."));

        Assert.True(row.IsVisible);
        Assert.Equal(TalkSide.Discord, row.Side);
        Assert.Equal("Windows would not send the key.", row.Problem);
    }

    [Theory]
    [InlineData(TalkSide.Discord, "Talking in Discord")]
    [InlineData(TalkSide.VRChat, "Talking in VRChat")]
    [InlineData(TalkSide.Neither, "Talk in Discord")]
    public void TheSwapButtonNamesTheSide(TalkSide side, string caption)
    {
        Assert.Equal(caption, new TalkRow(true, side, false, false, null).SwapCaption);
    }

    [Theory]
    [InlineData(false, "Discord Mute")]
    [InlineData(true, "Discord Muted")]
    public void TheMuteButtonSaysWhetherItIsOn(bool muted, string caption)
    {
        Assert.Equal(caption, new TalkRow(true, TalkSide.Neither, muted, false, null).MuteCaption);
    }

    [Theory]
    [InlineData(false, "VRChat Mute")]
    [InlineData(true, "VRChat Muted")]
    public void TheVRChatMuteButtonSaysWhetherItIsOn(bool muted, string caption)
    {
        Assert.Equal(caption, new TalkRow(true, TalkSide.VRChat, false, false, null, VRChatMuted: muted).VRChatMuteCaption);
    }

    [Theory]
    [InlineData(false, "Discord Deafen")]
    [InlineData(true, "Discord Deafened")]
    public void TheDeafenButtonSaysWhetherItIsOn(bool deafened, string caption)
    {
        Assert.Equal(caption, new TalkRow(true, TalkSide.Neither, false, deafened, null).DeafenCaption);
    }
}
