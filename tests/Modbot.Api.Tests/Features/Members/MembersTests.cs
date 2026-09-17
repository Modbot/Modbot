using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Members;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.Members;

/// <summary>
/// The Members and Bans pages' data: the swept lists, joined to the stored profiles, searched and
/// paged on the server.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MembersTests
{
    private const string Group = "grp_test";

    private readonly PostgresFixture _db;

    public MembersTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A group of four, one of whom has left, with two roles and three fetched profiles.</summary>
    private static async Task SeedAsync(ReadSurfaceTestHost host, bool sweptOnce = true)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;
        settings.MemberSweepCompletedAt = sweptOnce ? Day.AddHours(1) : null;
        settings.MemberSweepCount = sweptOnce ? 3 : 0;
        settings.BanSweepCompletedAt = sweptOnce ? Day.AddHours(1) : null;
        settings.BanSweepCount = sweptOnce ? 1 : 0;
        settings.GroupInfoSnapshot = new GroupInfoSnapshot(
            "Test", "TEST", "0001", null, null, "usr_owner", null, null, false, 3, 0,
            [
                new GroupRoleSnapshot("grol_mod", "Moderator", null, 1, true, false, false, false, []),
                new GroupRoleSnapshot("grol_member", "Member", null, 2, false, false, true, true, []),
            ]).ToJson();

        db.GroupMembers.AddRange(
            new GroupMember { GroupId = Group, UserId = "usr_alice", Roles = """["grol_member","grol_mod"]""", JoinedAt = Day.AddDays(-30), MembershipStatus = "member", FirstSeenAt = Day, LastSeenAt = Day },
            new GroupMember { GroupId = Group, UserId = "usr_bob", Roles = """["grol_member"]""", JoinedAt = Day.AddDays(-10), MembershipStatus = "member", FirstSeenAt = Day, LastSeenAt = Day },
            new GroupMember { GroupId = Group, UserId = "8JoV9XEdpo", Roles = """["grol_member"]""", JoinedAt = Day.AddDays(-400), MembershipStatus = "member", FirstSeenAt = Day, LastSeenAt = Day },
            new GroupMember { GroupId = Group, UserId = "usr_gone", Roles = """[]""", JoinedAt = Day.AddDays(-5), MembershipStatus = "member", FirstSeenAt = Day, LastSeenAt = Day, LeftAt = Day.AddHours(1) });

        db.GroupBans.AddRange(
            new GroupBan { GroupId = Group, UserId = "usr_banned", BannedAt = Day.AddDays(-2), FirstSeenAt = Day, LastSeenAt = Day },
            new GroupBan { GroupId = Group, UserId = "usr_forgiven", BannedAt = Day.AddDays(-20), FirstSeenAt = Day, LastSeenAt = Day, LiftedAt = Day.AddHours(1) });

        db.VRChatUsers.AddRange(
            new VRChatUser { UserId = "usr_alice", DisplayName = "Alice Wonder", CurrentAvatarThumbnailImageUrl = "https://img/alice", Is18PlusVerified = true, FirstSeenAt = Day, LastSeenAt = Day, LastRefreshedAt = Day },
            new VRChatUser { UserId = "usr_bob", DisplayName = "Bob_Builder", ProfilePictureUrl = "https://img/bob-override", FirstSeenAt = Day, LastSeenAt = Day.AddHours(2), LastRefreshedAt = Day },
            new VRChatUser { UserId = "usr_banned", DisplayName = "Mallory", FirstSeenAt = Day, LastSeenAt = Day, LastRefreshedAt = Day });

        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ListsCurrentMembersNewestJoinerFirstWithNamesRolesAndPictures()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var list = await host.GetJsonAsync<MemberListResponse>("/api/members", cookie, Ct);

        Assert.Equal(3, list.Total);
        Assert.Equal(["usr_bob", "usr_alice", "8JoV9XEdpo"], list.Members.Select(m => m.UserId));

        var alice = list.Members.Single(m => m.UserId == "usr_alice");
        Assert.Equal("Alice Wonder", alice.DisplayName);
        Assert.Equal(["Member", "Moderator"], alice.RoleNames);
        Assert.Equal("https://img/alice", alice.AvatarThumbnailUrl);
        Assert.True(alice.EighteenPlus);

        // The override picture wins when set, as VRChat's own client does.
        Assert.Equal("https://img/bob-override", list.Members.Single(m => m.UserId == "usr_bob").AvatarThumbnailUrl);

        // A legacy id with no fetched profile: shown by id, nothing invented.
        var legacy = list.Members.Single(m => m.UserId == "8JoV9XEdpo");
        Assert.Null(legacy.DisplayName);
        Assert.Null(legacy.ProfileRefreshedAt);

        Assert.Equal(["grol_member", "grol_mod"], list.Roles.Select(r => r.Id));
        Assert.True(list.Coverage.FirstSweepComplete);
        Assert.Equal(Day.AddHours(1), list.Coverage.LastSyncedAt);
        Assert.Equal(3, list.Coverage.MemberCount);
    }

    [Fact]
    public async Task SearchMatchesTheDisplayNameAndTheIdCaseInsensitivelyAndLiterally()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var byName = await host.GetJsonAsync<MemberListResponse>("/api/members?search=alice", cookie, Ct);
        Assert.Equal(["usr_alice"], byName.Members.Select(m => m.UserId));

        var byId = await host.GetJsonAsync<MemberListResponse>("/api/members?search=9XEd", cookie, Ct);
        Assert.Equal(["8JoV9XEdpo"], byId.Members.Select(m => m.UserId));

        // A percent sign is a percent sign, not a wildcard: "b%b" would match every Bob if it were.
        var literal = await host.GetJsonAsync<MemberListResponse>("/api/members?search=b%25b", cookie, Ct);
        Assert.Empty(literal.Members);

        var underscore = await host.GetJsonAsync<MemberListResponse>("/api/members?search=Bob_", cookie, Ct);
        Assert.Equal(["usr_bob"], underscore.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task TheRoleFilterUsesTheRoleId()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var moderators = await host.GetJsonAsync<MemberListResponse>("/api/members?role=grol_mod", cookie, Ct);

        Assert.Equal(["usr_alice"], moderators.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task SeveralRolesAreAnyOf_NotRoleLeavesPeopleOut_AndNoRoleKeepsTheRoleless()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var either = await host.GetJsonAsync<MemberListResponse>("/api/members?role=grol_mod&role=grol_member", cookie, Ct);
        Assert.Equal(3, either.Total);

        var notMods = await host.GetJsonAsync<MemberListResponse>("/api/members?notRole=grol_mod", cookie, Ct);
        Assert.Equal(["usr_bob", "8JoV9XEdpo"], notMods.Members.Select(m => m.UserId));

        var memberButNotMod = await host.GetJsonAsync<MemberListResponse>(
            "/api/members?role=grol_member&notRole=grol_mod", cookie, Ct);
        Assert.Equal(["usr_bob", "8JoV9XEdpo"], memberButNotMod.Members.Select(m => m.UserId));

        // The only roleless person has left, so the filter finds them only among leavers.
        var roleless = await host.GetJsonAsync<MemberListResponse>("/api/members?noRole=true&status=all", cookie, Ct);
        Assert.Equal(["usr_gone"], roleless.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task TheRoleListCountsWhoHoldsEachRoleNow()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var list = await host.GetJsonAsync<MemberListResponse>("/api/members", cookie, Ct);

        // Three current members hold Member; one holds Moderator; the leaver counts for nothing.
        Assert.Equal(3, list.Roles.Single(r => r.Id == "grol_member").Members);
        Assert.Equal(1, list.Roles.Single(r => r.Id == "grol_mod").Members);
    }

    [Fact]
    public async Task EighteenPlusRepresentingSeenAndProfileFilters()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var verified = await host.GetJsonAsync<MemberListResponse>("/api/members?eighteenPlus=true", cookie, Ct);
        Assert.Equal(["usr_alice"], verified.Members.Select(m => m.UserId));

        var unverified = await host.GetJsonAsync<MemberListResponse>("/api/members?eighteenPlus=false", cookie, Ct);
        Assert.Equal(["usr_bob", "8JoV9XEdpo"], unverified.Members.Select(m => m.UserId));

        var representing = await host.GetJsonAsync<MemberListResponse>("/api/members?representing=true", cookie, Ct);
        Assert.Empty(representing.Members);

        // Bob was seen two hours after noon; Alice at noon; the legacy id has no profile row.
        var seenLately = await host.GetJsonAsync<MemberListResponse>(
            $"/api/members?seenFrom={Uri.EscapeDataString(Day.AddHours(1).ToString("o"))}", cookie, Ct);
        Assert.Equal(["usr_bob"], seenLately.Members.Select(m => m.UserId));

        var fetched = await host.GetJsonAsync<MemberListResponse>("/api/members?profile=fetched", cookie, Ct);
        Assert.Equal(["usr_bob", "usr_alice"], fetched.Members.Select(m => m.UserId));

        var notFetched = await host.GetJsonAsync<MemberListResponse>("/api/members?profile=not-fetched", cookie, Ct);
        Assert.Equal(["8JoV9XEdpo"], notFetched.Members.Select(m => m.UserId));

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/members?profile=maybe", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheStatusFilterShowsWhoLeft()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var left = await host.GetJsonAsync<MemberListResponse>("/api/members?status=left", cookie, Ct);
        Assert.Equal(["usr_gone"], left.Members.Select(m => m.UserId));
        Assert.NotNull(left.Members[0].LeftAt);

        var all = await host.GetJsonAsync<MemberListResponse>("/api/members?status=all", cookie, Ct);
        Assert.Equal(4, all.Total);
    }

    [Fact]
    public async Task PagesAreCountedFromOneAndTheTotalIsTheWholeList()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<MemberListResponse>("/api/members?page=1&pageSize=2", cookie, Ct);
        var second = await host.GetJsonAsync<MemberListResponse>("/api/members?page=2&pageSize=2", cookie, Ct);

        Assert.Equal(3, first.Total);
        Assert.Equal(2, first.Members.Count);
        Assert.Equal(["8JoV9XEdpo"], second.Members.Select(m => m.UserId));
        Assert.Equal(2, second.Page);
    }

    [Fact]
    public async Task SortingByNamePutsPeopleWithNoNameLast()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var list = await host.GetJsonAsync<MemberListResponse>("/api/members?sort=name", cookie, Ct);

        Assert.Equal(["usr_alice", "usr_bob", "8JoV9XEdpo"], list.Members.Select(m => m.UserId));
    }

    /// <summary>Before the first full sweep the list is partial, and the response says so rather than showing a short list as the group.</summary>
    [Fact]
    public async Task BeforeTheFirstSweepTheCoverageSaysSo()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, sweptOnce: false);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var list = await host.GetJsonAsync<MemberListResponse>("/api/members", cookie, Ct);

        Assert.False(list.Coverage.FirstSweepComplete);
        Assert.Null(list.Coverage.LastSyncedAt);
        Assert.Equal(host.Clock.UtcNow, list.Coverage.Now);
    }

    [Fact]
    public async Task MembershipAnswersForAMemberALeaverABannedPersonAndAStranger()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var alice = await host.GetJsonAsync<MembershipView>("/api/members/membership?id=usr_alice", cookie, Ct);
        Assert.True(alice.Known);
        Assert.True(alice.IsMember);
        Assert.Equal(["Member", "Moderator"], alice.RoleNames);
        Assert.Equal(Day.AddDays(-30), alice.JoinedAt);
        Assert.False(alice.Banned);

        var gone = await host.GetJsonAsync<MembershipView>("/api/members/membership?id=usr_gone", cookie, Ct);
        Assert.True(gone.Known);
        Assert.False(gone.IsMember);
        Assert.Equal(Day.AddHours(1), gone.LeftAt);

        var banned = await host.GetJsonAsync<MembershipView>("/api/members/membership?id=usr_banned", cookie, Ct);
        Assert.False(banned.Known);
        Assert.True(banned.Banned);
        Assert.Equal(Day.AddDays(-2), banned.BannedAt);

        var stranger = await host.GetJsonAsync<MembershipView>("/api/members/membership?id=usr_nobody", cookie, Ct);
        Assert.False(stranger.Known);
        Assert.False(stranger.Banned);
    }

    [Fact]
    public async Task TheBanListShowsBansThatStandWithNamesAndSearch()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var list = await host.GetJsonAsync<GroupBanListResponse>("/api/bans", cookie, Ct);
        var row = Assert.Single(list.Bans);
        Assert.Equal("usr_banned", row.UserId);
        Assert.Equal("Mallory", row.DisplayName);
        Assert.Equal(Day.AddDays(-2), row.BannedAt);
        Assert.True(list.Coverage.FirstSweepComplete);
        Assert.Equal(1, list.Coverage.BanCount);

        var lifted = await host.GetJsonAsync<GroupBanListResponse>("/api/bans?status=lifted", cookie, Ct);
        Assert.Equal(["usr_forgiven"], lifted.Bans.Select(b => b.UserId));

        var searched = await host.GetJsonAsync<GroupBanListResponse>("/api/bans?search=mall", cookie, Ct);
        Assert.Equal(["usr_banned"], searched.Bans.Select(b => b.UserId));

        var none = await host.GetJsonAsync<GroupBanListResponse>("/api/bans?search=alice", cookie, Ct);
        Assert.Empty(none.Bans);
    }

    [Fact]
    public async Task MembersNeedViewMembersAndBansNeedViewAuditLog()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var auditOnly = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/members", auditOnly, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/members/membership?id=usr_a", auditOnly, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/bans", auditOnly, Ct)).StatusCode);

        var membersOnly = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/bans", membersOnly, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/members", membersOnly, Ct)).StatusCode);
    }

    [Fact]
    public async Task SignedOutCallersAreRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/members", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/bans", Ct)).StatusCode);
    }

    [Fact]
    public async Task MembershipWithoutAnIdIsABadRequest()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/members/membership?id=", cookie, Ct)).StatusCode);
    }

    /// <summary>
    /// Alice linked and is in the Discord server; Bob linked and has since left it; the legacy id
    /// linked and unlinked, which is not linked.
    /// </summary>
    private static async Task LinkAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.DiscordGuildId = "424242";

        db.DiscordMembers.AddRange(
            new DiscordMember { GuildId = "424242", UserId = "d_alice", Username = "alice", DisplayName = "Alice on Discord", AvatarUrl = "https://cdn/alice.png", FirstSeenAt = Day, UpdatedAt = Day },
            new DiscordMember { GuildId = "424242", UserId = "d_bob", Username = "bob", DisplayName = "Bob on Discord", FirstSeenAt = Day, UpdatedAt = Day, LeftAt = Day.AddDays(-1) });

        db.DiscordAccountLinks.AddRange(
            new DiscordAccountLink { DiscordUserId = "d_alice", DiscordUsername = "alice", VRChatUserId = "usr_alice", LinkedAt = Day },
            new DiscordAccountLink { DiscordUserId = "d_bob", DiscordUsername = "bob", VRChatUserId = "usr_bob", LinkedAt = Day },
            new DiscordAccountLink { DiscordUserId = "d_legacy", DiscordUsername = "legacy", VRChatUserId = "8JoV9XEdpo", LinkedAt = Day.AddDays(-5), UnlinkedAt = Day.AddDays(-4), UnlinkedBy = LinkEndedBy.Member });

        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task TheLinkedFilter_AndEachRowsDiscordAccount_ComeFromActiveLinks()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);
        await LinkAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile, Ct);

        var linked = await host.GetJsonAsync<MemberListResponse>("/api/members?linked=linked", cookie, Ct);
        Assert.Equal(["usr_bob", "usr_alice"], linked.Members.Select(m => m.UserId));
        Assert.Equal(2, linked.Total);

        var alice = linked.Members.Single(m => m.UserId == "usr_alice").LinkedDiscord;
        Assert.NotNull(alice);
        Assert.Equal("d_alice", alice.UserId);
        Assert.Equal("Alice on Discord", alice.Name);
        Assert.Equal("https://cdn/alice.png", alice.AvatarUrl);
        Assert.True(alice.InServer);

        var bob = linked.Members.Single(m => m.UserId == "usr_bob").LinkedDiscord;
        Assert.NotNull(bob);
        Assert.False(bob.InServer);
        Assert.Equal(Day.AddDays(-1), bob.LeftAt);

        var notLinked = await host.GetJsonAsync<MemberListResponse>("/api/members?linked=not-linked", cookie, Ct);
        Assert.Equal(["8JoV9XEdpo"], notLinked.Members.Select(m => m.UserId));
        Assert.Null(notLinked.Members[0].LinkedDiscord);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/members?linked=sometimes", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task Links_OnTheMemberList_NeedViewProfile()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);
        await LinkAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var list = await host.GetJsonAsync<MemberListResponse>("/api/members", cookie, Ct);
        Assert.All(list.Members, m => Assert.Null(m.LinkedDiscord));

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/members?linked=linked", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/members?linked=not-linked", cookie, Ct)).StatusCode);
    }
}
