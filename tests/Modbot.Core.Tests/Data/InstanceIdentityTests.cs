using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Data;

/// <summary>
/// The rule that decides whether an instance is one Modbot already knows or a new one.
/// </summary>
/// <remarks>
/// Every case here is a way the rule can be wrong without anything throwing. A wrong answer
/// produces a session that never happened -- either two unrelated evenings welded into one row,
/// or one evening cut in half -- and no test that checks only for exceptions would notice.
/// </remarks>
public class InstanceIdentityTests
{
    private const string Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:85019";

    private static readonly DateTimeOffset Evening = new(2026, 9, 13, 21, 0, 0, TimeSpan.Zero);

    private static VRChatInstance Instance(
        DateTimeOffset lastSeenAt,
        bool seenInGroupList = false,
        DateTimeOffset? closedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Location = Location,
            WorldId = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b",
            VRChatInstanceId = "85019",
            OpenedAt = lastSeenAt,
            LastSeenAt = lastSeenAt,
            SeenInGroupList = seenInGroupList,
            ClosedAt = closedAt,
        };

    [Fact]
    public void NothingOpenMeansANewInstance()
    {
        Assert.Null(InstanceIdentity.Match([], Evening));
    }

    [Fact]
    public void ASightingMinutesLaterIsTheSameInstance()
    {
        var instance = Instance(Evening);

        Assert.Same(instance, InstanceIdentity.Match([instance], Evening.AddMinutes(4)));
    }

    [Fact]
    public void TheSameNumberThreeDaysLaterIsANewInstance()
    {
        var instance = Instance(Evening);

        Assert.Null(InstanceIdentity.Match([instance], Evening + VRChatInstance.CountsAsNewAfter));
    }

    [Fact]
    public void TheSameNumberJustInsideThreeDaysIsStillTheSameInstance()
    {
        var instance = Instance(Evening);
        var justInside = Evening + VRChatInstance.CountsAsNewAfter - TimeSpan.FromMinutes(1);

        Assert.Same(instance, InstanceIdentity.Match([instance], justInside));
    }

    /// <summary>
    /// The case the group's live list exists for: an instance open all week with nobody running the
    /// client in it. The list says it is still open, so no gap makes it a different instance.
    /// </summary>
    [Fact]
    public void AnInstanceTheGroupListCarriesSurvivesAnyGap()
    {
        var instance = Instance(Evening, seenInGroupList: true);

        Assert.Same(instance, InstanceIdentity.Match([instance], Evening.AddDays(30)));
    }

    /// <summary>
    /// A client that was offline sends its backlog on reconnect, so a line older than what is
    /// already recorded is ordinary. Splitting on it would cut one evening into two.
    /// </summary>
    [Fact]
    public void AReportThatArrivesLateButHappenedEarlierIsTheSameInstance()
    {
        var instance = Instance(Evening);

        Assert.Same(instance, InstanceIdentity.Match([instance], Evening.AddMinutes(-20)));
    }

    [Fact]
    public void AnInstanceClosedByTimeIsNeverMatchedAgain()
    {
        var instance = Instance(Evening, closedAt: Evening.AddHours(1));
        instance.ClosedBy = "time";

        Assert.Null(InstanceIdentity.Match([instance], Evening.AddHours(2)));
    }

    /// <summary>
    /// The case the probe on 2026-09-13 could not settle: it is not known whether an empty instance
    /// stays in the group's list. If it does not, a quiet stretch would look like a close followed
    /// by a new instance, and every figure about how long instances run would be wrong. Coming straight
    /// back means it was the same instance all along.
    /// </summary>
    [Fact]
    public void AnInstanceTheListDroppedAndCarriedAgainMinutesLaterIsTheSameInstance()
    {
        var instance = Instance(Evening, seenInGroupList: true, closedAt: Evening.AddMinutes(30));
        instance.ClosedBy = "list";

        var backAgain = Evening.AddMinutes(32);

        Assert.Same(instance, InstanceIdentity.Match([instance], backAgain));
    }

    [Fact]
    public void AnInstanceTheListDroppedLongAgoIsANewInstance()
    {
        var instance = Instance(Evening, seenInGroupList: true, closedAt: Evening.AddMinutes(30));
        instance.ClosedBy = "list";

        var muchLater = Evening.AddMinutes(30) + InstanceIdentity.ReopensWithin;

        Assert.Null(InstanceIdentity.Match([instance], muchLater));
    }

    /// <summary>
    /// Reopening is only ever an undo of the list's own close. An instance closed by the time rule has
    /// been quiet for three days, and letting it reopen would undo the split that rule exists for.
    /// </summary>
    [Fact]
    public void OnlyTheListsOwnCloseCanBeUndone()
    {
        var instance = Instance(Evening, closedAt: Evening.AddMinutes(30));
        instance.ClosedBy = "time";

        Assert.Null(InstanceIdentity.Match([instance], Evening.AddMinutes(32)));
    }

    /// <summary>
    /// Two open rows at one location means an earlier close was missed. Only the most recently
    /// seen could still be running, so that is the one a new sighting belongs to.
    /// </summary>
    [Fact]
    public void WhenTwoRowsAreOpenTheMostRecentlySeenWins()
    {
        var stale = Instance(Evening.AddHours(-6));
        var live = Instance(Evening);

        Assert.Same(live, InstanceIdentity.Match([stale, live], Evening.AddMinutes(5)));
        Assert.Same(live, InstanceIdentity.Match([live, stale], Evening.AddMinutes(5)));
    }
}
