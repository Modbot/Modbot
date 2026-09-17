using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Data.Migrations;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Retention;

/// <summary>
/// The catch-up in the <c>AddGroupMemberCounts</c> migration: the readings table filled from the
/// group-info facts a deployment recorded before it existed.
/// </summary>
/// <remarks>
/// Every suite migrates an empty database, so the catch-up runs over nothing everywhere else.
/// Here its SQL is taken off the migration and run over facts shaped the way the sync shapes
/// them, which is the only way to know it reads a real fact log.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class GroupMemberCountCatchUpTests(PostgresFixture fixture) : AnalyticsTestBase(fixture)
{
    [Fact]
    public async Task OneReadingPerFactThatCarriedACount_EachCountCarriedForward()
    {
        var t0 = Start.AddDays(-10);

        await WriteAsync(
            GroupFact(t0, new JsonObject
            {
                ["baseline"] = new JsonObject { ["MemberCount"] = 14208, ["OnlineMemberCount"] = 30, ["Name"] = "Group" },
            }),

            // A change knows only that it happened since the previous poll; the reading is when
            // the poll saw it.
            GroupFact(t0.AddDays(1), Changed("MemberCount", 14208, 14230), since: t0.AddDays(1).AddMinutes(-5)),
            GroupFact(t0.AddDays(2), Changed("OnlineMemberCount", 30, 35), since: t0.AddDays(2).AddMinutes(-5)),

            // A change to something else, and the audit log's own group-update entry: no counts,
            // no reading.
            GroupFact(t0.AddDays(3), Changed("Name", "Group", "Renamed"), since: t0.AddDays(3).AddMinutes(-5)),
            GroupFact(t0.AddDays(4), new JsonObject { ["description"] = "audit" }));

        await using var context = Database.NewContext();

        var sql = new AddGroupMemberCounts().UpOperations.OfType<SqlOperation>().Single().Sql;
        await context.Database.ExecuteSqlRawAsync(sql, Ct);

        var readings = await context.GroupMemberCounts.AsNoTracking().OrderBy(r => r.CountedAt).ToListAsync(Ct);

        Assert.Equal([t0, t0.AddDays(1), t0.AddDays(2)], readings.Select(r => r.CountedAt));
        Assert.Equal([14208, 14230, 14230], readings.Select(r => r.MemberCount));
        Assert.Equal([30, 30, 35], readings.Select(r => r.OnlineMemberCount));
        Assert.All(readings, r => Assert.Equal("grp_test", r.GroupId));
    }

    /// <summary>An online count changing before any member count was stated makes no reading: there is no member count to give it.</summary>
    [Fact]
    public async Task AnOnlineCountBeforeAnyMemberCount_MakesNoReading()
    {
        var t0 = Start.AddDays(-10);

        await WriteAsync(
            GroupFact(t0, Changed("OnlineMemberCount", 3, 4), since: t0.AddMinutes(-5)),
            GroupFact(t0.AddDays(1), new JsonObject { ["baseline"] = new JsonObject { ["MemberCount"] = 500 } }));

        await using var context = Database.NewContext();

        var sql = new AddGroupMemberCounts().UpOperations.OfType<SqlOperation>().Single().Sql;
        await context.Database.ExecuteSqlRawAsync(sql, Ct);

        var reading = Assert.Single(await context.GroupMemberCounts.AsNoTracking().ToListAsync(Ct));

        Assert.Equal(t0.AddDays(1), reading.CountedAt);
        Assert.Equal(500, reading.MemberCount);

        // The baseline said nothing about the online count, so the last one stated carries
        // forward into the reading, as it would between any two facts.
        Assert.Equal(4, reading.OnlineMemberCount);
    }

    private static JsonObject Changed(string field, JsonNode? from, JsonNode? to)
        => new() { ["changed"] = new JsonObject { [field] = new JsonObject { ["old"] = from, ["new"] = to } } };

    private static FactRecord GroupFact(DateTimeOffset at, JsonObject data, DateTimeOffset? since = null) => new()
    {
        Type = FactType.GroupInfoChanged,
        OccurredAt = since ?? at,
        OccurredBefore = since is null ? null : at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = "grp_test",
        Source = FactSource.SyncDiff,
        Data = data,
    };
}
