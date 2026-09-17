using Modbot.Companion.Instances;
using Modbot.Companion.Voice;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Voice;

/// <summary>
/// The voice end to end with the engine and the speakers faked: what is said, what is kept
/// quiet about, and that two lines never play over each other.
/// </summary>
public class VoiceAnnouncerTests
{
    private const string Moderator = "usr_me";

    private sealed class FakeSynthesizer : IVoiceSynthesizer
    {
        public List<string> Spoken { get; } = [];

        public VoiceClip Speak(string text)
        {
            Spoken.Add(text);
            return new VoiceClip([0.5f, -0.5f, 1f], 22_050);
        }

        public void Dispose() { }
    }

    private sealed class FakePlayer : IVoicePlayer
    {
        public List<(VoiceClip Clip, OutputDevice? Device)> Played { get; } = [];

        /// <summary>When set, playback does not finish until it is completed.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>Completed the first time playback starts, so a test can wait for that moment.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InFlight { get; private set; }

        public int MostInFlight { get; private set; }

        public async Task PlayAsync(VoiceClip clip, OutputDevice? device, CancellationToken cancellationToken)
        {
            InFlight++;
            MostInFlight = Math.Max(MostInFlight, InFlight);
            try
            {
                Played.Add((clip, device));
                Started.TrySetResult();
                if (Hold is { } hold)
                    await hold.Task;
            }
            finally
            {
                InFlight--;
            }
        }
    }

    private sealed class FakeDevices(params OutputDevice[] devices) : IOutputDevices
    {
        public IReadOnlyList<OutputDevice> List() => devices;

        public OutputDevice? Default() => devices.FirstOrDefault();
    }

    private static readonly OutputDevice Speakers = new("{spk}", "Speakers");
    private static readonly OutputDevice Headset = new("{hmd}", "Headset");

    private readonly FakeClock _clock = new();
    private readonly FakeSynthesizer _synthesizer = new();
    private readonly FakePlayer _player = new();
    private readonly List<string> _log = [];

    private VoiceSettings _settings = new(On: true);
    private bool _engineReady = true;

    private VoiceAnnouncer Announcer(params OutputDevice[] devices) => new(
        new AnnouncementQueue(_clock),
        new PlainSpokenName(),
        () => _settings,
        () => Moderator,
        () => _engineReady ? _synthesizer : null,
        _player,
        new FakeDevices(devices.Length == 0 ? [Speakers] : devices),
        _log.Add);

    private static InstanceLocation Instance()
    {
        Assert.True(InstanceLocation.TryParse("wrld_4b34:39911~group(grp_cats)~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private ObservedPresence Joined(string id, string? name) => new(PresenceKind.Joined, _clock.UtcNow.DateTime, id, name, Instance());

    private ObservedPresence Left(string id, string? name) => new(PresenceKind.Left, _clock.UtcNow.DateTime, id, name, Instance());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaysTheEventsScreensSentenceForAJoin()
    {
        var voice = Announcer();
        voice.Offer([Joined("usr_rin", "Rin")]);

        Assert.True(await voice.SpeakNextAsync(Ct));

        Assert.Equal(["Rin joined your world"], _synthesizer.Spoken);
        Assert.Single(_player.Played);
    }

    [Fact]
    public async Task NeverTheModeratorThemselves()
    {
        // The moderator's own arrival is what the tracker marks as an exact join; they know.
        var voice = Announcer();
        voice.Offer([Joined(Moderator, "Me"), Left(Moderator, "Me")]);

        Assert.False(await voice.SpeakNextAsync(Ct));
        Assert.Empty(_synthesizer.Spoken);
    }

    [Fact]
    public async Task OffByDefaultMeansSilence()
    {
        _settings = VoiceSettings.Default;
        var voice = Announcer();

        voice.Offer([Joined("usr_rin", "Rin")]);
        voice.FlaggedJoin("Rin");
        voice.Problem("cats rejected this device.");

        Assert.False(await voice.SpeakNextAsync(Ct));
        Assert.Equal(0, voice.Waiting);
    }

    [Fact]
    public async Task EachKindHasItsOwnSwitch()
    {
        _settings = new VoiceSettings(On: true, Joins: false, Leaves: true, FlaggedJoins: false);
        var voice = Announcer();

        voice.Offer([Joined("usr_rin", "Rin"), Left("usr_kai", "Kai")]);
        voice.FlaggedJoin("Ash");

        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.Equal(["Kai left your world"], _synthesizer.Spoken);
        Assert.False(await voice.SpeakNextAsync(Ct));
    }

    [Fact]
    public async Task SilentWhilePaused()
    {
        var voice = Announcer();
        voice.Offer([Joined("usr_rin", "Rin")]);

        voice.Paused = true;
        voice.Offer([Joined("usr_kai", "Kai")]);

        // What was queued before the pause is dropped, not saved up.
        Assert.False(await voice.SpeakNextAsync(Ct));
        Assert.Equal(0, voice.Waiting);
        Assert.Empty(_synthesizer.Spoken);
    }

    [Fact]
    public async Task NeverSpeaksOverItself()
    {
        _player.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = Announcer();

        voice.Offer([Joined("usr_rin", "Rin")]);
        var first = voice.SpeakNextAsync(Ct);
        Assert.True(voice.IsSpeaking);
        await _player.Started.Task;

        voice.Offer([Joined("usr_kai", "Kai")]);
        Assert.False(await voice.SpeakNextAsync(Ct));
        Assert.Equal(1, voice.Waiting);
        Assert.Single(_player.Played);

        _player.Hold.SetResult();
        Assert.True(await first);
        Assert.False(voice.IsSpeaking);

        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.Equal(["Rin joined your world", "Kai joined your world"], _synthesizer.Spoken);
        Assert.Equal(1, _player.MostInFlight);
    }

    [Fact]
    public async Task ABurstWhileSpeakingBecomesOneSentence()
    {
        _player.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = Announcer();

        voice.Offer([Joined("usr_rin", "Rin")]);
        var first = voice.SpeakNextAsync(Ct);
        await _player.Started.Task;

        voice.Offer([Joined("usr_a", "Kai"), Joined("usr_b", "Mio"), Joined("usr_c", "Sol")]);

        _player.Hold.SetResult();
        await first;

        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.Equal("three people joined your world", _synthesizer.Spoken[^1]);
    }

    [Fact]
    public async Task FlaggedJoinsAndProblemsComeFirst()
    {
        var voice = Announcer();
        voice.Offer([Joined("usr_rin", "Rin")]);
        voice.FlaggedJoin("Ash");
        voice.Problem("cats rejected this device. Modbot has stopped reporting to it.");

        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.True(await voice.SpeakNextAsync(Ct));

        Assert.Equal(
            [
                "cats rejected this device. Modbot has stopped reporting to it.",
                "Flagged user Ash joined",
                "Rin joined your world",
            ],
            _synthesizer.Spoken);
    }

    [Fact]
    public async Task TestSpeaksEvenWhenOff()
    {
        _settings = VoiceSettings.Default;
        var voice = Announcer();

        voice.Test();

        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.Equal([VoiceAnnouncer.TestLine], _synthesizer.Spoken);
    }

    [Fact]
    public async Task VolumeScalesTheSound()
    {
        _settings = new VoiceSettings(On: true, Volume: 50);
        var voice = Announcer();
        voice.Offer([Joined("usr_rin", "Rin")]);

        Assert.True(await voice.SpeakNextAsync(Ct));

        Assert.Equal([0.25f, -0.25f, 0.5f], _player.Played[0].Clip.Samples);
    }

    [Fact]
    public async Task AChosenDeviceIsUsedWhenPresent()
    {
        _settings = new VoiceSettings(On: true, OutputDeviceId: Headset.Id);
        var voice = Announcer(Speakers, Headset);
        voice.Offer([Joined("usr_rin", "Rin")]);

        Assert.True(await voice.SpeakNextAsync(Ct));

        Assert.Equal(Headset, _player.Played[0].Device);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task AChosenDeviceThatIsGoneFallsBackToTheDefaultAndSaysSoOnce()
    {
        _settings = new VoiceSettings(On: true, OutputDeviceId: Headset.Id);
        var voice = Announcer(Speakers);

        voice.Offer([Joined("usr_rin", "Rin")]);
        Assert.True(await voice.SpeakNextAsync(Ct));
        voice.Offer([Joined("usr_kai", "Kai")]);
        Assert.True(await voice.SpeakNextAsync(Ct));

        // Null means "the system default, whatever it is right now" -- which is what follows
        // the operating system when it moves the default.
        Assert.All(_player.Played, p => Assert.Null(p.Device));
        var line = Assert.Single(_log);
        Assert.Contains("Speakers", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoChoiceMeansTheSystemDefault()
    {
        var voice = Announcer(Speakers, Headset);
        voice.Offer([Joined("usr_rin", "Rin")]);

        Assert.True(await voice.SpeakNextAsync(Ct));

        Assert.Null(_player.Played[0].Device);
    }

    [Fact]
    public async Task LinesWaitForTheEngineAndThenAgeOut()
    {
        _engineReady = false;
        var voice = Announcer();
        voice.Offer([Joined("usr_rin", "Rin")]);

        Assert.False(await voice.SpeakNextAsync(Ct));
        Assert.Equal(1, voice.Waiting);

        _clock.Advance(TimeSpan.FromSeconds(30));
        _engineReady = true;

        Assert.False(await voice.SpeakNextAsync(Ct));
        Assert.Empty(_synthesizer.Spoken);
    }

    [Fact]
    public async Task ANameWithNothingSpeakableIsSomeone()
    {
        var voice = Announcer();
        voice.Offer([Joined("usr_x", "​́"), Left("usr_y", null)]);

        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.True(await voice.SpeakNextAsync(Ct));

        Assert.Equal(["someone joined your world", "someone left your world"], _synthesizer.Spoken);
    }

    [Fact]
    public async Task APlayerThatFailsIsLoggedAndTheVoiceCarriesOn()
    {
        var failing = new ThrowingPlayer();
        var voice = new VoiceAnnouncer(
            new AnnouncementQueue(_clock), new PlainSpokenName(), () => _settings, () => Moderator,
            () => _synthesizer, failing, new FakeDevices(Speakers), _log.Add);

        voice.Offer([Joined("usr_rin", "Rin")]);
        Assert.True(await voice.SpeakNextAsync(Ct));

        Assert.False(voice.IsSpeaking);
        Assert.Contains(_log, l => l.Contains("could not be spoken", StringComparison.Ordinal));

        voice.Offer([Joined("usr_kai", "Kai")]);
        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.Equal(2, _synthesizer.Spoken.Count);
    }

    private sealed class ThrowingPlayer : IVoicePlayer
    {
        public Task PlayAsync(VoiceClip clip, OutputDevice? device, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no sound today");
    }
}
