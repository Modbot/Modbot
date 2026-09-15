using Modbot.Core.Data.Entities;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>
/// Route matching on its own (Discord event routes design §3): every filter that is set must
/// match, any listed value inside one filter is enough, and people are read across platforms.
/// </summary>
public class EventRouteMatchTests
{
    private const string Jessie = "usr_jessie";
    private const string Ray = "usr_ray";
    private const string Stranger = "usr_stranger";

    private static readonly Guid SamAccount = Guid.Parse("0199c0de-0000-7000-8000-000000000001");
    private static readonly Guid ModeratorRole = Guid.Parse("0199c0de-0000-7000-8000-00000000aaaa");
    private static readonly Guid ViewerRole = Guid.Parse("0199c0de-0000-7000-8000-00000000bbbb");
    private const string SamVRChat = "usr_sam";
    private const string SamDiscord = "900000000000000001";

    private static readonly RoutePeople People = new(
        [new RouteAccount(SamAccount, SamVRChat, SamDiscord, new HashSet<Guid> { ModeratorRole })],
        new Dictionary<string, IReadOnlySet<string>>
        {
            [Jessie] = new HashSet<string> { "grol_member" },
            [Ray] = new HashSet<string> { "grol_member", "grol_staff" },
            [SamVRChat] = new HashSet<string> { "grol_staff" },
        });

    private static DiscordEventRoute Route(Action<DiscordEventRoute>? more = null, params string[] types)
    {
        var route = new DiscordEventRoute
        {
            Id = Guid.NewGuid(),
            ChannelId = "1",
            EventTypes = types.Length == 0 ? [FactType.MemberBanned] : [.. types],
        };
        more?.Invoke(route);
        return route;
    }

    private static ModbotEvent Fact(
        string subject = Jessie,
        string? actor = Ray,
        string type = FactType.MemberBanned,
        FactPlatform subjectPlatform = FactPlatform.VRChat,
        FactPlatform? actorPlatform = FactPlatform.VRChat)
        => new()
        {
            Id = 1,
            Type = type,
            SubjectPlatform = subjectPlatform,
            SubjectId = subject,
            ActorPlatform = actor is null ? null : actorPlatform,
            ActorId = actor,
        };

    [Fact]
    public void TheEventType_MustBeOnTheRoute()
    {
        Assert.True(EventRouteMatch.Matches(Route(), Fact(), People));
        Assert.False(EventRouteMatch.Matches(Route(), Fact(type: FactType.MemberUnbanned), People));
    }

    [Fact]
    public void AnAccountType_NeverMatches_EvenWhenTheRouteNamesIt()
    {
        var route = Route(null, FactType.Login, FactType.LoginFailed, FactType.ResetLinkCreated, FactType.DiscordLogPosted);

        Assert.False(EventRouteMatch.Matches(route, Fact(type: FactType.Login), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(type: FactType.LoginFailed), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(type: FactType.ResetLinkCreated), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(type: FactType.DiscordLogPosted), People));
    }

    [Fact]
    public void ASubjectFilter_TakesAnyOfItsPeople()
    {
        var route = Route(r => r.SubjectIds = [Stranger, Jessie]);

        Assert.True(EventRouteMatch.Matches(route, Fact(subject: Jessie), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(subject: Ray), People));
    }

    [Fact]
    public void ASubjectThatIsNotAPerson_NeverMatchesAPersonFilter_ButMatchesARouteWithout()
    {
        var group = Fact(subject: "grp_1", actor: null, type: FactType.GroupInfoChanged);

        Assert.True(EventRouteMatch.Matches(Route(null, FactType.GroupInfoChanged), group, People));
        Assert.False(EventRouteMatch.Matches(Route(r => r.SubjectIds = ["grp_1"], FactType.GroupInfoChanged),
            Fact(subject: "grp_1", actor: null, type: FactType.GroupInfoChanged, subjectPlatform: FactPlatform.Modbot), People));
    }

    [Fact]
    public void AnActorFilter_TakesAnyOfItsPeople()
    {
        var route = Route(r => r.ActorIds = [Ray]);

        Assert.True(EventRouteMatch.Matches(route, Fact(actor: Ray), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(actor: Stranger), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(actor: null), People));
    }

    [Fact]
    public void Automatic_MatchesEventsNobodyDid_AloneOrBesidePeople()
    {
        var automaticOnly = Route(r => r.ActorAutomatic = true);
        Assert.True(EventRouteMatch.Matches(automaticOnly, Fact(actor: null), People));
        Assert.False(EventRouteMatch.Matches(automaticOnly, Fact(actor: Ray), People));

        var either = Route(r =>
        {
            r.ActorAutomatic = true;
            r.ActorIds = [Ray];
        });
        Assert.True(EventRouteMatch.Matches(either, Fact(actor: null), People));
        Assert.True(EventRouteMatch.Matches(either, Fact(actor: Ray), People));
        Assert.False(EventRouteMatch.Matches(either, Fact(actor: Stranger), People));
    }

    [Fact]
    public void AModbotAccountActor_IsReadAsTheVRChatAccountItLinked()
    {
        var route = Route(r => r.ActorIds = [SamVRChat], FactType.ReportCreated);
        var fact = Fact(actor: SamAccount.ToString(), type: FactType.ReportCreated, actorPlatform: FactPlatform.Modbot);

        Assert.True(EventRouteMatch.Matches(route, fact, People));
    }

    [Fact]
    public void ADiscordSubject_IsReadThroughTheAccountThatStoredItsId()
    {
        var route = Route(r => r.SubjectIds = [SamVRChat], FactType.DiscordMemberJoined);

        Assert.True(EventRouteMatch.Matches(route,
            Fact(subject: SamDiscord, actor: null, type: FactType.DiscordMemberJoined, subjectPlatform: FactPlatform.Discord), People));
        Assert.False(EventRouteMatch.Matches(route,
            Fact(subject: "900000000000000002", actor: null, type: FactType.DiscordMemberJoined, subjectPlatform: FactPlatform.Discord), People));
    }

    [Fact]
    public void SubjectVRChatRoles_TakeAnyOfTheRoles_AndNobodyForANonMember()
    {
        var route = Route(r => r.SubjectVRChatRoleIds = ["grol_nope", "grol_staff"]);

        Assert.True(EventRouteMatch.Matches(route, Fact(subject: Ray), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(subject: Jessie), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(subject: Stranger), People));
    }

    [Fact]
    public void ActorVRChatRoles_NeedAnActor()
    {
        var route = Route(r => r.ActorVRChatRoleIds = ["grol_staff"]);

        Assert.True(EventRouteMatch.Matches(route, Fact(actor: Ray), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(actor: Jessie), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(actor: null), People));

        // Through the account: Sam's Modbot account is linked to a VRChat account with staff.
        Assert.True(EventRouteMatch.Matches(route,
            Fact(actor: SamAccount.ToString(), actorPlatform: FactPlatform.Modbot), People));
    }

    [Fact]
    public void ActorModbotRoles_FindTheAccountOnEveryPlatform()
    {
        var route = Route(r => r.ActorModbotRoleIds = [ViewerRole, ModeratorRole]);

        Assert.True(EventRouteMatch.Matches(route, Fact(actor: SamAccount.ToString(), actorPlatform: FactPlatform.Modbot), People));
        Assert.True(EventRouteMatch.Matches(route, Fact(actor: SamVRChat), People));
        Assert.True(EventRouteMatch.Matches(route, Fact(actor: SamDiscord, actorPlatform: FactPlatform.Discord), People));

        Assert.False(EventRouteMatch.Matches(route, Fact(actor: Ray), People));
        Assert.False(EventRouteMatch.Matches(route, Fact(actor: null), People));
        Assert.False(EventRouteMatch.Matches(Route(r => r.ActorModbotRoleIds = [ViewerRole]), Fact(actor: SamVRChat), People));
    }

    [Fact]
    public void EveryFilterThatIsSet_MustMatch()
    {
        var route = Route(r =>
        {
            r.SubjectIds = [Jessie];
            r.ActorVRChatRoleIds = ["grol_staff"];
            r.ActorModbotRoleIds = [ModeratorRole];
        });

        // Sam is staff in the group and a moderator in Modbot.
        Assert.True(EventRouteMatch.Matches(route, Fact(subject: Jessie, actor: SamVRChat), People));

        // Ray is staff in the group but has no Modbot account.
        Assert.False(EventRouteMatch.Matches(route, Fact(subject: Jessie, actor: Ray), People));

        // Right actor, wrong subject.
        Assert.False(EventRouteMatch.Matches(route, Fact(subject: Ray, actor: SamVRChat), People));
    }

    [Fact]
    public void AnyRouteForAChannel_IsEnough()
    {
        var bans = Route();
        var kicksByRay = Route(r => r.ActorIds = [Ray], FactType.MemberKicked);

        Assert.True(EventRouteMatch.AnyMatches([bans, kicksByRay], Fact(type: FactType.MemberKicked, actor: Ray), People));
        Assert.False(EventRouteMatch.AnyMatches([bans, kicksByRay], Fact(type: FactType.MemberKicked, actor: Jessie), People));
    }
}
