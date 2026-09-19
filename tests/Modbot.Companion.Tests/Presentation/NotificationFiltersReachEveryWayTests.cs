using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;
using Modbot.Companion.Voice;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// One filter, three ways. Each way asks the same list at the last moment before the moderator is
/// told, so a tick taken off stops the card, the bleep and the line, and a tick put on starts
/// exactly the one it names.
/// </summary>
public class NotificationFiltersReachEveryWayTests
{
    private const string Moderator = "usr_me";

    private readonly FakeClock _clock = new();
    private readonly FakePlayer _player = new();
    private readonly FakeSynthesizer _synthesizer = new();

    private NotificationFilters _filters = NotificationFilters.Default;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PopUps Cards() => new(_clock)
    {
        Dwell = TimeSpan.FromSeconds(30),
        Wanted = kind => _filters.PopUpShows(kind),
    };

    private NotificationSound Sound() => new(
        _player,
        new FakeDevices(),
        () => NotificationSettings.Default,
        () => null,
        new BleepRule(_clock),
        null,
        () => _filters);

    private VoiceAnnouncer Voice() => new(
        new AnnouncementQueue(_clock),
        new PlainSpokenName(),
        () => new VoiceSettings(On: true),
        () => Moderator,
        () => _synthesizer,
        _player,
        new FakeDevices(),
        null,
        () => _filters);

    private static InstanceLocation Instance()
    {
        Assert.True(InstanceLocation.TryParse("wrld_4b34:39911~group(grp_cats)~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private ObservedPresence Joined(string id, string? name)
        => new(PresenceKind.Joined, _clock.UtcNow.DateTime, id, name, Instance());

    private static PopUp Card(string id = "a1") => new(id, "Cat Lounge", "Rin", null, PopUpTone.Plain);

    [Fact]
    public async Task AKindTurnedOffEverywhereReachesNoneOfTheThree()
    {
        _filters = NotificationFilters.Nothing;

        var cards = Cards();
        cards.Show(Card(), NotificationKind.Joined);
        Assert.Empty(cards.Current());

        Assert.False(await Sound().PlayAsync(NotificationKind.Joined, "usr_rin", Ct));
        Assert.Empty(_player.Played);

        var voice = Voice();
        voice.Offer([Joined("usr_rin", "Rin")]);
        Assert.Equal(0, voice.Waiting);
    }

    [Fact]
    public async Task AKindTurnedOnEverywhereReachesAllThree()
    {
        _filters = NotificationFilters.Everything;

        var cards = Cards();
        cards.Show(Card(), NotificationKind.Joined);
        Assert.Single(cards.Current());

        Assert.True(await Sound().PlayAsync(NotificationKind.Joined, "usr_rin", Ct));

        var voice = Voice();
        voice.Offer([Joined("usr_rin", "Rin")]);
        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.Equal(["Rin joined your world"], _synthesizer.Spoken);
    }

    [Fact]
    public async Task OneWayIsTurnedOnWithoutTurningOnTheOthers()
    {
        // The whole reason each way has its own ticks: a moderator can have every arrival spoken
        // and only flagged arrivals bleeped.
        _filters = NotificationFilters.Nothing.With(NotificationWay.Voice, NotificationKind.Joined, true);

        var cards = Cards();
        cards.Show(Card(), NotificationKind.Joined);
        Assert.Empty(cards.Current());

        Assert.False(await Sound().PlayAsync(NotificationKind.Joined, "usr_rin", Ct));

        var voice = Voice();
        voice.Offer([Joined("usr_rin", "Rin")]);
        Assert.True(await voice.SpeakNextAsync(Ct));
    }

    [Fact]
    public async Task TheDefaultsLetAFlaggedJoinThroughEveryWay()
    {
        var cards = Cards();
        cards.Show(Card("alert:1"), NotificationKind.FlaggedJoin);
        Assert.Single(cards.Current());

        Assert.True(await Sound().PlayAsync(NotificationKind.FlaggedJoin, "Rin", Ct));

        var voice = Voice();
        voice.FlaggedJoin("Rin");
        Assert.True(await voice.SpeakNextAsync(Ct));
        Assert.Equal(["Flagged user Rin joined"], _synthesizer.Spoken);
    }

    [Fact]
    public async Task TheDefaultsKeepEveryWayQuietAboutAnAvatarChange()
    {
        var cards = Cards();
        cards.Show(Card(), NotificationKind.ChangedAvatar);
        Assert.Empty(cards.Current());

        Assert.False(await Sound().PlayAsync(NotificationKind.ChangedAvatar, "usr_rin", Ct));

        var voice = Voice();
        voice.Offer([new ObservedPresence(
            PresenceKind.AvatarChanged, _clock.UtcNow.DateTime, "usr_rin", "Rin", Instance(), "Tall Cat")]);
        Assert.Equal(0, voice.Waiting);
    }

    [Fact]
    public async Task TheTestButtonGoesThroughWithEveryTickTakenOff()
    {
        _filters = NotificationFilters.Nothing;

        Assert.True(await Sound().PlayAsync(NotificationKind.Test, cancellationToken: Ct));
    }

    [Fact]
    public void ACardWithNoKindIsNeverFiltered()
    {
        // The overlay preview and the sample screens. Nothing about a preview is a notification.
        _filters = NotificationFilters.Nothing;

        var cards = Cards();
        cards.Show(Card());

        Assert.Single(cards.Current());
    }

    [Fact]
    public async Task WithNoFiltersHandedInTheVoicesOwnThreeSwitchesStillDecide()
    {
        // A client older than this card, and the voice's own tests: nothing changes for them.
        var voice = new VoiceAnnouncer(
            new AnnouncementQueue(_clock),
            new PlainSpokenName(),
            () => new VoiceSettings(On: true, Joins: false),
            () => Moderator,
            () => _synthesizer,
            _player,
            new FakeDevices());

        voice.Offer([Joined("usr_rin", "Rin")]);

        Assert.False(await voice.SpeakNextAsync(Ct));
        Assert.Empty(_synthesizer.Spoken);
    }

    private sealed class FakePlayer : IVoicePlayer
    {
        public List<VoiceClip> Played { get; } = [];

        public Task PlayAsync(VoiceClip clip, OutputDevice? device, CancellationToken cancellationToken)
        {
            Played.Add(clip);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSynthesizer : IVoiceSynthesizer
    {
        public List<string> Spoken { get; } = [];

        public VoiceClip Speak(string text, string? voiceName)
        {
            Spoken.Add(text);
            return new VoiceClip([0.5f, -0.5f], 24_000);
        }

        public void Dispose() { }
    }

    private sealed class FakeDevices : IOutputDevices
    {
        public IReadOnlyList<OutputDevice> List() => [new("{spk}", "Speakers")];

        public OutputDevice? Default() => new("{spk}", "Speakers");
    }
}
