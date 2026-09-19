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

        db.GroupBans.AddRange(
            new GroupBan { GroupId = Group, UserId = "usr_mallory", BannedAt = Day.AddDays(-2), FirstSeenAt = Day, LastSeenAt = Day },
            // Banned once and let back in: off the ban list, still somebody the group has banned.
            new GroupBan
            {
                GroupId = Group,
                UserId = "usr_gone",
                BannedAt = Day.AddDays(-40),
                LiftedAt = Day.AddDays(-20),
                FirstSeenAt = Day,
                LastSeenAt = Day,
            });

        db.DiscordAccountLinks.Add(new DiscordAccountLink
        {
            DiscordUserId = "111",
            DiscordUsername = "alice",
            VRChatUserId = "usr_alice",
            LinkedAt = Day.AddDays(-10),
        });

        db.ModerationFlags.Add(new ModerationFlag
        {
            Id = Guid.CreateVersion7(),
            FlaggedAt = Day.AddDays(-2),
            RuleName = "Slurs",
            RuleVersion = 1,
            Target = "discordMessage",
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_mallory",
            Matched = "a word",
        });

        db.VRChatUsers.AddRange(
            new VRChatUser
            {
                UserId = "usr_alice",
                DisplayName = "Alice Wonder",
                CurrentAvatarThumbnailImageUrl = "https://img/alice",
                Is18PlusVerified = true,
                TrustRank = TrustRank.KnownUser,
                LastPlatform = "standalonewindows",
                FirstSeenAt = Day.AddDays(-300),
                LastSeenAt = Day,
                LastRefreshedAt = Day,
            },
            new VRChatUser
            {
                UserId = "usr_gone",
                DisplayName = "Gone Away",
                TrustRank = TrustRank.Visitor,
                LastPlatform = "android",
                FirstSeenAt = Day.AddDays(-200),
                LastSeenAt = Day.AddHours(-1),
                LastRefreshedAt = Day,
            },
            new VRChatUser
            {
                UserId = "usr_mallory",
                DisplayName = "Mallory",
                TrustRank = TrustRank.Nuisance,
                // As VRChat sent it: the column is free text and the filter matches it as it is.
                LastPlatform = "StandaloneWindows",
                FirstSeenAt = Day.AddDays(-100),
                LastSeenAt = Day.AddHours(-2),
                LastRefreshedAt = Day,
            },
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
    public async Task EverBannedFindsPeopleTheBanListNoLongerHolds()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        // The one whose ban was lifted is off the ban list and still someone the group has banned,
        // which is the whole difference between the two questions.
        var ever = await host.GetJsonAsync<PeopleListResponse>("/api/people?everBanned=true", cookie, Ct);
        Assert.Equal(["usr_gone", "usr_mallory"], ever.People.Select(p => p.UserId).Order());

        var onTheList = await host.GetJsonAsync<PeopleListResponse>("/api/people?banned=true", cookie, Ct);
        Assert.Equal(["usr_mallory"], onTheList.People.Select(p => p.UserId));

        var never = await host.GetJsonAsync<PeopleListResponse>("/api/people?everBanned=false", cookie, Ct);
        Assert.DoesNotContain("usr_gone", never.People.Select(p => p.UserId));
        Assert.Contains("usr_visitor", never.People.Select(p => p.UserId));
    }

    [Fact]
    public async Task TheTrustRankFilterTakesSeveralRanksAndLeavesOutAnybodyNobodyHasRead()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var one = await host.GetJsonAsync<PeopleListResponse>("/api/people?trustRank=Nuisance", cookie, Ct);
        Assert.Equal(["usr_mallory"], one.People.Select(p => p.UserId));

        var either = await host.GetJsonAsync<PeopleListResponse>(
            "/api/people?trustRank=Nuisance&trustRank=KnownUser", cookie, Ct);
        Assert.Equal(["usr_alice", "usr_mallory"], either.People.Select(p => p.UserId).Order());

        // Nobody has read the visitor's or the legacy account's tags, so neither has a rank at all
        // and asking for Visitor must not sweep them in.
        var visitors = await host.GetJsonAsync<PeopleListResponse>("/api/people?trustRank=Visitor", cookie, Ct);
        Assert.Equal(["usr_gone"], visitors.People.Select(p => p.UserId));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.GetAsync("/api/people?trustRank=SuperUser", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task ThePlatformFilterMatchesWhateverVRChatSentWhateverItsCapitals()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var pc = await host.GetJsonAsync<PeopleListResponse>("/api/people?platform=standalonewindows", cookie, Ct);
        Assert.Equal(["usr_alice", "usr_mallory"], pc.People.Select(p => p.UserId).Order());

        var both = await host.GetJsonAsync<PeopleListResponse>(
            "/api/people?platform=android&platform=ios", cookie, Ct);
        Assert.Equal(["usr_gone"], both.People.Select(p => p.UserId));

        // Free text on the wire, so a value this build has no word for is a value nobody matches,
        // never an error.
        var unknown = await host.GetJsonAsync<PeopleListResponse>("/api/people?platform=holodeck", cookie, Ct);
        Assert.Empty(unknown.People);
    }

    [Fact]
    public async Task TheDiscordTheAgeMarkAndTheFlagFiltersEachNarrowTheList()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var linked = await host.GetJsonAsync<PeopleListResponse>("/api/people?linked=linked", cookie, Ct);
        Assert.Equal(["usr_alice"], linked.People.Select(p => p.UserId));

        var notLinked = await host.GetJsonAsync<PeopleListResponse>("/api/people?linked=not-linked", cookie, Ct);
        Assert.DoesNotContain("usr_alice", notLinked.People.Select(p => p.UserId));
        Assert.Equal(4, notLinked.Total);

        var marked = await host.GetJsonAsync<PeopleListResponse>("/api/people?eighteenPlus=true", cookie, Ct);
        Assert.Equal(["usr_alice"], marked.People.Select(p => p.UserId));

        var unmarked = await host.GetJsonAsync<PeopleListResponse>("/api/people?eighteenPlus=false", cookie, Ct);
        Assert.Equal(4, unmarked.Total);

        var flagged = await host.GetJsonAsync<PeopleListResponse>("/api/people?flagged=true", cookie, Ct);
        Assert.Equal(["usr_mallory"], flagged.People.Select(p => p.UserId));

        var clean = await host.GetJsonAsync<PeopleListResponse>("/api/people?flagged=false", cookie, Ct);
        Assert.DoesNotContain("usr_mallory", clean.People.Select(p => p.UserId));

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.GetAsync("/api/people?linked=maybe", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheLastSeenStretchIsHalfOpenSoTheDayItEndsOnIsNotCountedTwice()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        // Everybody Modbot last saw in the two hours before noon: Alice at noon, Gone at 11, and
        // Mallory at 10 is the first one outside it.
        var from = Uri.EscapeDataString(Day.AddHours(-1).ToString("O"));
        var recent = await host.GetJsonAsync<PeopleListResponse>($"/api/people?seenFrom={from}", cookie, Ct);
        Assert.Equal(["usr_alice", "usr_gone"], recent.People.Select(p => p.UserId));

        var to = Uri.EscapeDataString(Day.AddHours(-2).ToString("O"));
        var older = await host.GetJsonAsync<PeopleListResponse>($"/api/people?seenTo={to}", cookie, Ct);
        Assert.Equal(["usr_visitor", "8JoV9XEdpo"], older.People.Select(p => p.UserId));

        var between = await host.GetJsonAsync<PeopleListResponse>(
            $"/api/people?seenFrom={to}&seenTo={from}", cookie, Ct);
        Assert.Equal(["usr_mallory"], between.People.Select(p => p.UserId));
    }

    [Fact]
    public async Task FiltersCombineWithAndRatherThanWideningTheList()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var both = await host.GetJsonAsync<PeopleListResponse>(
            "/api/people?membership=not-member&platform=standalonewindows", cookie, Ct);
        Assert.Equal(["usr_mallory"], both.People.Select(p => p.UserId));

        var none = await host.GetJsonAsync<PeopleListResponse>(
            "/api/people?membership=member&flagged=true", cookie, Ct);
        Assert.Empty(none.People);
        Assert.Equal(0, none.Total);

        // The counts above the list are the whole table either way: they say what there is, not
        // what the filters left.
        Assert.Equal(5, none.Coverage.Known);
        Assert.Equal(1, none.Coverage.Members);
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
