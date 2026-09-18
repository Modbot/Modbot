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
/// The member list paged by cursor: that the pages join up, that they keep joining up while the
/// list is written to underneath, and that a cursor nobody can read shows the list anyway.
/// </summary>
/// <remarks>
/// These are the cases a page number gets wrong, which is the whole reason the cursor exists. The
/// member sweep rewrites this table constantly, so "read page one, somebody joins, read page two"
/// is not a contrived sequence -- it is the ordinary one.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class MemberPagingTests
{
    private const string Group = "grp_test";

    private readonly PostgresFixture _db;

    public MemberPagingTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Six members, each a day apart, newest first: f, e, d, c, b, a.</summary>
    private static async Task SeedAsync(ReadSurfaceTestHost host, params (string Id, DateTimeOffset? Joined)[] people)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;
        settings.MemberSweepCompletedAt = Day.AddHours(1);
        settings.MemberSweepCount = people.Length;

        foreach (var (id, joined) in people)
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
        }

        await db.SaveChangesAsync(Ct);
    }

    private static (string, DateTimeOffset?)[] SixADayApart() =>
    [
        ("usr_a", Day.AddDays(-6)),
        ("usr_b", Day.AddDays(-5)),
        ("usr_c", Day.AddDays(-4)),
        ("usr_d", Day.AddDays(-3)),
        ("usr_e", Day.AddDays(-2)),
        ("usr_f", Day.AddDays(-1)),
    ];

    [Fact]
    public async Task ACursorReadsTheRowsAfterTheOnesAlreadyRead()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<MemberListResponse>("/api/members?pageSize=2", cookie, Ct);
        Assert.Equal(["usr_f", "usr_e"], first.Members.Select(m => m.UserId));
        Assert.NotNull(first.Next);

        // The first page has nothing behind it, so there is nothing to go back to.
        Assert.Null(first.Previous);

        var second = await host.GetJsonAsync<MemberListResponse>(Page(first.Next), cookie, Ct);
        Assert.Equal(["usr_d", "usr_c"], second.Members.Select(m => m.UserId));

        var third = await host.GetJsonAsync<MemberListResponse>(Page(second.Next), cookie, Ct);
        Assert.Equal(["usr_b", "usr_a"], third.Members.Select(m => m.UserId));

        // Six rows in pages of two: the last page is full, so the server only knows there is no
        // more after asking for one row past it.
        Assert.Null(third.Next);
        Assert.NotNull(third.Previous);
    }

    [Fact]
    public async Task GoingBackLandsOnThePageJustLeft()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<MemberListResponse>("/api/members?pageSize=2", cookie, Ct);
        var second = await host.GetJsonAsync<MemberListResponse>(Page(first.Next), cookie, Ct);
        var back = await host.GetJsonAsync<MemberListResponse>(Page(second.Previous), cookie, Ct);

        Assert.Equal(["usr_f", "usr_e"], back.Members.Select(m => m.UserId));

        // Back at the top, so there is nowhere further back -- and the rows come back in the
        // list's own order, not the order the query read them in.
        Assert.Null(back.Previous);
        Assert.NotNull(back.Next);
    }

    [Fact]
    public async Task ARowArrivingBetweenTwoPagesRepeatsNothingAndSkipsNothing()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<MemberListResponse>("/api/members?pageSize=2", cookie, Ct);
        Assert.Equal(["usr_f", "usr_e"], first.Members.Select(m => m.UserId));

        // A sweep finds somebody who joined this morning: they belong at the top of the list,
        // above everything already read. With a page number, page two would now start one row
        // earlier and usr_d would be read twice.
        await SeedAsync(host, [("usr_new", Day)]);

        var second = await host.GetJsonAsync<MemberListResponse>(Page(first.Next), cookie, Ct);

        Assert.Equal(["usr_d", "usr_c"], second.Members.Select(m => m.UserId));
        Assert.DoesNotContain("usr_e", second.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task ARowLeavingBetweenTwoPagesDoesNotPullOneOutOfSight()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<MemberListResponse>("/api/members?pageSize=2", cookie, Ct);

        // usr_f leaves. A numbered page two would now begin at usr_c and usr_d would never be
        // read by anybody paging through.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var gone = await db.GroupMembers.FindAsync(["grp_test", "usr_f"], Ct);
            gone!.LeftAt = Day;
            await db.SaveChangesAsync(Ct);
        }

        var second = await host.GetJsonAsync<MemberListResponse>(Page(first.Next), cookie, Ct);

        Assert.Equal(["usr_d", "usr_c"], second.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task PeopleWhoJoinedAtTheSameMomentAreNotSteppedOver()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // What a sweep of an imported group looks like: one timestamp for everybody. A cursor on
        // the timestamp alone would hand back the same page for ever, or skip the lot.
        await SeedAsync(host,
            ("usr_a", Day),
            ("usr_b", Day),
            ("usr_c", Day),
            ("usr_d", Day));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var seen = new List<string>();
        var url = "/api/members?pageSize=2";

        for (var read = 0; read < 5; read++)
        {
            var page = await host.GetJsonAsync<MemberListResponse>(url, cookie, Ct);
            seen.AddRange(page.Members.Select(m => m.UserId));

            if (page.Next is null)
                break;

            url = Page(page.Next);
        }

        Assert.Equal(["usr_a", "usr_b", "usr_c", "usr_d"], seen);
    }

    [Fact]
    public async Task PeopleWithNoJoinDateComeLastAndStillPage()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await SeedAsync(host,
            ("usr_a", Day.AddDays(-2)),
            ("usr_b", Day.AddDays(-1)),
            ("usr_y", null),
            ("usr_z", null));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<MemberListResponse>("/api/members?pageSize=2", cookie, Ct);
        Assert.Equal(["usr_b", "usr_a"], first.Members.Select(m => m.UserId));

        // The boundary falls exactly where the join dates run out, so the cursor for the next
        // page carries no value at all -- which is not the same as carrying an empty one.
        var second = await host.GetJsonAsync<MemberListResponse>(Page(first.Next), cookie, Ct);
        Assert.Equal(["usr_y", "usr_z"], second.Members.Select(m => m.UserId));
        Assert.Null(second.Next);

        var back = await host.GetJsonAsync<MemberListResponse>(Page(second.Previous), cookie, Ct);
        Assert.Equal(["usr_b", "usr_a"], back.Members.Select(m => m.UserId));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("next!name!=Alice!usr_a")]
    [InlineData("next!joined!=not-a-date!usr_a")]
    public async Task ACursorNobodyCanReadShowsTheListRatherThanAnError(string cursor)
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        // A bookmark from before a redeploy, a link pasted with a character lost, a cursor
        // written while the list was sorted by name. None of them is worth an error page.
        var page = await host.GetJsonAsync<MemberListResponse>(
            $"/api/members?pageSize=2&cursor={Uri.EscapeDataString(cursor)}", cookie, Ct);

        Assert.Equal(["usr_f", "usr_e"], page.Members.Select(m => m.UserId));
        Assert.Null(page.Previous);
    }

    [Fact]
    public async Task TheOldPageNumberStillWorksAndHandsBackACursor()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        // Scripts and API keys were already calling this with `page`, so it keeps working -- and
        // the answer carries the cursor they can move to.
        var second = await host.GetJsonAsync<MemberListResponse>("/api/members?pageSize=2&page=2", cookie, Ct);

        Assert.Equal(["usr_d", "usr_c"], second.Members.Select(m => m.UserId));
        Assert.Equal(2, second.Page);
        Assert.Equal(6, second.Total);
        Assert.NotNull(second.Next);

        var third = await host.GetJsonAsync<MemberListResponse>(Page(second.Next), cookie, Ct);
        Assert.Equal(["usr_b", "usr_a"], third.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task ACursorWinsOverAPageNumberSentWithIt()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host, SixADayApart());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<MemberListResponse>("/api/members?pageSize=2", cookie, Ct);
        var second = await host.GetJsonAsync<MemberListResponse>($"{Page(first.Next)}&page=5", cookie, Ct);

        Assert.Equal(["usr_d", "usr_c"], second.Members.Select(m => m.UserId));
        Assert.Equal(1, second.Page);
    }

    [Fact]
    public async Task SortingByNamePagesThroughThePeopleWithNoNameToo()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host,
            ("usr_a", Day),
            ("usr_b", Day),
            ("usr_y", Day),
            ("usr_z", Day));

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.VRChatUsers.AddRange(
                new VRChatUser { UserId = "usr_a", DisplayName = "Alice", FirstSeenAt = Day, LastSeenAt = Day, LastRefreshedAt = Day },
                new VRChatUser { UserId = "usr_b", DisplayName = "Bob", FirstSeenAt = Day, LastSeenAt = Day, LastRefreshedAt = Day });
            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var first = await host.GetJsonAsync<MemberListResponse>("/api/members?sort=name&pageSize=2", cookie, Ct);
        Assert.Equal(["usr_a", "usr_b"], first.Members.Select(m => m.UserId));

        // The profile sync has not reached these two, so they have no name to sort on and sit at
        // the end of the list rather than at the start of it.
        var second = await host.GetJsonAsync<MemberListResponse>(
            $"/api/members?sort=name&pageSize=2&cursor={Uri.EscapeDataString(first.Next!)}", cookie, Ct);

        Assert.Equal(["usr_y", "usr_z"], second.Members.Select(m => m.UserId));
    }

    [Fact]
    public async Task TheGroupBanListPagesByCursorAndABanArrivingDoesNotRepeatARow()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(Ct);
            settings.ManagedGroupId = Group;
            settings.BanSweepCompletedAt = Day.AddHours(1);

            // A sweep of an old group: four bans, and two of them stamped with the same moment,
            // because that is what VRChat hands back for a batch.
            db.GroupBans.AddRange(
                new GroupBan { GroupId = Group, UserId = "usr_1", BannedAt = Day.AddDays(-4), FirstSeenAt = Day, LastSeenAt = Day },
                new GroupBan { GroupId = Group, UserId = "usr_2", BannedAt = Day.AddDays(-3), FirstSeenAt = Day, LastSeenAt = Day },
                new GroupBan { GroupId = Group, UserId = "usr_3", BannedAt = Day.AddDays(-3), FirstSeenAt = Day, LastSeenAt = Day },
                new GroupBan { GroupId = Group, UserId = "usr_4", BannedAt = Day.AddDays(-1), FirstSeenAt = Day, LastSeenAt = Day });

            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var first = await host.GetJsonAsync<GroupBanListResponse>("/api/bans?pageSize=2", cookie, Ct);
        Assert.Equal(["usr_4", "usr_2"], first.Bans.Select(b => b.UserId));

        // Somebody is banned while the reader is on page one. A numbered page two would now begin
        // at usr_2 and show it a second time.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.GroupBans.Add(new GroupBan
            {
                GroupId = Group, UserId = "usr_new", BannedAt = Day, FirstSeenAt = Day, LastSeenAt = Day,
            });
            await db.SaveChangesAsync(Ct);
        }

        var second = await host.GetJsonAsync<GroupBanListResponse>(
            $"/api/bans?pageSize=2&cursor={Uri.EscapeDataString(first.Next!)}", cookie, Ct);

        // usr_3 shares usr_2's ban moment, so it is only reachable at all because the user id
        // breaks the tie inside the cursor.
        Assert.Equal(["usr_3", "usr_1"], second.Bans.Select(b => b.UserId));
    }

    [Fact]
    public async Task AGroupBanCursorNobodyCanReadShowsTheListRatherThanAnError()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(Ct);
            settings.ManagedGroupId = Group;

            db.GroupBans.Add(new GroupBan
            {
                GroupId = Group, UserId = "usr_1", BannedAt = Day, FirstSeenAt = Day, LastSeenAt = Day,
            });

            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var page = await host.GetJsonAsync<GroupBanListResponse>("/api/bans?cursor=nonsense", cookie, Ct);

        Assert.Equal(["usr_1"], page.Bans.Select(b => b.UserId));
        Assert.Null(page.Previous);
    }

    private static string Page(string? cursor) =>
        $"/api/members?pageSize=2&cursor={Uri.EscapeDataString(cursor!)}";
}
