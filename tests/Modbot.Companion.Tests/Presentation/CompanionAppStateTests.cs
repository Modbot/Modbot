using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Journal;
using Modbot.Companion.Pairing;
using Modbot.Companion.Pipeline;
using Modbot.Companion.Presentation;
using Modbot.Companion.Startup;
using Modbot.Companion.Time;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// What the window tells the moderator. Tested directly, because the wording is the substance of
/// the trust argument rather than decoration on top of it: "paused" and "broken" are different
/// facts, and a moderator who cannot tell them apart has no reason to keep the program installed.
/// </summary>
public class CompanionAppStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "modbot-appstate-tests", Guid.NewGuid().ToString("n"));

    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private SentJournal Journal() => new(Path.Combine(_directory, "sent.jsonl"), _clock);

    private CompanionAppState State() => new(_clock, Journal());

    private ServerConnection Connection(string serverId = "cats", IIngestTransport? transport = null)
        => new(
            new ServerPairing(serverId, new Uri("https://modbot.example"), "token", "grp_cats"),
            new FileEventBuffer(Path.Combine(_directory, $"{serverId}.jsonl"), _clock),
            new PresenceEventMapper(new LogTimestampConverter(TimeZoneInfo.Utc), new ServerClock(_clock)),
            new ServerClock(_clock),
            transport ?? new StubTransport(IngestOutcome.Accepted),
            _clock,
            "2026.9.0");

    private sealed class StubTransport(IngestOutcome outcome) : IIngestTransport
    {
        public Task<IngestResult> SendAsync(ServerPairing pairing, EventBatch batch, CancellationToken cancellationToken)
            => Task.FromResult(new IngestResult(outcome));
    }

    [Fact]
    public void WithNothingPairedItSaysSoRatherThanLookingBroken()
    {
        var snapshot = State().Snapshot();

        Assert.Empty(snapshot.Servers);
        Assert.Empty(snapshot.Warnings);
        Assert.Equal(LogHealthStatus.Idle, snapshot.LogStatus);
        Assert.Contains("VRChat is not running", snapshot.LogDetail);
    }

    [Fact]
    public void PausedSaysThatNothingIsBeingCapturedAndNotMerelyNotSent()
    {
        // The difference matters and is the promise the pause button makes. Pausing stops Modbot
        // observing what you do; it does not save it up to report when you resume.
        var state = State();
        var connection = Connection();
        state.Connections.Add(connection);

        connection.IsPaused = true;
        var row = Assert.Single(state.Snapshot().Servers);

        Assert.True(row.IsPaused);
        Assert.Contains("is being captured", row.Detail);
        Assert.Contains("does not save it up", row.Detail);
    }

    [Fact]
    public void PausedIsNotReportedAsAWarning()
    {
        // It is a choice the moderator made a moment ago, not a fault. Warning about it would
        // train them to ignore the warnings that matter.
        var state = State();
        var connection = Connection();
        state.Connections.Add(connection);
        connection.IsPaused = true;

        Assert.Empty(state.Snapshot().Warnings);
    }

    [Fact]
    public async Task ARejectedTokenIsAWarningThatSaysItWillNotRestartOnItsOwn()
    {
        var state = State();
        var connection = Connection(transport: new StubTransport(IngestOutcome.Unauthorised));
        state.Connections.Add(connection);

        connection.Accept(Observation());
        _clock.Advance(ServerConnection.DefaultBatchInterval + TimeSpan.FromSeconds(1));
        await connection.PumpAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(state.Snapshot().Warnings).Message;

        Assert.Contains("rejected this device", warning);
        Assert.Contains("will not restart on its own", warning);

        // And says why it might be perfectly correct, so a moderator who was removed from staff
        // is not left thinking the program is broken.
        Assert.Contains("removed from that group's staff", warning);
    }

    [Fact]
    public void AnUndecryptablePairingNamesTheServerAndSaysWhatHappened()
    {
        // The moderator needs to know which server to pair again, and that the cause is a settings
        // file that moved between Windows accounts rather than anything sinister.
        var state = State();
        state.UnusablePairings.Add(new LoadedPairing("cats", null, PairingFault.TokenUndecryptable));

        var warning = Assert.Single(state.Snapshot().Warnings).Message;

        Assert.Contains("cats", warning);
        Assert.Contains("cannot be decrypted", warning);
        Assert.Contains("Pair this server again", warning);
    }

    [Fact]
    public void AnUnrecognisedLogNamesBothPossibleCausesRatherThanGuessing()
    {
        // Missing verbose-logging flags and a changed log format have the same symptom -- nothing
        // is reported -- and only one is Modbot's fault. Guessing between them sends the moderator
        // to the wrong fix.
        var state = State();
        state.LogHealth = new LogHealth(
            9000,
            400,
            0,
            _clock.UtcNow,
            null,
            LastTimestampedLineAt: _clock.UtcNow,
            LastBehaviourLineAt: _clock.UtcNow);

        var snapshot = state.Snapshot();

        Assert.Equal(LogHealthStatus.NotUnderstood, snapshot.LogStatus);
        var warning = Assert.Single(snapshot.Warnings).Message;
        Assert.Contains("verbose logging flags", warning);
        Assert.Contains("log format has changed", warning);
    }

    [Fact]
    public void AnInstanceWhereNothingIsHappeningIsNotWarnedAbout()
    {
        // A moderator sitting alone in a world is not a fault and must not be told it is one. The
        // lines Modbot reads simply are not being written, while VRChat's own frame-rate lines keep
        // the file growing all evening.
        var state = State();
        state.LogHealth = new LogHealth(
            9000,
            400,
            120,
            _clock.UtcNow,
            _clock.UtcNow - TimeSpan.FromHours(1),
            LastTimestampedLineAt: _clock.UtcNow,
            LastBehaviourLineAt: _clock.UtcNow - TimeSpan.FromHours(1));

        var snapshot = state.Snapshot();

        Assert.Equal(LogHealthStatus.Quiet, snapshot.LogStatus);
        Assert.Empty(snapshot.Warnings);
        Assert.Contains("Nothing has happened", snapshot.LogDetail);
    }

    [Fact]
    public void TheSettingsCardIsNotDrawnWhenTheSwitchInItIsNot()
    {
        // The card holds one switch, and only an installed copy shows it. A copy run from a folder
        // was left with a heading and nothing under it, which reads as a screen that failed to draw.
        var state = State();

        Assert.False(state.Snapshot().ShowStartupCard);

        state.Startup = new StartupState(Visible: true, On: true, TurnedOffInWindows: false);

        Assert.True(state.Snapshot().ShowStartupCard);
    }

    [Fact]
    public void AHealthyLogStatesTheRatioAModeratorCanCheck()
    {
        // "We read one tag out of a dozen" is a claim somebody can verify against the file itself,
        // which is the whole reason to state it rather than say "working".
        var state = State();
        state.LogHealth = new LogHealth(17000, 700, 120, _clock.UtcNow, _clock.UtcNow);

        var snapshot = state.Snapshot();

        Assert.Equal(LogHealthStatus.Healthy, snapshot.LogStatus);
        Assert.Contains("17,000", snapshot.LogDetail);
        Assert.Contains("700", snapshot.LogDetail);
        Assert.Contains("120", snapshot.LogDetail);
    }

    [Fact]
    public async Task WaitingSaysNothingIsLost()
    {
        var state = State();
        var connection = Connection(transport: new StubTransport(IngestOutcome.ServerTrouble));
        state.Connections.Add(connection);

        connection.Accept(Observation());
        _clock.Advance(ServerConnection.DefaultBatchInterval + TimeSpan.FromSeconds(1));
        await connection.PumpAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(state.Snapshot().Servers);

        Assert.Equal(ConnectionState.Waiting, row.State);
        Assert.Contains("none are lost", row.Detail);
    }

    [Fact]
    public void TheJournalIsCarriedThroughToTheWindow()
    {
        var journal = Journal();
        var state = new CompanionAppState(_clock, journal);

        journal.RecordSent("cats", [WireEvent()]);

        Assert.Contains("Rin", Assert.Single(state.Snapshot().Events).Summary);
    }

    [Fact]
    public void ADownloadedUpdateIsAnInformationalLineThatNamesTheVersionAndTheNextStep()
    {
        // Never a restart, never a critical warning: an update waits for the moderator to quit
        // and reopen, and the line has to say that a pairing survives it, because "reinstall"
        // is the word that makes a volunteer expect to start over.
        var state = State();
        state.UpdateReady = "2026.9.2";

        var warning = Assert.Single(state.Snapshot().Warnings);

        Assert.Equal(WarningSeverity.Info, warning.Severity);
        Assert.Contains("2026.9.2", warning.Message);
        Assert.Contains("next time Modbot starts", warning.Message);
        Assert.Contains("pairings are kept", warning.Message);
    }

    private static ObservedPresence Observation()
    {
        Assert.True(InstanceLocation.TryParse(
            "wrld_4b34:39911~group(grp_cats)~groupAccessType(members)~region(use)", out var location));

        return new ObservedPresence(
            PresenceKind.Joined,
            new DateTime(2026, 9, 12, 20, 14, 7, DateTimeKind.Unspecified),
            "usr_8f2c",
            "Rin",
            location);
    }

    private static CompanionEvent WireEvent() => new()
    {
        CompanionEventId = "e1",
        Type = CompanionEventType.InstanceJoined,
        OccurredAt = new DateTimeOffset(2026, 9, 12, 20, 14, 7, TimeSpan.Zero),
        SubjectId = "usr_8f2c",
        WorldId = "wrld_4b34",
        InstanceId = "39911",
        GroupId = "grp_cats",
        Data = new Dictionary<string, string>(StringComparer.Ordinal) { ["displayName"] = "Rin" },
    };

    [Fact]
    public void TheSnapshotCarriesTheOverlayAndTheDebugSwitch()
    {
        var state = State();

        Assert.Same(OverlayStatus.None, state.Snapshot().OverlayOrNone);
        Assert.False(state.Snapshot().DebugMode);

        state.Overlay = OverlayStatus.None with { Attached = true, State = "attached", FramesDrawn = 3 };
        state.DebugMode = true;

        var snapshot = state.Snapshot();
        Assert.True(snapshot.OverlayOrNone.Attached);
        Assert.Equal(3, snapshot.OverlayOrNone.FramesDrawn);
        Assert.True(snapshot.DebugMode);
    }

    [Fact]
    public void SwitchedOffAndCouldNotBeSetUpAreDifferentAnswers()
    {
        // "You turned it off" and "this PC could not build a panel" send a moderator to different
        // places, so the SteamVR page and the sidebar must be able to tell them apart. Neither is
        // "on but SteamVR is not running", which is the ordinary case and says nothing new.
        Assert.False(OverlayStatus.Off.On);
        Assert.False(OverlayStatus.Off.Attached);
        Assert.Equal("off", OverlayStatus.Off.State);

        Assert.True(OverlayStatus.None.On);
        Assert.False(OverlayStatus.None.Attached);
        Assert.Equal("not set up", OverlayStatus.None.State);

        // Everything else defaults to on, so nothing that builds a status by hand accidentally
        // reads as switched off.
        Assert.True(new OverlayStatus(
            true, "attached", "", null, 0, null, "Cat Lounge", 0, "up to date", null, null, null).On);
    }
}
