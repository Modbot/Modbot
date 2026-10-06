using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Companion.Time;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// The loop's side of a public, friends-only or private instance: the lists come from this PC's own
/// log, the panel says it is not synced with the group, and no roster, live or heads-up call is
/// made. The one call that can still go out is the profile read for a person who was tapped.
/// </summary>
public class OverlayDriverNotSyncedTests
{
    private const string Group = "grp_cats";
    private const string World = "wrld_4b34";
    private const string Plain = "12345";

    private sealed class RecordingPresenter : IOverlayPresenter
    {
        public OverlayScreen Last { get; private set; } = OverlayScreen.Idle;

        public bool Update(OverlayScreen screen)
        {
            Last = screen;
            return true;
        }

        public void Show()
        {
        }

        public void Hide()
        {
        }
    }

    private sealed class Reads : IOverlayReadClient
    {
        public int ContextCalls { get; private set; }

        public int LiveCalls { get; private set; }

        /// <summary>Which server each profile read went to, and about whom.</summary>
        public List<(string Server, string Subject)> Users { get; } = [];

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, string? worldId, CancellationToken cancellationToken)
        {
            ContextCalls++;
            return Task.FromResult(new ReadResult<InstanceContext>(ReadOutcome.Unreachable));
        }

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? worldId, string? after, int waitSeconds, CancellationToken cancellationToken)
        {
            LiveCalls++;
            return Task.FromResult(new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));
        }

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
        {
            Users.Add((pairing.ServerId, subjectId));
            return Task.FromResult(new ReadResult<UserSummary>(
                ReadOutcome.Fetched,
                new UserSummary(subjectId, "From the server", RosterStanding.Member, 3, null, ["prior kick"], ["Helper"])));
        }
    }

    private static InstanceLocation Location(string instance = Plain, string? group = null)
    {
        var raw = group is null
            ? $"{World}:{instance}~region(use)"
            : $"{World}:{instance}~group({group})~groupAccessType(members)~region(use)";

        Assert.True(InstanceLocation.TryParse(raw, out var location));
        return location;
    }

    private static ServerPairing Pairing(string id, string group)
        => new(id, new Uri($"https://{id}.example"), "token", group);

    private static PersonHere Here(string name) => new("usr_" + name, name, null);

    private static (OverlayDriver Driver, RecordingPresenter Presenter, Reads Reads, FakeClock Clock) Build(bool paired = true)
    {
        var presenter = new RecordingPresenter();
        var reads = new Reads();
        var clock = new FakeClock();
        var driver = new OverlayDriver(presenter, reads, clock);

        if (paired)
            driver.Add(Pairing("cats", Group), "Cat Lounge");

        return (driver, presenter, reads, clock);
    }

    private static ObservedPresence Seen(PresenceKind kind, string name, InstanceLocation? where = null, int minute = 0)
        => new(kind, new DateTime(2026, 10, 5, 21, minute, 0, DateTimeKind.Unspecified), "usr_" + name, name, where ?? Location());

    [Fact]
    public async Task APlainInstanceIsNotIdleAndSaysItIsNotSynced()
    {
        var (driver, presenter, _, _) = Build();
        driver.PeopleHere = [Here("Jo"), Here("Kai")];

        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(presenter.Last.NotSynced);
        Assert.False(presenter.Last.IsIdle);
        Assert.Null(presenter.Last.GroupLabel);
    }

    [Fact]
    public async Task TheInstanceListIsThePeopleTheLogNamesWithNothingKnownAboutThem()
    {
        var (driver, presenter, _, _) = Build();
        driver.PeopleHere = [Here("Jo"), Here("Kai")];

        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        var members = presenter.Last.Roster.Value!.Members;
        Assert.Equal(["Jo", "Kai"], members.Select(m => m.DisplayName));
        Assert.All(members, m =>
        {
            Assert.Equal(RosterStanding.Ordinary, m.Standing);
            Assert.Equal(0, m.PriorActions);
            Assert.Empty(m.Flags);
            Assert.Null(m.TrustRank);
            Assert.Null(m.EighteenPlus);
        });
    }

    [Fact]
    public async Task NoRosterOrLiveReadIsMadeAndNoHeadsUpsAreOffered()
    {
        var (driver, presenter, reads, clock) = Build();
        driver.PeopleHere = [Here("Jo")];

        driver.EnteredInstance(Location());
        for (var i = 0; i < 4; i++)
        {
            await driver.TickAsync(TestContext.Current.CancellationToken);
            clock.Advance(OverlayDriver.ContextRefreshInterval + TimeSpan.FromSeconds(1));
        }

        Assert.Equal(0, reads.ContextCalls);
        Assert.Equal(0, reads.LiveCalls);
        Assert.Empty(reads.Users);
        Assert.False(presenter.Last.CanPlaceHeadsUps);
        Assert.Empty(presenter.Last.HeadsUpsOrNone);
        Assert.Null(driver.CurrentServer);
    }

    [Fact]
    public async Task AHeadsUpCannotBeStartedFromARowThere()
    {
        var (driver, presenter, _, _) = Build();
        driver.PeopleHere = [Here("Jo")];

        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.AddHeadsUp("usr_Jo"));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Null(driver.Draft);
        Assert.Null(presenter.Last.Draft);
    }

    [Fact]
    public async Task WithNoServerPairedThePanelStaysIdle()
    {
        // Nothing to say it is not synced with, and nobody to look people up on.
        var (driver, presenter, reads, _) = Build(paired: false);
        driver.PeopleHere = [Here("Jo")];

        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(presenter.Last.IsIdle);
        Assert.False(presenter.Last.NotSynced);
        Assert.Equal(0, reads.ContextCalls);
    }

    [Fact]
    public async Task AGroupInstanceNoPairedServerManagesIsStillIdle()
    {
        var (driver, presenter, _, _) = Build();
        driver.PeopleHere = [Here("Jo")];

        driver.EnteredInstance(Location(group: "grp_somebody_else"));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(presenter.Last.IsIdle);
        Assert.False(presenter.Last.NotSynced);
    }

    [Fact]
    public async Task AGroupInstanceStillShowsTheGroupAndIsNotMarkedNotSynced()
    {
        var (driver, presenter, _, _) = Build();

        driver.EnteredInstance(Location(group: Group));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(presenter.Last.NotSynced);
        Assert.Equal("Cat Lounge", presenter.Last.GroupLabel);
    }

    [Fact]
    public async Task SomebodyTheLogDropsIsKeptAsJustLeftForAMinuteAndIsNotCounted()
    {
        var (driver, presenter, _, clock) = Build();
        driver.PeopleHere = [Here("Jo"), Here("Kai")];

        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(presenter.Last.LeftOrNone);

        driver.PeopleHere = [Here("Kai")];
        await driver.TickAsync(TestContext.Current.CancellationToken);

        var left = Assert.Single(presenter.Last.LeftOrNone);
        Assert.Equal("usr_Jo", left.Member.SubjectId);
        Assert.Equal(60, left.SecondsLeft(clock.UtcNow));
        Assert.Equal(["usr_Kai"], presenter.Last.Roster.Value!.Members.Select(m => m.SubjectId));

        clock.Advance(TimeSpan.FromSeconds(61));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(presenter.Last.LeftOrNone);
    }

    [Fact]
    public async Task AnotherInstanceStartsWithNobodyJustLeft()
    {
        var (driver, presenter, _, _) = Build();
        driver.PeopleHere = [Here("Jo"), Here("Kai")];
        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);
        driver.PeopleHere = [Here("Kai")];
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Single(presenter.Last.LeftOrNone);

        driver.EnteredInstance(Location("99999"));
        driver.PeopleHere = [Here("Ash")];
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Empty(presenter.Last.LeftOrNone);
        Assert.Equal(["Ash"], presenter.Last.Roster.Value!.Members.Select(m => m.DisplayName));
    }

    [Fact]
    public async Task AJoinTimeFromTheLogIsOnTheRow()
    {
        var (driver, presenter, _, _) = Build();
        driver.PeopleHere = [Here("Jo"), Here("Kai")];
        driver.ArrivedAt = new Dictionary<string, DateTime?>
        {
            ["usr_Jo"] = new DateTime(2026, 10, 5, 20, 50, 0, DateTimeKind.Unspecified),
            ["usr_Kai"] = null,
        };

        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(presenter.Last.ArrivalsOrNone["usr_Jo"] is not null);
        Assert.Null(presenter.Last.ArrivalsOrNone["usr_Kai"]);
    }

    [Fact]
    public async Task TheModeratorsOwnArrivalIsOnTheScreenForWhoWasHereBeforeThem()
    {
        var (driver, presenter, _, _) = Build();
        driver.PeopleHere = [Here("Me"), Here("Kai")];
        var arrived = new DateTime(2026, 10, 5, 20, 50, 0, DateTimeKind.Unspecified);
        driver.ArrivedAt = new Dictionary<string, DateTime?> { ["usr_Me"] = arrived, ["usr_Kai"] = null };
        driver.ModeratorId = "usr_Me";

        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new LogTimestampConverter().ToInstant(arrived), presenter.Last.ModeratorArrived);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task WithNoRecordOfTheModeratorsArrivalNoTimeIsGuessed(bool knowsWho, bool hasArrival)
    {
        // The overlay was switched on mid-instance, or the log has not said who the moderator is.
        var (driver, presenter, _, _) = Build();
        driver.PeopleHere = [Here("Kai")];
        driver.ArrivedAt = hasArrival
            ? new Dictionary<string, DateTime?> { ["usr_Me"] = new DateTime(2026, 10, 5, 20, 50, 0, DateTimeKind.Unspecified) }
            : new Dictionary<string, DateTime?> { ["usr_Kai"] = null };
        driver.ModeratorId = knowsWho ? "usr_Me" : null;

        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Null(presenter.Last.ModeratorArrived);
    }

    [Fact]
    public async Task TheAuditLogIsWhatTheLogSaidAboutJoinsLeavesAndWhoWasAlreadyHere()
    {
        var (driver, presenter, _, _) = Build();
        driver.EnteredInstance(Location());

        driver.NoteObserved(
        [
            Seen(PresenceKind.PresenceObserved, "Jo", minute: 1),
            Seen(PresenceKind.Joined, "Kai", minute: 2),
            Seen(PresenceKind.Left, "Jo", minute: 3),
            Seen(PresenceKind.AvatarChanged, "Kai", minute: 4),
        ]);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        // Newest first, and a change of avatar is not a row.
        Assert.Equal(
            [LiveEventKinds.PersonLeft, LiveEventKinds.PersonJoined, LiveEventKinds.PersonHere],
            presenter.Last.EventsOrNone.Select(e => e.Kind));
        Assert.Equal(["usr_Jo", "usr_Kai", "usr_Jo"], presenter.Last.EventsOrNone.Select(e => e.Person?.SubjectId));
        Assert.Equal(3, presenter.Last.EventsOrNone.Select(e => e.Id).Distinct().Count());
        Assert.All(presenter.Last.EventsOrNone, e => Assert.False(e.Flagged));
    }

    [Fact]
    public async Task WhatTheLogSaidBeforeTheDriverWasToldTheInstanceIsKept()
    {
        // The log is read, and its observations handed over, a moment before the driver hears
        // which instance the moderator walked into.
        var (driver, presenter, _, _) = Build();
        var arriving = Location("55555");

        driver.NoteObserved([Seen(PresenceKind.PresenceObserved, "Jo", arriving)]);
        driver.EnteredInstance(arriving);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Single(presenter.Last.EventsOrNone);
    }

    [Fact]
    public async Task WhatTheLogSaidAboutAnotherInstanceIsNotOnThisOnesAuditLog()
    {
        var (driver, presenter, _, _) = Build();
        var before = Location("11111");
        driver.EnteredInstance(before);
        driver.NoteObserved([Seen(PresenceKind.Joined, "Jo", before)]);

        driver.EnteredInstance(Location("22222"));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Empty(presenter.Last.EventsOrNone);
    }

    [Fact]
    public async Task AGroupInstancesObservationsAreLeftToItsServer()
    {
        var (driver, presenter, _, _) = Build();
        var group = Location("33333", Group);
        driver.EnteredInstance(group);

        driver.NoteObserved([Seen(PresenceKind.Joined, "Jo", group)]);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Empty(presenter.Last.EventsOrNone);
    }

    [Fact]
    public async Task TheLogRestatingWhoIsHereDoesNotListThemAgain()
    {
        var (driver, presenter, _, _) = Build();
        driver.EnteredInstance(Location());

        driver.NoteObserved([Seen(PresenceKind.PresenceObserved, "Jo")]);
        driver.NoteObserved([Seen(PresenceKind.PresenceObserved, "Jo", minute: 5)]);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Single(presenter.Last.EventsOrNone);
    }

    [Fact]
    public async Task TappingAPersonAsksTheirProfileOnlyAndOpensTheirCard()
    {
        var (driver, presenter, reads, _) = Build();
        driver.PeopleHere = [Here("Jo")];
        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.Person("usr_Jo"));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal([("cats", "usr_Jo")], reads.Users);
        Assert.Equal(OverlayPage.Person, presenter.Last.Page);
        Assert.Equal("From the server", presenter.Last.Person?.DisplayName);
        Assert.Equal(RosterStanding.Member, presenter.Last.Person?.Standing);

        // Still nothing about the instance went anywhere.
        Assert.Equal(0, reads.ContextCalls);
        Assert.Equal(0, reads.LiveCalls);
    }

    [Fact]
    public async Task TheCardOpensAtOnceFromTheNameTheLogGave()
    {
        // Before the server answers, the card is the row the list has: a name and nothing else.
        var presenter = new RecordingPresenter();
        var driver = new OverlayDriver(presenter, new SilentUserReads(), new FakeClock());
        driver.Add(Pairing("cats", Group), "Cat Lounge");
        driver.PeopleHere = [Here("Jo")];
        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.Person("usr_Jo"));
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Jo", presenter.Last.Person?.DisplayName);
        Assert.Equal(RosterStanding.Ordinary, presenter.Last.Person?.Standing);
    }

    private sealed class SilentUserReads : IOverlayReadClient
    {
        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, string? worldId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? worldId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<UserSummary>(ReadOutcome.Unreachable));
    }

    [Fact]
    public async Task WithSeveralServersTheFirstPairedIsAskedUntilAGroupInstanceHasBeenVisited()
    {
        var (driver, _, reads, _) = Build();
        driver.Add(Pairing("dogs", "grp_dogs"), "Dog Park");
        driver.PeopleHere = [Here("Jo")];
        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.Person("usr_Jo"));

        Assert.Equal("cats", reads.Users.Single().Server);
    }

    [Fact]
    public async Task WithSeveralServersTheOneThatManagedTheLastGroupInstanceIsAsked()
    {
        var (driver, _, reads, _) = Build();
        driver.Add(Pairing("dogs", "grp_dogs"), "Dog Park");

        // The moderator stood in the second server's group instance, then walked into a plain one.
        driver.EnteredInstance(Location("44444", "grp_dogs"));
        await driver.TickAsync(TestContext.Current.CancellationToken);
        driver.EnteredInstance(Location());
        driver.PeopleHere = [Here("Jo")];
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.Tap(new OverlayTarget.Person("usr_Jo"));

        Assert.Equal("dogs", reads.Users.Single().Server);
    }

    [Fact]
    public async Task ScrollingCountsThePeopleOnTheLog()
    {
        var (driver, presenter, _, _) = Build();
        driver.PeopleHere = [Here("Jo"), Here("Kai"), Here("Ash")];
        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);

        driver.ScrollRoster(10);
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, presenter.Last.RosterSkip);
    }

    [Fact]
    public async Task SaveAClipTravelsWithTheNotSyncedScreen()
    {
        var (driver, presenter, _, _) = Build();
        driver.Clips = new Modbot.Companion.Clips.ClipButton(Modbot.Companion.Clips.ClipButtonState.Ready, "Save a clip", CanPress: true);
        driver.EnteredInstance(Location());

        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(presenter.Last.Clips.IsVisible);
    }
}
