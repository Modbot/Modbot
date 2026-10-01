using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// The Show narrowings on a person's timeline: Moderation only, Presence and Discord.
/// </summary>
public class AuditShowTests
{
    private static IReadOnlyList<string> Every => AuditVisibility.VisibleTypes(ModbotPermissions.Administrator);

    [Fact]
    public void ModerationOnly_IsAllFromTheModerationLog()
    {
        // A moderator who reads only the moderation log must be able to see everything "Moderation
        // only" stands for; a type from the operational log here would be a promise it cannot keep.
        var moderation = Every.Where(t => AuditShow.Includes(AuditShow.Moderation, t)).ToList();

        Assert.NotEmpty(moderation);
        Assert.All(moderation, t => Assert.Equal(AuditCategory.Moderation, AuditVisibility.CategoryOf(t)));
    }

    [Fact]
    public void Presence_IsWhereTheyWereAndWhatTheyLookedLike()
    {
        var presence = Every.Where(t => AuditShow.Includes(AuditShow.Presence, t)).Order().ToList();

        Assert.Equal(
            new[]
            {
                FactType.AvatarChanged,
                FactType.InstanceJoined,
                FactType.InstanceLeft,
                FactType.InstanceLogStopped,
                FactType.InstancePresenceObserved,
            }.Order(),
            presence);
    }

    [Fact]
    public void ArrivalsAreNeverModeration()
    {
        Assert.False(AuditShow.Includes(AuditShow.Moderation, FactType.InstanceJoined));
        Assert.False(AuditShow.Includes(AuditShow.Moderation, FactType.AvatarChanged));
    }

    [Fact]
    public void DiscordIsDiscordsOwnLogAndEveryCopy()
    {
        Assert.True(AuditShow.Includes(AuditShow.Discord, FactType.DiscordMemberBanned));
        Assert.True(AuditShow.Includes(AuditShow.Discord, FactType.CopiedBan));
        Assert.True(AuditShow.Includes(AuditShow.Discord, FactType.CopyFailed));
        Assert.False(AuditShow.Includes(AuditShow.Discord, FactType.MemberBanned));
    }

    [Theory]
    [InlineData("moderation", true)]
    [InlineData("Presence", true)]
    [InlineData(" discord ", true)]
    [InlineData("everything", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheThreeNarrow(string? value, bool narrows)
        => Assert.Equal(narrows, AuditShow.TryParse(value, out _));

    [Fact]
    public void AnythingElseIsEverything()
        => Assert.True(AuditShow.Includes("everything", FactType.Login));
}
