using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Data;

/// <summary>
/// The <c>group_member</c> and <c>group_ban</c> tables as the migration creates them, against
/// real PostgreSQL.
/// </summary>
/// <remarks>
/// The schema, not the sweep: that the jsonb columns round-trip, that an id of any shape is
/// accepted in both halves of the key, that the same person can be a member of two groups, and
/// that the columns the design names exist under those names. The sweeps themselves are tested in
/// the VRChat suite.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class GroupMemberTests
{
    private readonly PostgresFixture _db;

    public GroupMemberTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AMemberRowRoundTripsWithItsJsonColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = $"grp_{Guid.NewGuid():N}";
        var user = $"usr_{Guid.NewGuid():N}";

        await using (var write = _db.NewContext())
        {
            write.GroupMembers.Add(new GroupMember
            {
                GroupId = group,
                UserId = user,
                MembershipId = "gmem_1",
                Roles = """["grol_a","grol_b"]""",
                JoinedAt = At.AddDays(-30),
                MembershipStatus = "member",
                Visibility = "visible",
                IsRepresenting = true,
                ManagerNotes = "regular",
                FirstSeenAt = At,
                LastSeenAt = At,
                WaitingFacts = """[{"kind":"join","from":"2026-09-13T11:00:00+00:00"}]""",
                Raw = """{"userId":"x","roleIds":["grol_a","grol_b"]}""",
            });

            await write.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        var row = await read.GroupMembers.AsNoTracking().SingleAsync(m => m.GroupId == group && m.UserId == user, ct);

        Assert.Equal(At.AddDays(-30), row.JoinedAt);
        Assert.True(row.IsRepresenting);
        Assert.Null(row.LeftAt);

        // jsonb normalises whitespace, so the shape is asserted rather than the bytes.
        Assert.Contains("grol_b", row.Roles);
        Assert.Contains("\"kind\"", row.WaitingFacts);
        Assert.Contains("\"roleIds\"", row.Raw);
    }

    [Fact]
    public async Task ABanRowRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = $"grp_{Guid.NewGuid():N}";
        var user = $"usr_{Guid.NewGuid():N}";

        await using (var write = _db.NewContext())
        {
            write.GroupBans.Add(new GroupBan
            {
                GroupId = group,
                UserId = user,
                BannedAt = At.AddDays(-2),
                FirstSeenAt = At,
                LastSeenAt = At,
                Raw = """{"userId":"x","bannedAt":"2026-09-11T12:00:00Z"}""",
            });

            await write.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        var row = await read.GroupBans.AsNoTracking().SingleAsync(b => b.GroupId == group && b.UserId == user, ct);

        Assert.Equal(At.AddDays(-2), row.BannedAt);
        Assert.Null(row.LiftedAt);
        Assert.Contains("bannedAt", row.Raw);
    }

    /// <summary>
    /// Spec 3.1.1: legacy ids follow no structure. Both halves of the key must take whatever
    /// VRChat sends.
    /// </summary>
    [Fact]
    public async Task ALegacyIdOfAnyShapeIsAcceptedInTheKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = $"Old Group {Guid.NewGuid():N}";
        var user = $"8JoV9XEdpo {Guid.NewGuid():N} with spaces/and.punctuation";

        await using (var write = _db.NewContext())
        {
            write.GroupMembers.Add(new GroupMember { GroupId = group, UserId = user, FirstSeenAt = At, LastSeenAt = At });
            await write.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        Assert.NotNull(await read.GroupMembers.FindAsync([group, user], ct));
    }

    /// <summary>
    /// The key includes the group on purpose: one person, two groups, two rows. A single-group
    /// appliance today is not a promise about the table.
    /// </summary>
    [Fact]
    public async Task TheSamePersonCanBelongToTwoGroups()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = $"usr_{Guid.NewGuid():N}";

        await using (var write = _db.NewContext())
        {
            write.GroupMembers.Add(new GroupMember { GroupId = "grp_one", UserId = user, FirstSeenAt = At, LastSeenAt = At });
            write.GroupMembers.Add(new GroupMember { GroupId = "grp_two", UserId = user, FirstSeenAt = At, LastSeenAt = At });
            await write.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        Assert.Equal(2, await read.GroupMembers.CountAsync(m => m.UserId == user, ct));
    }

    [Fact]
    public async Task TheSameMembershipCannotBeInsertedTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = $"usr_{Guid.NewGuid():N}";

        await using (var first = _db.NewContext())
        {
            first.GroupMembers.Add(new GroupMember { GroupId = "grp_one", UserId = user, FirstSeenAt = At, LastSeenAt = At });
            await first.SaveChangesAsync(ct);
        }

        await using var second = _db.NewContext();
        second.GroupMembers.Add(new GroupMember { GroupId = "grp_one", UserId = user, FirstSeenAt = At, LastSeenAt = At });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(ct));
    }

    /// <summary>The columns the design names exist under those names, in both tables and on the settings row.</summary>
    [Fact]
    public async Task TheColumnsAreNamedPlainly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = _db.NewContext();

        var members = await Columns(context, "group_member", ct);
        Assert.Contains("joined_at", members);
        Assert.Contains("left_at", members);
        Assert.Contains("roles", members);
        Assert.Contains("membership_status", members);
        Assert.Contains("manager_notes", members);
        Assert.Contains("waiting_facts", members);
        Assert.Contains("raw", members);

        var bans = await Columns(context, "group_ban", ct);
        Assert.Contains("banned_at", bans);
        Assert.Contains("lifted_at", bans);
        Assert.Contains("raw", bans);

        var settings = await Columns(context, "settings", ct);
        Assert.Contains("member_sweep_offset", settings);
        Assert.Contains("member_sweep_completed_at", settings);
        Assert.Contains("ban_sweep_offset", settings);
        Assert.Contains("ban_sweep_completed_at", settings);
    }

    /// <summary>The role filter runs on a GIN index, and a jsonb containment test must find a role inside the array.</summary>
    [Fact]
    public async Task ARoleCanBeFoundInsideTheRolesArray()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = $"grp_{Guid.NewGuid():N}";

        await using (var write = _db.NewContext())
        {
            write.GroupMembers.Add(new GroupMember { GroupId = group, UserId = "usr_a", Roles = """["grol_mod","grol_member"]""", FirstSeenAt = At, LastSeenAt = At });
            write.GroupMembers.Add(new GroupMember { GroupId = group, UserId = "usr_b", Roles = """["grol_member"]""", FirstSeenAt = At, LastSeenAt = At });
            await write.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();

        var moderators = await read.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == group && EF.Functions.JsonContains(m.Roles, """["grol_mod"]"""))
            .Select(m => m.UserId)
            .ToListAsync(ct);

        Assert.Equal(["usr_a"], moderators);
    }

    private static async Task<List<string>> Columns(Core.Data.ModbotContext context, string table, CancellationToken ct) =>
        await context.Database
            .SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = {0}", table)
            .ToListAsync(ct);
}
