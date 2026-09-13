using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>Which facts count as seeing a person, decided by type and never by the id's shape.</summary>
public class UserSightingsTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ABanNamesBothThePersonBannedAndTheModerator()
    {
        var sightings = UserSightings.From(
            FactType.MemberBanned, FactPlatform.VRChat, "usr_target",
            FactPlatform.VRChat, "usr_moderator", At).ToList();

        Assert.Equal(2, sightings.Count);
        Assert.Contains(sightings, s => s.UserId == "usr_target" && s.Reason == RefreshReason.SeenInFactLog);
        Assert.Contains(sightings, s => s.UserId == "usr_moderator" && s.Reason == RefreshReason.SeenInFactLog);
    }

    /// <summary>A presence report is the top tier: somebody is in the instance right now.</summary>
    [Theory]
    [InlineData(FactType.InstanceJoined)]
    [InlineData(FactType.InstancePresenceObserved)]
    [InlineData(FactType.AvatarChanged)]
    public void AClientPresenceReportIsAnInstanceSighting(string type)
    {
        var sighting = Assert.Single(UserSightings.From(type, FactPlatform.VRChat, "usr_a", null, null, At));

        Assert.Equal(RefreshReason.SeenInInstance, sighting.Reason);
    }

    /// <summary>
    /// Spec 3.1.1: a legacy id looks like anything at all. The only safe question is what kind
    /// of fact it is, and the subject of a group-info fact is the group whatever its id looks like.
    /// </summary>
    [Fact]
    public void AGroupInfoFactsSubjectIsNotAPerson_EvenWithAnIdShapedLikeOne()
    {
        var sightings = UserSightings.From(
            FactType.GroupInfoChanged, FactPlatform.VRChat, "usr_looks_like_a_user", null, null, At);

        Assert.Empty(sightings);
    }

    [Fact]
    public void AnInstanceCreatesSubjectIsALocation_ButItsActorIsAPerson()
    {
        var sighting = Assert.Single(UserSightings.From(
            FactType.GroupInstanceCreated, FactPlatform.VRChat, "wrld_x:1234~group(grp_y)",
            FactPlatform.VRChat, "usr_owner", At));

        Assert.Equal("usr_owner", sighting.UserId);
    }

    [Fact]
    public void AnUnrecognisedFactContributesOnlyItsActor()
    {
        var sighting = Assert.Single(UserSightings.From(
            FactType.Unrecognised, FactPlatform.VRChat, "something", FactPlatform.VRChat, "usr_actor", At));

        Assert.Equal("usr_actor", sighting.UserId);
    }

    /// <summary>The profile sync's own facts must not feed the profile sync.</summary>
    [Theory]
    [InlineData(FactType.UserProfileChanged)]
    [InlineData(FactType.UserAgeVerified)]
    [InlineData(FactType.UserAgeFlagCleared)]
    public void ProfileFactsAreNotSightings(string type)
        => Assert.Empty(UserSightings.From(type, FactPlatform.VRChat, "usr_a", FactPlatform.Modbot, "someone", At));

    [Fact]
    public void ADiscordSubjectIsNotAVRChatUser()
        => Assert.Empty(UserSightings.From(FactType.DiscordMemberJoined, FactPlatform.Discord, "1234", null, null, At));
}
