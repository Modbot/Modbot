using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>
/// Route matching on its own (Discord event routes design §3): every filter that is set must
/// match, any listed value inside one filter is enough, people are matched on their own accounts
/// with links adding the other side, and roles are the ones held when the event happened.
/// </summary>
public class EventRouteMatchTests
{
    private const string TeaSpoon = "usr_teaspoon";
    private const string Ray = "usr_ray";
    private const string Stranger = "usr_stranger";

    /// <summary>On Discord only: never linked, no VRChat account Modbot knows of.</summary>
    private const string DiscordOnly = "900000000000000777";

    /// <summary>Jessie's two accounts, linked.</summary>
    private const string JessieVRChat = "usr_jessie";
    private const string JessieDiscord = "900000000000000555";

    private static readonly Guid SamAccount = Guid.Parse("0199c0de-0000-7000-8000-000000000001");
    private static readonly Guid ModeratorRole = Guid.Parse("0199c0de-0000-7000-8000-00000000aaaa");
    private static readonly Guid ViewerRole = Guid.Parse("0199c0de-0000-7000-8000-00000000bbbb");
    private const string SamVRChat = "usr_sam";
    private const string SamDiscord = "900000000000000001";

    private static readonly PeopleDirectory Directory = new(
        [new DirectoryAccount(SamAccount, SamVRChat, SamDiscord, new HashSet<Guid> { ModeratorRole })],
        [(JessieVRChat, JessieDiscord)]);

    private static RoutePeople People(params (long FactId, HeldRoles Roles)[] roles)
        => new(Directory, roles.ToDictionary(r => r.FactId, r => r.Roles));

    private static readonly RoutePeople NoRoles = People();

    private static HeldRoles Held(string[]? subject = null, string[]? actor = null, Guid[]? actorModbot = null)
        => new(subject ?? [], actor ?? [], (actorModbot ?? []).Select(g => g.ToString()).ToList());

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
        string subject = TeaSpoon,
        string? actor = Ray,
        string type = FactType.MemberBanned,
        FactPlatform subjectPlatform = FactPlatform.VRChat,
        FactPlatform? actorPlatform = FactPlatform.VRChat,
        long id = 1)
        => new()
        {
            Id = id,
            Type = type,
            SubjectPlatform = subjectPlatform,
            SubjectId = subject,
            ActorPlatform = actor is null ? null : actorPlatform,
            ActorId = actor,
        };

    [Fact]
    public void TheEventType_MustBeOnTheRoute()
    {
        Assert.True(EventRouteMatch.Matches(Route(), Fact(), NoRoles));
        Assert.False(EventRouteMatch.Matches(Route(), Fact(type: FactType.MemberUnbanned), NoRoles));
    }

    [Fact]
    public void AnAccountType_NeverMatches_EvenWhenTheRouteNamesIt()
    {
        var route = Route(null, FactType.Login, FactType.LoginFailed, FactType.ResetLinkCreated, FactType.DiscordLogPosted);

        Assert.False(EventRouteMatch.Matches(route, Fact(type: FactType.Login), NoRoles));
        Assert.False(EventRouteMatch.Matches(route, Fact(type: FactType.LoginFailed), NoRoles));
        Assert.False(EventRouteMatch.Matches(route, Fact(type: FactType.ResetLinkCreated), NoRoles));
        Assert.False(EventRouteMatch.Matches(route, Fact(type: FactType.DiscordLogPosted), NoRoles));
    }

    /// <summary>
    /// TeaSpoon has a VRChat account and nothing else: no Discord account, no link, no Modbot
    /// account. Every VRChat event about her still matches a filter on her VRChat id.
    /// </summary>
    [Theory]
    [InlineData(FactType.MemberKicked)]
    [InlineData(FactType.MemberBanned)]
    [InlineData(FactType.GroupInstanceWarn)]
    [InlineData(FactType.GroupInstanceKick)]
    [InlineData(FactType.InstanceJoined)]
    [InlineData(FactType.MemberJoined)]
    [InlineData(FactType.UserAgeFlagSet)]
    public void AVRChatOnlyPerson_MatchesEveryVRChatEventAboutThem_WithNoDiscordAccountOrLink(string type)
    {
        var route = Route(r => r.SubjectIds = [TeaSpoon], type);

        Assert.True(EventRouteMatch.Matches(route, Fact(subject: TeaSpoon, type: type), NoRoles));
        Assert.False(EventRouteMatch.Matches(route, Fact(subject: Stranger, type: type), NoRoles));
    }

    [Fact]
    public void ADiscordOnlyPerson_MatchesTheirDiscordEvents_ByDiscordId()
    {
        var route = Route(r => r.SubjectDiscordIds = [DiscordOnly], FactType.DiscordMemberJoined, FactType.DiscordVoiceJoined);

        Assert.True(EventRouteMatch.Matches(route,
            Fact(subject: DiscordOnly, actor: null, type: FactType.DiscordMemberJoined, subjectPlatform: FactPlatform.Discord), NoRoles));
        Assert.True(EventRouteMatch.Matches(route,
            Fact(subject: DiscordOnly, actor: null, type: FactType.DiscordVoiceJoined, subjectPlatform: FactPlatform.Discord), NoRoles));

        // Somebody else on Discord does not match, and a Discord id never matches a VRChat account.
        Assert.False(EventRouteMatch.Matches(route,
            Fact(subject: "900000000000000778", actor: null, type: FactType.DiscordMemberJoined, subjectPlatform: FactPlatform.Discord), NoRoles));
        Assert.False(EventRouteMatch.Matches(Route(r => r.SubjectIds = [DiscordOnly], FactType.DiscordMemberJoined),
            Fact(subject: DiscordOnly, actor: null, type: FactType.DiscordMemberJoined, subjectPlatform: FactPlatform.Discord), NoRoles));
    }

    [Fact]
    public void ALinkedPerson_MatchesEventsOnBothAccounts_WhicheverAccountIsPicked()
    {
        var byVRChat = Route(r => r.SubjectIds = [JessieVRChat], FactType.MemberBanned, FactType.DiscordMemberJoined);
        var byDiscord = Route(r => r.SubjectDiscordIds = [JessieDiscord], FactType.MemberBanned, FactType.DiscordMemberJoined);

        var vrchatBan = Fact(subject: JessieVRChat);
        var discordJoin = Fact(subject: JessieDiscord, actor: null, type: FactType.DiscordMemberJoined, subjectPlatform: FactPlatform.Discord);

        Assert.True(EventRouteMatch.Matches(byVRChat, vrchatBan, NoRoles));
        Assert.True(EventRouteMatch.Matches(byVRChat, discordJoin, NoRoles));
        Assert.True(EventRouteMatch.Matches(byDiscord, vrchatBan, NoRoles));
        Assert.True(EventRouteMatch.Matches(byDiscord, discordJoin, NoRoles));
    }

    [Fact]
    public void ASubjectThatIsNotAPerson_NeverMatchesAPersonFilter_ButMatchesARouteWithout()
    {
        var group = Fact(subject: "grp_1", actor: null, type: FactType.GroupInfoChanged, subjectPlatform: FactPlatform.Modbot);

        Assert.True(EventRouteMatch.Matches(Route(null, FactType.GroupInfoChanged), group, NoRoles));
        Assert.False(EventRouteMatch.Matches(Route(r => r.SubjectIds = ["grp_1"], FactType.GroupInfoChanged), group, NoRoles));
    }

    [Fact]
    public void AnActorFilter_TakesAnyOfItsPeople()
    {
        var route = Route(r =>
        {
            r.ActorIds = [Ray];
            r.ActorDiscordIds = [DiscordOnly];
        });

        Assert.True(EventRouteMatch.Matches(route, Fact(actor: Ray), NoRoles));
        Assert.True(EventRouteMatch.Matches(route, Fact(actor: DiscordOnly, actorPlatform: FactPlatform.Discord), NoRoles));
        Assert.False(EventRouteMatch.Matches(route, Fact(actor: Stranger), NoRoles));
        Assert.False(EventRouteMatch.Matches(route, Fact(actor: null), NoRoles));
    }

    [Fact]
    public void Automatic_MatchesEventsNobodyDid_AloneOrBesidePeople()
    {
        var automaticOnly = Route(r => r.ActorAutomatic = true);
        Assert.True(EventRouteMatch.Matches(automaticOnly, Fact(actor: null), NoRoles));
        Assert.False(EventRouteMatch.Matches(automaticOnly, Fact(actor: Ray), NoRoles));

        var either = Route(r =>
        {
            r.ActorAutomatic = true;
            r.ActorIds = [Ray];
        });
        Assert.True(EventRouteMatch.Matches(either, Fact(actor: null), NoRoles));
        Assert.True(EventRouteMatch.Matches(either, Fact(actor: Ray), NoRoles));
        Assert.False(EventRouteMatch.Matches(either, Fact(actor: Stranger), NoRoles));
    }

    [Fact]
    public void AModbotAccountActor_IsReadAsTheAccountsItStored()
    {
        var fact = Fact(actor: SamAccount.ToString(), type: FactType.ReportCreated, actorPlatform: FactPlatform.Modbot);

        Assert.True(EventRouteMatch.Matches(Route(r => r.ActorIds = [SamVRChat], FactType.ReportCreated), fact, NoRoles));
        Assert.True(EventRouteMatch.Matches(Route(r => r.ActorDiscordIds = [SamDiscord], FactType.ReportCreated), fact, NoRoles));
    }

    [Fact]
    public void SubjectVRChatRoles_AreTheRolesHeldWhenItHappened()
    {
        var route = Route(r => r.SubjectVRChatRoleIds = ["grol_nope", "grol_staff"]);

        Assert.True(EventRouteMatch.Matches(route, Fact(id: 1), People((1, Held(subject: ["grol_member", "grol_staff"])))));
        Assert.False(EventRouteMatch.Matches(route, Fact(id: 1), People((1, Held(subject: ["grol_member"])))));

        // No roles known for the fact reads as no roles.
        Assert.False(EventRouteMatch.Matches(route, Fact(id: 2), NoRoles));
    }

    [Fact]
    public void ActorVRChatRoles_NeedAnActor()
    {
        var route = Route(r => r.ActorVRChatRoleIds = ["grol_staff"]);

        Assert.True(EventRouteMatch.Matches(route, Fact(actor: Ray), People((1, Held(actor: ["grol_staff"])))));
        Assert.False(EventRouteMatch.Matches(route, Fact(actor: Ray), People((1, Held(actor: ["grol_member"])))));
        Assert.False(EventRouteMatch.Matches(route, Fact(actor: null), People((1, Held(actor: ["grol_staff"])))));
    }

    [Fact]
    public void ActorModbotRoles_TakeAnyOfTheRoles()
    {
        var route = Route(r => r.ActorModbotRoleIds = [ViewerRole, ModeratorRole]);
        var fact = Fact(actor: SamVRChat);

        Assert.True(EventRouteMatch.Matches(route, fact, People((1, Held(actorModbot: [ModeratorRole])))));
        Assert.False(EventRouteMatch.Matches(route, fact, People((1, Held(actorModbot: [Guid.NewGuid()])))));
        Assert.False(EventRouteMatch.Matches(route, fact, NoRoles));
    }

    [Fact]
    public void EveryFilterThatIsSet_MustMatch()
    {
        var route = Route(r =>
        {
            r.SubjectIds = [TeaSpoon];
            r.ActorVRChatRoleIds = ["grol_staff"];
            r.ActorModbotRoleIds = [ModeratorRole];
        });

        var both = Held(actor: ["grol_staff"], actorModbot: [ModeratorRole]);

        Assert.True(EventRouteMatch.Matches(route, Fact(subject: TeaSpoon, actor: SamVRChat), People((1, both))));
        Assert.False(EventRouteMatch.Matches(route, Fact(subject: TeaSpoon, actor: SamVRChat), People((1, Held(actor: ["grol_staff"])))));
        Assert.False(EventRouteMatch.Matches(route, Fact(subject: Ray, actor: SamVRChat), People((1, both))));
    }

    [Fact]
    public void AnyRouteForAChannel_IsEnough()
    {
        var bans = Route();
        var kicksByRay = Route(r => r.ActorIds = [Ray], FactType.MemberKicked);

        Assert.True(EventRouteMatch.AnyMatches([bans, kicksByRay], Fact(type: FactType.MemberKicked, actor: Ray), NoRoles));
        Assert.False(EventRouteMatch.AnyMatches([bans, kicksByRay], Fact(type: FactType.MemberKicked, actor: TeaSpoon), NoRoles));
    }
}
