using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Data;

/// <summary>
/// The rule that decides whether a room is one Modbot already knows or a new one.
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

    private static VRChatInstance Room(
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
    public void NothingOpenMeansANewRoom()
    {
        Assert.Null(InstanceIdentity.Match([], Evening));
    }

    [Fact]
    public void ASightingMinutesLaterIsTheSameRoom()
    {
        var room = Room(Evening);

        Assert.Same(room, InstanceIdentity.Match([room], Evening.AddMinutes(4)));
    }

    [Fact]
    public void TheSameNumberThreeDaysLaterIsANewRoom()
    {
        var room = Room(Evening);

        Assert.Null(InstanceIdentity.Match([room], Evening + VRChatInstance.CountsAsNewAfter));
    }

    [Fact]
    public void TheSameNumberJustInsideThreeDaysIsStillTheSameRoom()
    {
        var room = Room(Evening);
        var justInside = Evening + VRChatInstance.CountsAsNewAfter - TimeSpan.FromMinutes(1);

        Assert.Same(room, InstanceIdentity.Match([room], justInside));
    }

    /// <summary>
    /// The case the group's live list exists for: a room open all week with nobody running the
    /// client in it. The list says it is still open, so no gap makes it a different room.
    /// </summary>
    [Fact]
    public void ARoomTheGroupListCarriesSurvivesAnyGap()
    {
        var room = Room(Evening, seenInGroupList: true);

        Assert.Same(room, InstanceIdentity.Match([room], Evening.AddDays(30)));
    }

    /// <summary>
    /// A client that was offline sends its backlog on reconnect, so a line older than what is
    /// already recorded is ordinary. Splitting on it would cut one evening into two.
    /// </summary>
    [Fact]
    public void AReportThatArrivesLateButHappenedEarlierIsTheSameRoom()
    {
        var room = Room(Evening);

        Assert.Same(room, InstanceIdentity.Match([room], Evening.AddMinutes(-20)));
    }

    [Fact]
    public void ARoomClosedByTimeIsNeverMatchedAgain()
    {
        var room = Room(Evening, closedAt: Evening.AddHours(1));
        room.ClosedBy = "time";

        Assert.Null(InstanceIdentity.Match([room], Evening.AddHours(2)));
    }

    /// <summary>
    /// The case the probe on 2026-09-13 could not settle: it is not known whether an empty room
    /// stays in the group's list. If it does not, a quiet stretch would look like a close followed
    /// by a new room, and every figure about how long rooms run would be wrong. Coming straight
    /// back means it was the same room all along.
    /// </summary>
    [Fact]
    public void ARoomTheListDroppedAndCarriedAgainMinutesLaterIsTheSameRoom()
    {
        var room = Room(Evening, seenInGroupList: true, closedAt: Evening.AddMinutes(30));
        room.ClosedBy = "list";

        var backAgain = Evening.AddMinutes(32);

        Assert.Same(room, InstanceIdentity.Match([room], backAgain));
    }

    [Fact]
    public void ARoomTheListDroppedLongAgoIsANewRoom()
    {
        var room = Room(Evening, seenInGroupList: true, closedAt: Evening.AddMinutes(30));
        room.ClosedBy = "list";

        var muchLater = Evening.AddMinutes(30) + InstanceIdentity.ReopensWithin;

        Assert.Null(InstanceIdentity.Match([room], muchLater));
    }

    /// <summary>
    /// Reopening is only ever an undo of the list's own close. A room closed by the time rule has
    /// been quiet for three days, and letting it reopen would undo the split that rule exists for.
    /// </summary>
    [Fact]
    public void OnlyTheListsOwnCloseCanBeUndone()
    {
        var room = Room(Evening, closedAt: Evening.AddMinutes(30));
        room.ClosedBy = "time";

        Assert.Null(InstanceIdentity.Match([room], Evening.AddMinutes(32)));
    }

    /// <summary>
    /// Two open rows at one location means an earlier close was missed. Only the most recently
    /// seen could still be running, so that is the one a new sighting belongs to.
    /// </summary>
    [Fact]
    public void WhenTwoRowsAreOpenTheMostRecentlySeenWins()
    {
        var stale = Room(Evening.AddHours(-6));
        var live = Room(Evening);

        Assert.Same(live, InstanceIdentity.Match([stale, live], Evening.AddMinutes(5)));
        Assert.Same(live, InstanceIdentity.Match([live, stale], Evening.AddMinutes(5)));
    }
}
