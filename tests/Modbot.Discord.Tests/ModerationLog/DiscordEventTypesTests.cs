using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>The event types a channel may be sent, and the groups the settings page lists them in.</summary>
public class DiscordEventTypesTests
{
    [Theory]
    [InlineData(FactType.Login)]
    [InlineData(FactType.LoginFailed)]
    [InlineData(FactType.PasswordChanged)]
    [InlineData(FactType.ResetLinkCreated)]
    [InlineData(FactType.ResetLinkUsed)]
    [InlineData(FactType.SignedOutEverywhere)]
    [InlineData(FactType.ContactChanged)]
    [InlineData(FactType.DiscordLogPosted)]
    [InlineData(FactType.MigrationApplied)]
    [InlineData(FactType.PartitionCreated)]
    [InlineData(FactType.RetentionPruned)]
    [InlineData(FactType.EvidenceViewed)]
    [InlineData(FactType.EvidenceDownloaded)]
    [InlineData("modbot.user.login.something-new")]
    [InlineData("modbot.user.password.reset.expire")]
    public void SignInsResetLinksAndPlumbing_AreNeverSendable(string type)
    {
        Assert.False(DiscordEventTypes.CanSend(type));
        Assert.DoesNotContain(type, DiscordEventTypes.Sendable);
        Assert.Empty(DiscordEventTypes.Clean([type]));
    }

    [Fact]
    public void EverythingElseInFactType_IsSendable_SoANewTypeAppearsOnItsOwn()
    {
        Assert.All(FactType.All.Where(DiscordEventTypes.CanSend), t => Assert.Contains(t, DiscordEventTypes.Sendable));
        Assert.Contains(FactType.MemberBanned, DiscordEventTypes.Sendable);
        Assert.Contains(FactType.UserRolesChanged, DiscordEventTypes.Sendable);
        Assert.Contains(FactType.DiscordVoiceJoined, DiscordEventTypes.Sendable);

        // Who put a file on a case file, took it off or destroyed it can be routed; who looked at
        // it or copied it cannot, because a channel has no View evidence gate.
        Assert.Contains(FactType.EvidenceAttached, DiscordEventTypes.Sendable);
        Assert.Contains(FactType.EvidenceDetached, DiscordEventTypes.Sendable);
        Assert.Contains(FactType.EvidenceDestroyed, DiscordEventTypes.Sendable);
        Assert.True(DiscordEventTypes.CanSend("vrchat.group.something.new"));
        Assert.Equal(DiscordEventTypes.Group, DiscordEventTypes.GroupOf("vrchat.group.something.new"));
    }

    [Fact]
    public void EverySendableType_IsInExactlyOneGroup_WithAPlainLabel()
    {
        var listed = DiscordEventTypes.Groups.SelectMany(g => g.Types).ToList();

        Assert.Equal(DiscordEventTypes.Sendable.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        Assert.All(listed, t => Assert.NotEqual(t, FactLabels.For(t)));
    }

    [Theory]
    [InlineData(FactType.MemberBanned, DiscordEventTypes.Moderation)]
    [InlineData(FactType.GroupInstanceWarn, DiscordEventTypes.Moderation)]
    [InlineData(FactType.ReportCreated, DiscordEventTypes.Moderation)]
    [InlineData(FactType.UserAgeFlagSet, DiscordEventTypes.Moderation)]
    [InlineData(FactType.AutoModFlag, DiscordEventTypes.Moderation)]
    [InlineData(FactType.MemberJoined, DiscordEventTypes.Members)]
    [InlineData(FactType.MembersSnapshot, DiscordEventTypes.Members)]
    [InlineData(FactType.RoleGranted, DiscordEventTypes.Members)]
    [InlineData(FactType.UserProfileChanged, DiscordEventTypes.Members)]
    [InlineData(FactType.GroupInstanceCreated, DiscordEventTypes.Instances)]
    [InlineData(FactType.InstanceJoined, DiscordEventTypes.Instances)]
    [InlineData(FactType.GroupPostCreated, DiscordEventTypes.Group)]
    [InlineData(FactType.DiscordMemberJoined, DiscordEventTypes.Discord)]
    [InlineData(FactType.UserRolesChanged, DiscordEventTypes.AccessAndSettings)]
    [InlineData(FactType.SettingsChanged, DiscordEventTypes.AccessAndSettings)]
    [InlineData(FactType.SyncFailed, DiscordEventTypes.Other)]
    public void TypesAreGroupedByTheirName(string type, string group)
        => Assert.Equal(group, DiscordEventTypes.GroupOf(type));

    /// <summary>
    /// The entry that an instance ended on its own follows "Instance closed": it is not offered as a
    /// box of its own, and a route naming "Instance closed" is what takes it.
    /// </summary>
    [Fact]
    public void AnInstanceEndedOnItsOwn_IsNotAChoice_ButFollowsInstanceClosed()
    {
        Assert.DoesNotContain(FactType.InstanceEndedOnItsOwn, DiscordEventTypes.Sendable);
        Assert.Contains(FactType.GroupInstanceClosed, DiscordEventTypes.Sendable);
        Assert.True(DiscordEventTypes.CanSend(FactType.InstanceEndedOnItsOwn));

        Assert.Equal(FactType.GroupInstanceClosed, DiscordEventTypes.RouteTypeOf(FactType.InstanceEndedOnItsOwn));
        Assert.Equal(FactType.MemberBanned, DiscordEventTypes.RouteTypeOf(FactType.MemberBanned));

        Assert.Equal([FactType.GroupInstanceClosed], DiscordEventTypes.Clean([FactType.InstanceEndedOnItsOwn, FactType.GroupInstanceClosed]));
    }

    [Fact]
    public void Clean_KeepsSendableTypesOnce_InListOrder()
    {
        var cleaned = DiscordEventTypes.Clean([FactType.RoleRevoked, " " + FactType.MemberBanned, FactType.Login, "nonsense", FactType.MemberBanned]);

        Assert.Equal([FactType.MemberBanned, FactType.RoleRevoked], cleaned);
    }
}
