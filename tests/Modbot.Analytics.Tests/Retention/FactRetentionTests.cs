using Modbot.Analytics.Retention;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Tests.Retention;

/// <summary>
/// Spec 5.5 and 5.9.2: which fact types are worth keeping forever, and which are noise that would
/// bury them.
/// </summary>
/// <remarks>
/// No database. The mapping decides what gets destroyed, so it is worth pinning in its own right.
/// </remarks>
public class FactRetentionTests
{
    [Theory]
    [InlineData(FactType.MemberBanned)]
    [InlineData(FactType.MemberKicked)]
    [InlineData(FactType.MemberJoined)]
    [InlineData(FactType.RoleGranted)]
    [InlineData(FactType.DiscordMemberJoined)]
    [InlineData(FactType.SettingsChanged)]
    [InlineData(FactType.Login)]
    [InlineData(FactType.UserPurged)]
    public void ModerationHistoryIsKept(string type)
        => Assert.Equal(RetentionClass.Moderation, FactRetention.ClassOf(type));

    /// <summary>
    /// The rest of VRChat's group audit log: things people did in the group, kept as long as bans
    /// are. Retention is a prefix test and <c>vrchat.group.</c> is not a presence prefix, which
    /// is what this pins.
    /// </summary>
    [Theory]
    [InlineData(FactType.GroupInstanceKick)]
    [InlineData(FactType.GroupInstanceWarn)]
    [InlineData(FactType.GroupInstanceCreated)]
    [InlineData(FactType.GroupInstanceAnnouncement)]
    [InlineData(FactType.JoinRequestCreated)]
    [InlineData(FactType.JoinRequestRejected)]
    [InlineData(FactType.GroupPostCreated)]
    [InlineData(FactType.CalendarEventCreated)]
    [InlineData(FactType.CalendarEventSeriesDeleted)]
    [InlineData(FactType.RoleUpdated)]
    [InlineData(FactType.GroupInfoChanged)]
    public void GroupAuditLogHistoryIsKept(string type)
        => Assert.Equal(RetentionClass.Moderation, FactRetention.ClassOf(type));

    [Fact]
    public void EverythingFromTheGroupAuditLogIsKeptForever()
    {
        var groupTypes = FactType.All
            .Where(t => t.StartsWith("vrchat.group.", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(groupTypes);
        Assert.All(groupTypes, t => Assert.Equal(RetentionClass.Moderation, FactRetention.ClassOf(t)));
    }

    /// <summary>
    /// The prefix that matters. A moderator kicking somebody out of a group instance is
    /// moderation; a client noticing somebody arrive in one is presence. They must not share a
    /// retention class, and the names are close enough that this is worth a line.
    /// </summary>
    [Fact]
    public void AGroupInstanceKickDoesNotAgeOutLikeAnInstanceJoin()
    {
        Assert.Equal(RetentionClass.Presence, FactRetention.ClassOf(FactType.InstanceJoined));
        Assert.Equal(RetentionClass.Moderation, FactRetention.ClassOf(FactType.GroupInstanceKick));
    }

    [Theory]
    [InlineData(FactType.InstanceJoined)]
    [InlineData(FactType.InstanceLeft)]
    [InlineData(FactType.AvatarChanged)]
    [InlineData(FactType.DiscordVoiceJoined)]
    public void PresenceAgesOut(string type)
        => Assert.Equal(RetentionClass.Presence, FactRetention.ClassOf(type));

    /// <summary>
    /// Spec 5.9.2: "system events take the short retention class deliberately". A sync failure
    /// from last March is not history anyone needs.
    /// </summary>
    [Theory]
    [InlineData(FactType.SyncFailed)]
    [InlineData(FactType.RateLimitColdStop)]
    [InlineData(FactType.WafBlocked)]
    [InlineData(FactType.RetentionPruned)]
    [InlineData(FactType.PartitionCreated)]
    public void OperationalNoiseAgesOut(string type)
        => Assert.Equal(RetentionClass.Presence, FactRetention.ClassOf(type));

    /// <summary>
    /// A type nobody has classified must be kept, not destroyed: too much storage is a bill, and
    /// an early deletion is not recoverable.
    /// </summary>
    [Fact]
    public void AnUnknownTypeIsKeptForever()
        => Assert.Equal(RetentionClass.Moderation, FactRetention.ClassOf("vrchat.something.nobody.declared"));

    [Fact]
    public void EveryTypeBelongsToExactlyOneClass()
    {
        var classified = FactRetention.All.SelectMany(FactRetention.TypesIn).ToList();

        Assert.Equal(FactType.All.Count, classified.Count);
        Assert.Equal(classified.Count, classified.Distinct().Count());
    }
}
