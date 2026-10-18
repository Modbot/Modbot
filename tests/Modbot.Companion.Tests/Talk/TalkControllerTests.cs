using Modbot.Companion.Presentation;
using Modbot.Companion.Talk;

namespace Modbot.Companion.Tests.Talk;

/// <summary>
/// The order things happen in, and what happens when one of them fails, over a made-up VRChat and a
/// made-up keyboard (design spec 2026-10-17, §1 and §4).
/// </summary>
public class TalkControllerTests
{
    private const int MuteCode = 0x7C;
    private const int DeafenCode = 0x7D;

    private static readonly TalkSettings On = new(true, "f13", "f14");

    private sealed class Events
    {
        public List<string> Log { get; } = [];
    }

    private sealed class FakeMic(Events events, bool? muted) : IVRChatMic
    {
        /// <summary>What one tap does; the default flips the microphone and says so.</summary>
        public Func<FakeMic, MicTap>? OnTap { get; set; }

        public int Taps { get; private set; }

        public bool? Muted { get; set; } = muted;

        public string? Problem { get; set; }

        public Task<MicTap> TapAsync(CancellationToken cancellationToken)
        {
            Taps++;
            events.Log.Add("tap");

            return Task.FromResult(OnTap is { } custom ? custom(this) : Flip());
        }

        private MicTap Flip()
        {
            Muted = Muted is { } current ? !current : true;
            return new MicTap(true, Muted);
        }
    }

    private sealed class FakeKeys(Events events) : IKeyPresser
    {
        public bool Works { get; set; } = true;

        public bool Press(ShortcutKeys keys)
        {
            events.Log.Add(keys.Key switch
            {
                MuteCode => "press mute",
                DeafenCode => "press deafen",
                _ => $"press {keys.Key:X}",
            });

            return Works;
        }
    }

    private sealed record Rig(Events Events, FakeMic Mic, FakeKeys Keys, TalkController Talk, Box<TalkSettings> Settings);

    private sealed class Box<T>(T value)
    {
        public T Value { get; set; } = value;
    }

    private static Rig Make(bool? vrchatMuted, TalkSettings? settings = null)
    {
        var events = new Events();
        var mic = new FakeMic(events, vrchatMuted);
        var keys = new FakeKeys(events);
        var box = new Box<TalkSettings>(settings ?? On);

        return new Rig(events, mic, keys, new TalkController(mic, keys, () => box.Value, TimeSpan.Zero), box);
    }

    [Fact]
    public async Task TalkInVRChatClosesDiscordBeforeItOpensVRChat()
    {
        var rig = Make(vrchatMuted: true);

        var result = await rig.Talk.TalkInVRChatAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(["press mute", "tap"], rig.Events.Log);
        Assert.False(rig.Mic.Muted);
        Assert.True(rig.Talk.State.DiscordMuted);
    }

    [Fact]
    public async Task TalkInDiscordMutesVRChatBeforeItOpensDiscord()
    {
        var rig = Make(vrchatMuted: true);
        await rig.Talk.TalkInVRChatAsync(TestContext.Current.CancellationToken);
        rig.Events.Log.Clear();

        var result = await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(["tap", "press mute"], rig.Events.Log);
        Assert.True(rig.Mic.Muted);
        Assert.False(rig.Talk.State.DiscordMuted);
    }

    [Fact]
    public async Task TalkInDiscordDoesNothingToVRChatThatIsAlreadyMuted()
    {
        var rig = Make(vrchatMuted: true);

        var result = await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Empty(rig.Events.Log);
    }

    [Fact]
    public async Task TalkInDiscordDoesNotOpenDiscordWhenVRChatCouldNotBeMuted()
    {
        var rig = Make(vrchatMuted: false);
        await rig.Talk.TalkInVRChatAsync(TestContext.Current.CancellationToken);
        rig.Events.Log.Clear();
        rig.Mic.OnTap = _ => new MicTap(false, rig.Mic.Muted);

        var result = await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(TalkController.CouldNotSend, result.Problem);
        Assert.Equal(["tap"], rig.Events.Log);
        Assert.True(rig.Talk.State.DiscordMuted);
    }

    [Fact]
    public async Task TalkInDiscordStopsWhenVRChatNeverSaidWhatItsMicrophoneIs()
    {
        var rig = Make(vrchatMuted: null);
        rig.Mic.OnTap = _ => new MicTap(true, null);

        var result = await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(TalkController.DidntAnswer, result.Problem);

        // One tap and no more: with nothing to compare against, a second one could undo the first,
        // and Discord was not opened.
        Assert.Equal(["tap"], rig.Events.Log);
    }

    [Fact]
    public async Task APlainProblemFromTheServiceIsWhatThePersonIsTold()
    {
        var rig = Make(vrchatMuted: null);
        rig.Mic.Problem = "Modbot could not start listening for VRChat: boom";
        rig.Mic.OnTap = _ => new MicTap(true, null);

        var result = await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Modbot could not start listening for VRChat: boom", result.Problem);
    }

    [Fact]
    public async Task TalkInVRChatStillOpensVRChatWhenDiscordHasNoKey()
    {
        var rig = Make(vrchatMuted: true, On with { DiscordMuteKey = null });

        var result = await rig.Talk.TalkInVRChatAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(TalkController.NoMuteKey, result.Problem);
        Assert.Equal(["tap"], rig.Events.Log);
        Assert.False(rig.Mic.Muted);
    }

    [Fact]
    public async Task TalkInVRChatSaysSoWhenVRChatCouldNotBeOpened()
    {
        var rig = Make(vrchatMuted: true);
        rig.Mic.OnTap = _ => new MicTap(true, true);

        var result = await rig.Talk.TalkInVRChatAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(TalkController.DidntChange, result.Problem);
    }

    [Fact]
    public async Task ATapThatLandedOnTheWrongSideIsTriedOnceMore()
    {
        var rig = Make(vrchatMuted: false);
        var answers = new Queue<bool>([false, true]);
        rig.Mic.OnTap = mic =>
        {
            mic.Muted = answers.Dequeue();
            return new MicTap(true, mic.Muted);
        };

        var result = await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(2, rig.Mic.Taps);
        Assert.True(rig.Mic.Muted);
    }

    [Fact]
    public async Task NoMoreThanTwoTapsEverGoOut()
    {
        var rig = Make(vrchatMuted: false);
        rig.Mic.OnTap = mic => new MicTap(true, false);

        var result = await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(TalkController.DidntChange, result.Problem);
        Assert.Equal(2, rig.Mic.Taps);
        Assert.DoesNotContain(rig.Events.Log, e => e.StartsWith("press", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MutingFromNotKnownTapsAgainIfItLandedOpen()
    {
        var rig = Make(vrchatMuted: null);
        var answers = new Queue<bool>([false, true]);
        rig.Mic.OnTap = mic =>
        {
            mic.Muted = answers.Dequeue();
            return new MicTap(true, mic.Muted);
        };

        var result = await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(2, rig.Mic.Taps);
        Assert.True(rig.Mic.Muted);
    }

    [Fact]
    public async Task OpeningDiscordWhileDeafenedUndeafensFirst()
    {
        var rig = Make(vrchatMuted: false);
        await rig.Talk.ToggleDiscordDeafenAsync(TestContext.Current.CancellationToken);
        rig.Events.Log.Clear();

        var result = await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(["tap", "press deafen"], rig.Events.Log);
        Assert.False(rig.Talk.State.DiscordDeafened);
        Assert.False(rig.Talk.State.DiscordMuted);
    }

    [Fact]
    public async Task ClosingDiscordWhileDeafenedPressesNothing()
    {
        var rig = Make(vrchatMuted: true);
        await rig.Talk.ToggleDiscordDeafenAsync(TestContext.Current.CancellationToken);
        rig.Events.Log.Clear();

        var result = await rig.Talk.TalkInVRChatAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(["tap"], rig.Events.Log);
        Assert.True(rig.Talk.State.DiscordDeafened);
    }

    [Fact]
    public async Task EverythingDoesNothingWhileItIsOff()
    {
        var rig = Make(vrchatMuted: false, TalkSettings.Default);

        foreach (var result in new[]
                 {
                     await rig.Talk.TalkInDiscordAsync(TestContext.Current.CancellationToken),
                     await rig.Talk.TalkInVRChatAsync(TestContext.Current.CancellationToken),
                     await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken),
                     await rig.Talk.ToggleDiscordDeafenAsync(TestContext.Current.CancellationToken),
                 })
        {
            Assert.False(result.Ok);
            Assert.Equal(TalkController.Off, result.Problem);
        }

        Assert.Empty(rig.Events.Log);
        Assert.False(rig.Talk.State.On);
    }

    [Fact]
    public async Task ASwitchedOnSettingIsSeenAtTheNextAction()
    {
        var rig = Make(vrchatMuted: true, TalkSettings.Default);
        Assert.False((await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken)).Ok);

        rig.Settings.Value = On;

        Assert.True((await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken)).Ok);
        Assert.Equal(["press mute"], rig.Events.Log);
    }

    [Fact]
    public async Task ADiscordButtonWithoutItsKeyPressesNothingAndRemembersNothing()
    {
        var rig = Make(vrchatMuted: true, new TalkSettings(true, null, null));

        var mute = await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken);
        var deafen = await rig.Talk.ToggleDiscordDeafenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TalkController.NoMuteKey, mute.Problem);
        Assert.Equal(TalkController.NoDeafenKey, deafen.Problem);
        Assert.Empty(rig.Events.Log);
        Assert.False(rig.Talk.State.DiscordMuted);
        Assert.False(rig.Talk.State.DiscordDeafened);
    }

    [Fact]
    public async Task AKeyWindowsWouldNotSendIsNotRemembered()
    {
        var rig = Make(vrchatMuted: true);
        rig.Keys.Works = false;

        var result = await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TalkController.KeyNotSent, result.Problem);
        Assert.False(rig.Talk.State.DiscordMuted);
    }

    [Fact]
    public async Task EachDiscordButtonPressesItsOwnKeyAndRemembersIt()
    {
        var rig = Make(vrchatMuted: true);

        await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken);
        Assert.True(rig.Talk.State.DiscordMuted);
        Assert.False(rig.Talk.State.DiscordDeafened);

        await rig.Talk.ToggleDiscordDeafenAsync(TestContext.Current.CancellationToken);
        Assert.True(rig.Talk.State.DiscordDeafened);

        Assert.Equal(["press mute", "press deafen"], rig.Events.Log);
    }

    [Fact]
    public async Task TheReadoutSaysWhatVRChatSaidAndWhatWentWrong()
    {
        var rig = Make(vrchatMuted: true, new TalkSettings(true, null, null));

        await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken);

        var state = rig.Talk.State;
        Assert.True(state.On);
        Assert.True(state.VRChatMuted);
        Assert.Equal(TalkController.NoMuteKey, state.Problem);

        rig.Settings.Value = On;
        await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken);

        Assert.Null(rig.Talk.State.Problem);
    }

    [Fact]
    public async Task ChangedIsRaisedAfterEveryActionWhateverCameOfIt()
    {
        var rig = Make(vrchatMuted: true, TalkSettings.Default);
        var raised = 0;
        rig.Talk.Changed += () => raised++;

        await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken);
        rig.Settings.Value = On;
        await rig.Talk.ToggleDiscordMuteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, raised);
    }

    [Fact]
    public async Task AnActionStartedWhileAnotherRunsWaitsForIt()
    {
        var rig = Make(vrchatMuted: true);
        var release = new TaskCompletionSource();
        var slowMic = new SlowMic(rig.Events, release.Task);
        var talk = new TalkController(slowMic, rig.Keys, () => On, TimeSpan.Zero);

        var first = talk.TalkInVRChatAsync(TestContext.Current.CancellationToken);
        var second = talk.ToggleDiscordDeafenAsync(TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        release.SetResult();
        await Task.WhenAll(first, second);

        // The first action (close Discord, tap) ran whole before the second pressed anything.
        Assert.Equal(["press mute", "tap", "press deafen"], rig.Events.Log);
    }

    private sealed class SlowMic(Events events, Task gate) : IVRChatMic
    {
        public bool? Muted { get; private set; } = true;

        public string? Problem => null;

        public async Task<MicTap> TapAsync(CancellationToken cancellationToken)
        {
            events.Log.Add("tap");
            await gate;
            Muted = false;
            return new MicTap(true, false);
        }
    }
}
