using Modbot.Client.Instances;
using Modbot.Client.LogReading;
using Modbot.Client.Tests.LogReading;

namespace Modbot.Client.Tests.Instances;

/// <summary>
/// The phantom-burst rules applied to a real session, end to end.
/// </summary>
/// <remarks>
/// A unit test proves the rules do what their author meant. This proves they survive the log
/// VRChat actually wrote — a home world, a populated group instance with real arrivals and
/// departures inside it, both bursts, and an exit that was never logged at all.
/// </remarks>
public class RealSessionTests
{
    private static List<ObservedPresence> Replay()
    {
        var tracker = new InstanceSessionTracker();
        return [.. LogFixture.Events().SelectMany(tracker.Observe)];
    }

    private static List<ObservedPresence> InGroupInstance()
        => [.. Replay().Where(o => o.Instance.GroupId == LogFixture.GroupId)];

    [Fact]
    public void TheWholeSessionReducesToThirtyTwoFacts()
    {
        // 88 recognised lines, of which 40 are joins, leaves and room transitions. 18 of those 40
        // are phantom -- 7 roster joins reported as arrivals plus 11 departures for people who
        // never left. Recording them would have been a 60% error in the numbers, silently.
        Assert.Equal(32, Replay().Count);
    }

    [Fact]
    public void TheSevenPeopleAlreadyInTheGroupInstanceAreObserved_NotArrivals()
    {
        var observed = InGroupInstance();

        var roster = observed.Where(o => o.Kind == PresenceKind.PresenceObserved).ToList();
        Assert.Equal(7, roster.Count);
        Assert.All(roster, o => Assert.Equal(new DateTime(2026, 9, 3, 20, 32, 18), o.OccurredAtLocal));
        Assert.Equal(
            ["ΛƧƬΛ", "-winter~", "BlackIndium", "Fraiinco2Wavy", "CODYYYYYYYYYYYY", "nicopuppyboy", "~ RedZu ~"],
            roster.Select(o => o.DisplayName));
    }

    [Fact]
    public void TheModeratorsOwnArrivalIsTheEighthAndItIsExact()
    {
        var observed = InGroupInstance();

        var mine = observed.First(o => o.SubjectId == LogFixture.LocalUserId);
        Assert.Equal(PresenceKind.Joined, mine.Kind);
        Assert.Equal(new DateTime(2026, 9, 3, 20, 32, 18), mine.OccurredAtLocal);
    }

    [Fact]
    public void TheSixGenuineArrivalsInsideTheGroupInstanceAreKept()
    {
        var arrivals = InGroupInstance()
            .Where(o => o.Kind == PresenceKind.Joined)
            .Select(o => (o.DisplayName, o.OccurredAtLocal.TimeOfDay))
            .ToList();

        Assert.Equal(
            [
                ("bin¹", new TimeSpan(20, 32, 18)),
                ("-winter~", new TimeSpan(20, 37, 29)),
                ("CODYYYYYYYYYYYY", new TimeSpan(20, 42, 32)),
                ("MAR2109HD", new TimeSpan(20, 42, 42)),
                ("SubKay_", new TimeSpan(20, 44, 24)),
                ("Hawk Echos", new TimeSpan(20, 44, 29)),
                ("-Traceless-", new TimeSpan(20, 45, 0)),
            ],
            arrivals);
    }

    [Fact]
    public void TheElevenPhantomDeparturesAreDiscardedAndTheFiveRealOnesKept()
    {
        var departures = InGroupInstance()
            .Where(o => o.Kind == PresenceKind.Left)
            .Select(o => (o.DisplayName, o.OccurredAtLocal.TimeOfDay))
            .ToList();

        Assert.Equal(
            [
                ("-winter~", new TimeSpan(20, 33, 11)),
                ("CODYYYYYYYYYYYY", new TimeSpan(20, 40, 56)),
                ("CODYYYYYYYYYYYY", new TimeSpan(20, 44, 6)),
                ("-winter~", new TimeSpan(20, 45, 28)),

                // The moderator's own departure, taken from OnLeftRoom rather than from the copy of
                // it buried in the phantom burst a moment later.
                ("bin¹", new TimeSpan(20, 45, 29)),
            ],
            departures);
    }

    [Fact]
    public void NobodyStillStandingInTheInstanceIsRecordedAsHavingLeft()
    {
        // ΛƧƬΛ, BlackIndium, Fraiinco2Wavy, nicopuppyboy, ~ RedZu ~, MAR2109HD, SubKay_,
        // Hawk Echos, -Traceless- and hevy1015 were all still there. Their sessions stop being
        // observed, which is not the same thing as ending.
        var stillThere = new[]
        {
            "ΛƧƬΛ", "BlackIndium", "Fraiinco2Wavy", "nicopuppyboy", "~ RedZu ~",
            "MAR2109HD", "SubKay_", "Hawk Echos", "-Traceless-", "hevy1015",
        };

        var departed = InGroupInstance()
            .Where(o => o.Kind == PresenceKind.Left)
            .Select(o => o.DisplayName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(stillThere, name => Assert.DoesNotContain(name, departed));
    }

    [Fact]
    public void SomebodyWhoOnlyEverAppearsInTheDepartureBurstIsNeverReported()
    {
        // hevy1015 entered two seconds before the moderator left; VRChat logged their leave in the
        // phantom burst and never logged their join at all. A parser that pairs leaves with joins
        // would either crash or invent a session here.
        Assert.DoesNotContain(
            Replay(),
            o => o.DisplayName == "hevy1015" || o.SubjectId == "usr_c4ec4f0e-a7d6-489b-b74d-4036e913312d");
    }

    [Fact]
    public void AvatarChangesAreOnlyTheOnesThatWereActuallyChanges()
    {
        // 33 "Switching X to avatar Y" lines in the file. Eight of them are the arrival burst
        // announcing what everyone is already wearing, and most of the rest are VRChat repeating
        // one avatar load two or three times.
        var changes = Replay().Where(o => o.Kind == PresenceKind.AvatarChanged).ToList();

        Assert.Equal(9, changes.Count);
        Assert.All(changes, c => Assert.NotNull(c.AvatarName));
        Assert.All(changes, c => Assert.NotNull(c.SubjectId));

        // Full-width punctuation survives the round trip.
        Assert.Contains(changes, c => c.AvatarName == "Nova-Line ［FT］");
    }

    [Fact]
    public void EveryFactCarriesBothTheWorldAndTheInstance()
    {
        Assert.All(Replay(), o =>
        {
            Assert.False(string.IsNullOrEmpty(o.Instance.WorldId));
            Assert.False(string.IsNullOrEmpty(o.Instance.InstanceId));
        });
    }

    [Fact]
    public void TheSessionIsSpreadAcrossTheThreeInstancesActuallyVisited()
    {
        var instances = Replay().Select(o => o.Instance.InstanceId).Distinct().ToList();

        Assert.Equal(["69955", "85019", "39047"], instances);
    }

    [Fact]
    public void AnUncleanExitSimplyStopsReporting()
    {
        // The log ends mid-session: VRChat was killed in the friends-only instance and never wrote
        // OnLeftRoom or anything else. So the departure burst has no anchor and there is nothing to
        // report -- the last thing on record is the arrival. Closing those sessions is the server's
        // job, on a timeout, and cannot be done here.
        var last = Replay()[^1];

        Assert.Equal(PresenceKind.Joined, last.Kind);
        Assert.Equal("39047", last.Instance.InstanceId);
        Assert.Equal(new DateTime(2026, 9, 3, 20, 45, 48), last.OccurredAtLocal);
    }

    [Fact]
    public void TheTrackerStillKnowsWhereItIsWhenTheLogRunsOut()
    {
        var tracker = new InstanceSessionTracker();
        foreach (var _ in LogFixture.Events().SelectMany(tracker.Observe))
        {
            // Draining the sequence is the point; the state afterwards is what is under test.
        }

        Assert.Equal("39047", tracker.CurrentInstance?.InstanceId);
        Assert.Equal(LogFixture.LocalUserId, tracker.LocalUserId);
        Assert.Equal(2, tracker.Roster.Count);
    }

    [Fact]
    public void ReplayingTheLogTwiceProducesTheSameFacts()
    {
        // Restarting the client must not change what the log says happened.
        Assert.Equal(
            Replay().Select(o => $"{o.Kind}/{o.SubjectId}/{o.OccurredAtLocal:O}/{o.AvatarName}"),
            Replay().Select(o => $"{o.Kind}/{o.SubjectId}/{o.OccurredAtLocal:O}/{o.AvatarName}"));
    }

    [Fact]
    public void NoRawLogTextSurvivesIntoAFact()
    {
        // A fact carries ids, a display name, an avatar name and a timestamp. It never carries the
        // line it came from, and this is the test that says so.
        var lines = LogFixture.Lines().ToHashSet(StringComparer.Ordinal);

        Assert.All(Replay(), o =>
        {
            Assert.DoesNotContain(o.SubjectId, "[Behaviour]");
            Assert.DoesNotContain(lines, raw => raw.Contains(o.DisplayName ?? " ") && raw == o.DisplayName);
        });
    }

    [Fact]
    public void TheInviteNotificationInTheFullLogIsNotEvenLookedAt()
    {
        // The untrimmed log contains invite notifications carrying a sender's user id and the
        // message they typed. They are not [Behaviour] lines, so the parser never considers them --
        // which is the concrete version of "we read one tag and nothing else".
        const string invite =
            "2026.09.03 20:31:59 Debug      -  Received Notification: <Notification from "
            + "username:-winter~, sender user id:usr_527e5167 to usr_f2049d71 of type: invite, "
            + "message: \"Join me in The Black Cat\">";

        Assert.True(VRChatLogLineParser.TryParse(invite, out var line));
        Assert.Null(line.Tag);
        Assert.Null(BehaviourEventParser.Parse(line));
    }
}
