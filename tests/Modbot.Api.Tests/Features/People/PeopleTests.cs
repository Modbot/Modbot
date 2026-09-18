using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.People;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.People;

/// <summary>
/// The People page's data: everyone Modbot has a record of, member or not.
/// </summary>
/// <remarks>
/// The member list answers "who is in the group"; this answers "who has Modbot ever seen", which
/// is the bigger question and the one that had no page. The rows most worth pinning are the ones
/// the member list cannot hold: the visitor who was never a member, and the person who left.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class PeopleTests
{
    private const string Group = "grp_test";

    private readonly PostgresFixture _db;

    public PeopleTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Five people: a member, a member who left, a banned stranger, a visitor nobody ever fetched
    /// a profile for, and a legacy id.
    /// </summary>
    private static async Task SeedAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;

        db.GroupMembers.AddRange(
            new GroupMember { GroupId = Group, UserId = "usr_alice", Roles = "[]", JoinedAt = Day.AddDays(-30), FirstSeenAt = Day, LastSeenAt = Day },
            new GroupMember { GroupId = Group, UserId = "usr_gone", Roles = "[]", JoinedAt = Day.AddDays(-5), FirstSeenAt = Day, LastSeenAt = Day, LeftAt = Day.AddHours(1) });

        db.GroupBans.Add(
            new GroupBan { GroupId = Group, UserId = "usr_mallory", BannedAt = Day.AddDays(-2), FirstSeenAt = Day, LastSeenAt = Day });

        db.VRChatUsers.AddRange(
            new VRChatUser
            {
                UserId = "usr_alice",
                DisplayName = "Alice Wonder",
                CurrentAvatarThumbnailImageUrl = "https://img/alice",
                Is18PlusVerified = true,
                TrustRank = TrustRank.KnownUser,
                FirstSeenAt = Day.AddDays(-300),
                LastSeenAt = Day,
                LastRefreshedAt = Day,
            },
            new VRChatUser { UserId = "usr_gone", DisplayName = "Gone Away", FirstSeenAt = Day.AddDays(-200), LastSeenAt = Day.AddHours(-1), LastRefreshedAt = Day },
            new VRChatUser { UserId = "usr_mallory", DisplayName = "Mallory", FirstSeenAt = Day.AddDays(-100), LastSeenAt = Day.AddHours(-2), LastRefreshedAt = Day },
            // Seen once in an instance and never anything else: the whole reason this page exists.
            new VRChatUser { UserId = "usr_visitor", FirstSeenAt = Day.AddDays(-2), LastSeenAt = Day.AddHours(-3) },
            new VRChatUser { UserId = "8JoV9XEdpo", DisplayName = "Old Timer", FirstSeenAt = Day.AddDays(-900), LastSeenAt = Day.AddHours(-4), LastRefreshedAt = Day });

        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ListsEveryoneModbotHasARecordOfMostRecentlySeenFirst()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var list = await host.GetJsonAsync<PeopleListResponse>("/api/people", cookie, Ct);

        Assert.Equal(5, list.Total);
        Assert.Equal(
            ["usr_alice", "usr_gone", "usr_mallory", "usr_visitor", "8JoV9XEdpo"],
            list.People.Select(p => p.UserId));

        Assert.Equal(5, list.Coverage.Known);
        Assert.Equal(1, list.Coverage.Members);

        var alice = list.People.Single(p => p.UserId == "usr_alice");
        Assert.Equal("Alice Wonder", alice.DisplayName);
        Assert.Equal("https://img/alice", alice.AvatarThumbnailUrl);
        Assert.True(alice.EighteenPlus);
        Assert.Equal(TrustRank.KnownUser, alice.TrustRank);
        Assert.True(alice.IsMember);
        Assert.Null(alice.LeftAt);
        Assert.False(alice.Banned);
        Assert.Equal(Day.AddDays(-300), alice.FirstSeenAt);

        // Somebody the member list would never show: never a member, no profile fetched yet.
        var visitor = list.People.Single(p => p.UserId == "usr_visitor");
        Assert.False(visitor.IsMember);
        Assert.Null(visitor.LeftAt);
        Assert.Null(visitor.DisplayName);
        Assert.Null(visitor.ProfileRefreshedAt);
    }

    [Fact]
    public async Task SaysWhereEachPersonStandsWithTheGroup()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var list = await host.GetJsonAsync<PeopleListResponse>("/api/people", cookie, Ct);

        var left = list.People.Single(p => p.UserId == "usr_gone");
        Assert.False(left.IsMember);
        Assert.Equal(Day.AddHours(1), left.LeftAt);

        var banned = list.People.Single(p => p.UserId == "usr_mallory");
        Assert.True(banned.Banned);
        Assert.False(banned.IsMember);
    }

    [Fact]
    public async Task SearchMatchesTheDisplayNameAndTheIdCaseInsensitivelyAndLiterally()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var byName = await host.GetJsonAsync<PeopleListResponse>("/api/people?search=alice", cookie, Ct);
        Assert.Equal(["usr_alice"], byName.People.Select(p => p.UserId));

        // A legacy id follows no format, so search has to reach the id itself (spec 3.1.1).
        var byId = await host.GetJsonAsync<PeopleListResponse>("/api/people?search=9XEd", cookie, Ct);
        Assert.Equal(["8JoV9XEdpo"], byId.People.Select(p => p.UserId));

        // Somebody with no fetched name is still found by their id.
        var visitor = await host.GetJsonAsync<PeopleListResponse>("/api/people?search=visitor", cookie, Ct);
        Assert.Equal(["usr_visitor"], visitor.People.Select(p => p.UserId));

        // A percent sign is a percent sign, not a wildcard.
        var literal = await host.GetJsonAsync<PeopleListResponse>("/api/people?search=a%25e", cookie, Ct);
        Assert.Empty(literal.People);
    }

    [Fact]
    public async Task TheMembershipFilterSeparatesMembersFromEverybodyElse()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var members = await host.GetJsonAsync<PeopleListResponse>("/api/people?membership=member", cookie, Ct);
        Assert.Equal(["usr_alice"], members.People.Select(p => p.UserId));

        var strangers = await host.GetJsonAsync<PeopleListResponse>("/api/people?membership=not-member", cookie, Ct);
        Assert.Equal(["usr_gone", "usr_mallory", "usr_visitor", "8JoV9XEdpo"], strangers.People.Select(p => p.UserId));

        var left = await host.GetJsonAsync<PeopleListResponse>("/api/people?membership=left", cookie, Ct);
        Assert.Equal(["usr_gone"], left.People.Select(p => p.UserId));

        var banned = await host.GetJsonAsync<PeopleListResponse>("/api/people?banned=true", cookie, Ct);
        Assert.Equal(["usr_mallory"], banned.People.Select(p => p.UserId));

        var unfetched = await host.GetJsonAsync<PeopleListResponse>("/api/people?profile=not-fetched", cookie, Ct);
        Assert.Equal(["usr_visitor"], unfetched.People.Select(p => p.UserId));
    }

    [Fact]
    public async Task SortsByNameAndByHowLongTheyHaveBeenKnown()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        // Nobody has fetched the visitor's name, so they sort last rather than first.
        var byName = await host.GetJsonAsync<PeopleListResponse>("/api/people?sort=name", cookie, Ct);
        Assert.Equal(
            ["usr_alice", "usr_gone", "usr_mallory", "8JoV9XEdpo", "usr_visitor"],
            byName.People.Select(p => p.UserId));

        var longestKnown = await host.GetJsonAsync<PeopleListResponse>("/api/people?sort=known", cookie, Ct);
        Assert.Equal("8JoV9XEdpo", longestKnown.People[0].UserId);
        Assert.Equal("usr_visitor", longestKnown.People[^1].UserId);
    }

    [Fact]
    public async Task PagingCountsTheWholeListAndHandsBackOnePageOfIt()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var first = await host.GetJsonAsync<PeopleListResponse>("/api/people?pageSize=2", cookie, Ct);
        Assert.Equal(5, first.Total);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, first.PageSize);
        Assert.Equal(["usr_alice", "usr_gone"], first.People.Select(p => p.UserId));

        var second = await host.GetJsonAsync<PeopleListResponse>("/api/people?pageSize=2&page=2", cookie, Ct);
        Assert.Equal(2, second.Page);
        Assert.Equal(["usr_mallory", "usr_visitor"], second.People.Select(p => p.UserId));

        var last = await host.GetJsonAsync<PeopleListResponse>("/api/people?pageSize=2&page=3", cookie, Ct);
        Assert.Equal(["8JoV9XEdpo"], last.People.Select(p => p.UserId));

        // Past the end is an empty page, not an error, and the total still says how many there are.
        var past = await host.GetJsonAsync<PeopleListResponse>("/api/people?pageSize=2&page=9", cookie, Ct);
        Assert.Empty(past.People);
        Assert.Equal(5, past.Total);

        // A page size nobody should be asking for is clamped rather than served.
        var huge = await host.GetJsonAsync<PeopleListResponse>("/api/people?pageSize=100000", cookie, Ct);
        Assert.Equal(PeopleEndpoints.MaxPageSize, huge.PageSize);
    }

    [Fact]
    public async Task ItNeedsSeeProfilesAndSeeMembersIsNotEnough()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var membersOnly = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/people", membersOnly, Ct)).StatusCode);

        var profiles = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/people", profiles, Ct)).StatusCode);
    }

    [Fact]
    public async Task AFilterValueTheEndpointDoesNotKnowIsRefusedRatherThanIgnored()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.GetAsync("/api/people?membership=everyone", cookie, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.GetAsync("/api/people?profile=maybe", cookie, Ct)).StatusCode);
    }
}
