using Modbot.Companion.Instances;
using Modbot.Companion.Routing;
using Modbot.Companion.Tests.LogReading;

namespace Modbot.Companion.Tests.Routing;

public class EventRouterTests
{
    private sealed class RecordingTarget(string serverId, string managedGroupId) : IIngestTarget
    {
        public string ServerId { get; } = serverId;

        public string ManagedGroupId { get; } = managedGroupId;

        public List<ObservedPresence> Accepted { get; } = [];

        public void Accept(ObservedPresence observation) => Accepted.Add(observation);
    }

    private static ObservedPresence In(string location)
    {
        Assert.True(InstanceLocation.TryParse(location, out var parsed));

        return new ObservedPresence(
            PresenceKind.Joined,
            new DateTime(2026, 9, 3, 20, 32, 18),
            "usr_subject",
            "somebody",
            parsed);
    }

    [Fact]
    public void GoesToTheServerThatManagesTheOwningGroup()
    {
        var cats = new RecordingTarget("cats", "grp_cats");
        var router = new EventRouter([cats]);

        Assert.Equal(1, router.Dispatch(In("wrld_w:1~group(grp_cats)~region(use)")));
        Assert.Single(cats.Accepted);
    }

    [Fact]
    public void OneGroupsEventsNeverReachAnotherGroupsServer()
    {
        // The single most important test in the multi-server work. Its failure mode is silent:
        // everything appears to function while one community's data accumulates in another's
        // database.
        var cats = new RecordingTarget("cats", "grp_cats");
        var dogs = new RecordingTarget("dogs", "grp_dogs");
        var router = new EventRouter([cats, dogs]);

        router.Dispatch(In("wrld_w:1~group(grp_cats)~region(use)"));

        Assert.Single(cats.Accepted);
        Assert.Empty(dogs.Accepted);
    }

    [Theory]
    [InlineData("wrld_w:1~private(usr_a)~nonce(secret)~region(use)")]
    [InlineData("wrld_w:1~friends(usr_a)~region(use)")]
    [InlineData("wrld_w:1~hidden(usr_a)~region(use)")]
    [InlineData("wrld_w:1~region(use)")]
    public void AnInstanceWithNoOwningGroupIsSentNowhere(string location)
    {
        // A moderator's personal VRChat use. Not filtered server-side, not sent and then discarded
        // -- never transmitted, because there is nobody it could correctly be transmitted to.
        var anyone = new RecordingTarget("anyone", "grp_cats");
        var router = new EventRouter([anyone]);

        Assert.Equal(0, router.Dispatch(In(location)));
        Assert.Empty(anyone.Accepted);
    }

    [Fact]
    public void AGroupNobodyManagesIsSentNowhere()
    {
        var cats = new RecordingTarget("cats", "grp_cats");
        var router = new EventRouter([cats]);

        Assert.Equal(0, router.Dispatch(In("wrld_w:1~group(grp_strangers)")));
    }

    [Fact]
    public void TwoServersManagingTheSameGroupBothGetIt()
    {
        // Unusual but legitimate: a group running a second Modbot, perhaps mid-migration.
        var primary = new RecordingTarget("primary", "grp_cats");
        var secondary = new RecordingTarget("secondary", "grp_cats");
        var router = new EventRouter([primary, secondary]);

        Assert.Equal(2, router.Dispatch(In("wrld_w:1~group(grp_cats)")));
    }

    [Fact]
    public void GroupIdsAreComparedExactlyAsVRChatWroteThem()
    {
        // No case folding and no normalisation. Two ids differing only in case are two ids.
        var cats = new RecordingTarget("cats", "grp_CATS");
        var router = new EventRouter([cats]);

        Assert.Equal(0, router.Dispatch(In("wrld_w:1~group(grp_cats)")));
    }

    [Fact]
    public void TheRealSessionRoutesOnlyItsGroupInstance()
    {
        // Three instances were visited in the fixture: a home world, a group instance, and a
        // friends-only world. Only the middle one is any of Modbot's business.
        var cats = new RecordingTarget("cats", LogFixture.GroupId);
        var router = new EventRouter([cats]);
        var tracker = new InstanceSessionTracker();

        var dropped = router.DispatchAll(LogFixture.Events().SelectMany(tracker.Observe));

        Assert.Equal(LogFixture.GroupId, Assert.Single(cats.Accepted.Select(a => a.Instance.GroupId).Distinct()));
        Assert.Equal(27, cats.Accepted.Count);
        Assert.Equal(5, dropped);
    }
}
