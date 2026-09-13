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
    public void ModerationHistoryIsKept(FactType type)
        => Assert.Equal(RetentionClass.Moderation, FactRetention.ClassOf(type));

    [Theory]
    [InlineData(FactType.InstanceJoined)]
    [InlineData(FactType.InstanceLeft)]
    [InlineData(FactType.AvatarChanged)]
    [InlineData(FactType.DiscordVoiceJoined)]
    public void PresenceAgesOut(FactType type)
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
    public void OperationalNoiseAgesOut(FactType type)
        => Assert.Equal(RetentionClass.Presence, FactRetention.ClassOf(type));

    /// <summary>
    /// A type nobody has classified must be kept, not destroyed: too much storage is a bill, and
    /// an early deletion is not recoverable.
    /// </summary>
    [Fact]
    public void AnUnknownTypeIsKeptForever()
        => Assert.Equal(RetentionClass.Moderation, FactRetention.ClassOf((FactType)9999));

    [Fact]
    public void EveryTypeBelongsToExactlyOneClass()
    {
        var classified = FactRetention.All.SelectMany(FactRetention.TypesIn).ToList();

        Assert.Equal(Enum.GetValues<FactType>().Length, classified.Count);
        Assert.Equal(classified.Count, classified.Distinct().Count());
    }
}
