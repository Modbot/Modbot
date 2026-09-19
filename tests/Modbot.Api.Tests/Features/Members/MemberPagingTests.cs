using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Members;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.Members;

/// <summary>
/// The member list and the group's ban list read a numbered page at a time.
/// </summary>
/// <remarks>
/// <para>
/// What a numbered page rests on is that the ordering is total: every row has a place, and the
/// same read twice puts them in the same places. Each of these orderings ends in the user id,
/// which is unique in the list, so two people who joined in the same second cannot swap between
/// one page and the next. Take that tie-break away and the pages stop joining up even on a list
/// nobody is writing to, which is the case these tests pin down.
/// </para>
/// <para>
/// They also cover the edges a page number has and a cursor did not: a page past the end of the
/// list, and a page number nobody could have meant.
/// </para>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class MemberPagingTests
{
    private const string Group = "grp_test";

    private readonly PostgresFixture _db;

    public MemberPagingTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static async Task SeedAsync(
        ReadSurfaceTestHost host, params (string Id, DateTimeOffset? Joined, string? Name)[] people)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;
        settings.MemberSweepCompletedAt = Day.AddHours(1);
        settings.MemberSweepCount = people.Length;

        foreach (var (id, joined, name) in people)
        {
            db.GroupMembers.Add(new GroupMember
            {
                GroupId = Group,
                UserId = id,
                Roles = "[]",
                JoinedAt = joined,
                MembershipStatus = "member",
                FirstSeenAt = Day,
                LastSeenAt = Day,
            });

            if (name is not null)
            {
                db.VRChatUsers.Add(new VRChatUser
                {
                    UserId = id,
                    DisplayName = name,
                    TrustRank = TrustRank.KnownUser,
                    FirstSeenAt = Day,
                    LastSeenAt = Day.AddDays(-people.Length),
                    LastRefreshedAt = Day,
                });
            }
        }

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Six members a day apart, so the newest-first order is f, e, d, c, b, a.</summary>
    private static (string, DateTimeOffset?, string?)[] SixADayApart() =>
    [
        ("usr_a", Day.AddDays(-6), "Ada"),
        ("usr_b", Day.AddDays(-5), "Bo"),
        ("usr_c", Day.AddDays(-4), "Cy"),
        ("usr_d", Day.AddDays(-3), "Di"),
        ("usr_e", Day.AddDays(-2), "Eve"),
        ("usr_f", Day.AddDays(-1), "Fay"),
    ];

    /// <summary>Everybody joining at one instant, so only the tie-break decides the order.</summary>
    private static (string, DateTimeOffset?, string?)[] SixAtOnce() =>
    [
        ("usr_a", Day, "Ada"),
        ("usr_b", Day, "Bo"),
        ("usr_c", Day, "Cy"),
        ("usr_d", Day, "Di"),
        ("usr_e", Day, "Eve"),
        ("usr_f", Day, "Fay"),
    ];

    private static async Task<List<string>> WalkAsync(ReadSurfaceTestHost host, string cookie, string sort, int size)
    {
        var read = new List<string>();

        var first = await host.GetJsonAsync<MemberListResponse>(
            $"/api/members?sort={sort}&page=1&pageSize={size}", cookie, Ct);

        var pages = (first.Total + size - 1) / size;
        read.AddRange(first.Members.Select(m => m.UserId));

        for (var page = 2; page <= pages; page++)
        {
            var next = await host.GetJsonAsync<MemberListResponse>(
                $"/api/members?sort={sort}&page={page}&pageSize={size}", cookie, Ct);

            Assert.Equal(page, next.Page);
            read.AddRange(next.Members.Select(m => m.UserId));
        }

        return read;
    }

    [Theory]
    [InlineData("joined")]
    [InlineData("name")]
    [InlineData("seen")]
    public async Task ThePagesJoinUpInEveryOrderTheListOffers(string sort)
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var whole = await WalkAsync(host, cookie, sort, 6);
        var inTwos = await WalkAsync(host, cookie, sort, 2);

        Assert.Equal(6, whole.Count);
        Assert.Equal(whole, inTwos);
        Assert.Equal(whole.Distinct(), whole);
    }

    [Fact]
    public async Task EverybodyJoiningAtOnceStillPagesThroughWithoutRepeatingAnybody()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixAtOnce());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        // The join dates are all equal, so the user id is the only thing deciding the order.
        var read = await WalkAsync(host, cookie, "joined", 2);

        Assert.Equal(["usr_a", "usr_b", "usr_c", "usr_d", "usr_e", "usr_f"], read);
    }

    [Fact]
    public async Task PeopleWithNoJoinDatePageAtTheEndOfTheList()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(
            host,
            ("usr_a", Day.AddDays(-2), "Ada"),
            ("usr_b", Day.AddDays(-1), "Bo"),
            ("usr_nodate", null, "Cy"),
            ("usr_noprofile", null, null));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var read = await WalkAsync(host, cookie, "joined", 2);

        Assert.Equal(["usr_b", "usr_a", "usr_nodate", "usr_noprofile"], read);
    }

    [Fact]
    public async Task APagePastTheEndIsEmptyAndStillSaysHowBigTheListIs()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var far = await host.GetJsonAsync<MemberListResponse>("/api/members?page=40&pageSize=2", cookie, Ct);

        Assert.Empty(far.Members);
        Assert.Equal(6, far.Total);
        Assert.Equal(40, far.Page);
        Assert.Equal(2, far.PageSize);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-3")]
    [InlineData("")]
    public async Task APageNumberNobodyCouldHaveMeantIsTheFirstPage(string asked)
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var list = await host.GetJsonAsync<MemberListResponse>(
            $"/api/members?pageSize=2{(asked.Length > 0 ? "&" + asked : "")}", cookie, Ct);

        Assert.Equal(1, list.Page);
        Assert.Equal(["usr_f", "usr_e"], list.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task APageBiggerThanTheListWillServeIsCutDownToWhatItWill()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var list = await host.GetJsonAsync<MemberListResponse>("/api/members?pageSize=100000", cookie, Ct);

        Assert.Equal(MemberEndpoints.MaxPageSize, list.PageSize);
    }

    [Fact]
    public async Task TheGroupBanListPagesByNumberTheSameWay()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            var settings = await db.GetSettingsAsync(Ct);
            settings.ManagedGroupId = Group;
            settings.BanSweepCompletedAt = Day.AddHours(1);
            settings.BanSweepCount = 4;

            // Two pairs sharing a ban date, so both page boundaries land on a tie.
            db.GroupBans.AddRange(
                new GroupBan { GroupId = Group, UserId = "usr_a", BannedAt = Day.AddDays(-2), FirstSeenAt = Day, LastSeenAt = Day },
                new GroupBan { GroupId = Group, UserId = "usr_b", BannedAt = Day.AddDays(-2), FirstSeenAt = Day, LastSeenAt = Day },
                new GroupBan { GroupId = Group, UserId = "usr_c", BannedAt = Day.AddDays(-1), FirstSeenAt = Day, LastSeenAt = Day },
                new GroupBan { GroupId = Group, UserId = "usr_d", BannedAt = Day.AddDays(-1), FirstSeenAt = Day, LastSeenAt = Day });

            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var first = await host.GetJsonAsync<GroupBanListResponse>("/api/bans?page=1&pageSize=2", cookie, Ct);
        var second = await host.GetJsonAsync<GroupBanListResponse>("/api/bans?page=2&pageSize=2", cookie, Ct);

        Assert.Equal(4, first.Total);
        Assert.Equal(["usr_c", "usr_d"], first.Bans.Select(b => b.UserId));
        Assert.Equal(["usr_a", "usr_b"], second.Bans.Select(b => b.UserId));
        Assert.Equal(2, second.Page);
    }
}
