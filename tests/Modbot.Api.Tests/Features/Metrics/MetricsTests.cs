using System.Net;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Analytics.DailyTotals;
using Modbot.Api.Features.Metrics;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Metrics;

[Collection(nameof(PostgresCollection))]
public class MetricsTests
{
    private readonly PostgresFixture _db;

    public MetricsTests(PostgresFixture db) => _db = db;

    private static FactRecord Membership(string type, string subject, DateTimeOffset at) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        ActorPlatform = type is FactType.MemberBanned ? FactPlatform.VRChat : null,
        ActorId = type is FactType.MemberBanned ? "usr_mod" : null,
        Source = FactSource.AuditLog,
        Data = new JsonObject { ["actorDisplayName"] = "RedZu" },
    };

    private static FactRecord GroupInfoBaseline(int memberCount, DateTimeOffset at) => new()
    {
        Type = FactType.GroupInfoChanged,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = "grp_1",
        Source = FactSource.SyncDiff,
        Data = new JsonObject
        {
            ["baseline"] = new JsonObject { ["MemberCount"] = memberCount },
        },
    };

    private static FactRecord GroupInfoChange(int from, int to, DateTimeOffset at) => new()
    {
        Type = FactType.GroupInfoChanged,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = "grp_1",
        Source = FactSource.SyncDiff,
        Data = new JsonObject
        {
            ["changed"] = new JsonObject
            {
                ["MemberCount"] = new JsonObject { ["old"] = from, ["new"] = to },
            },
        },
    };

    [Fact]
    public async Task DailySeries_ComeFromTheDailyTotals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var day = host.Clock.UtcNow.AddDays(-2);

        await host.WriteFactAsync(Membership(FactType.MemberJoined, "usr_a", day), ct);
        await host.WriteFactAsync(Membership(FactType.MemberJoined, "usr_b", day), ct);
        await host.WriteFactAsync(Membership(FactType.MemberLeft, "usr_c", day), ct);
        await host.WriteFactAsync(Membership(FactType.MemberBanned, "usr_d", day), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var metrics = await host.GetJsonAsync<MetricsResponse>("/api/metrics?days=30", cookie, ct);

        decimal Total(string metric) => metrics.Series
            .Single(s => s.Metric == metric)
            .Points.Sum(p => p.Value);

        Assert.Equal(2m, Total(DailyTotalMetrics.MembersJoined));
        Assert.Equal(1m, Total(DailyTotalMetrics.MembersLeft));
        Assert.Equal(1m, Total(DailyTotalMetrics.BansAdded));
    }

    [Fact]
    public async Task MembersNet_IsLabelledAsNetChange_NotAsTheHeadcount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(
            Membership(FactType.MemberJoined, "usr_a", host.Clock.UtcNow.AddDays(-1)), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var metrics = await host.GetJsonAsync<MetricsResponse>("/api/metrics", cookie, ct);

        var series = metrics.Series.Single(s => s.Metric == DailyTotalMetrics.MembersNet);

        // The daily total counts from zero on the fact log's first day. A group that installs Modbot
        // with 40,000 members watches this series start at zero and climb -- so it must never be
        // labelled "members", and the note has to say what it is instead.
        Assert.DoesNotContain("Members", series.Label, StringComparison.Ordinal);
        Assert.NotNull(series.Note);
        Assert.Contains("not the group's member count", series.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemberCount_IsTheObservedHeadcount_FromGroupInfoFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var today = host.Clock.UtcNow;

        await host.WriteFactAsync(GroupInfoBaseline(14208, today.AddDays(-3)), ct);
        await host.WriteFactAsync(GroupInfoChange(14208, 14230, today.AddDays(-1)), ct);

        // Two observations on the same day: the later one is the day's value.
        await host.WriteFactAsync(GroupInfoChange(14230, 14244, today.AddDays(-1).AddHours(3)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var metrics = await host.GetJsonAsync<MetricsResponse>("/api/metrics?days=30", cookie, ct);

        Assert.Equal([14208m, 14244m], metrics.MemberCount.Select(p => p.Value));
    }

    [Fact]
    public async Task PerModeratorTotals_AreDimensionedByActor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var day = host.Clock.UtcNow.AddDays(-1);

        await host.WriteFactAsync(Membership(FactType.MemberBanned, "usr_a", day), ct);
        await host.WriteFactAsync(Membership(FactType.MemberBanned, "usr_b", day.AddMinutes(1)), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var metrics = await host.GetJsonAsync<MetricsResponse>("/api/metrics", cookie, ct);

        var moderator = Assert.Single(metrics.Moderators);

        Assert.Equal("vrchat:usr_mod", moderator.Dimension);
        Assert.Equal("usr_mod", moderator.ActorId);
        Assert.Equal("RedZu", moderator.Name);
        Assert.Equal(2m, moderator.Actions);
    }

    [Fact]
    public async Task ActionsByType_CountEachTypeSeparately()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var day = host.Clock.UtcNow.AddDays(-1);

        await host.WriteFactAsync(Membership(FactType.MemberBanned, "usr_a", day), ct);
        await host.WriteFactAsync(Membership(FactType.MemberBanned, "usr_b", day), ct);
        await host.WriteFactAsync(
            Membership(FactType.MemberUnbanned, "usr_a", day.AddHours(1)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var metrics = await host.GetJsonAsync<MetricsResponse>("/api/metrics?days=30", cookie, ct);

        decimal Total(string type) => metrics.ActionsByType
            .Single(s => s.Type == type.ToString()).Total;

        Assert.Equal(2m, Total(FactType.MemberBanned));
        Assert.Equal(1m, Total(FactType.MemberUnbanned));
        Assert.Equal(0m, Total(FactType.MemberKicked));
    }

    [Fact]
    public async Task Coverage_ReportsTheDailyTotalsRangeAndTheFactRangeSeparately()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var old = host.Clock.UtcNow.AddDays(-400);

        await host.WriteFactAsync(Membership(FactType.MemberJoined, "usr_a", old), ct);
        await host.RebuildDailyTotalsAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var metrics = await host.GetJsonAsync<MetricsResponse>("/api/metrics?days=30", cookie, ct);

        // Both are reported even though the requested window covers neither: a chart may
        // legitimately cover a longer period than the fact log does (spec 5.10.2), and one date
        // range shown over both would say they were the same.
        Assert.Equal(DateOnly.FromDateTime(old.UtcDateTime), metrics.Coverage.DailyTotalsFirstDay);
        Assert.Equal(DateOnly.FromDateTime(old.UtcDateTime), metrics.Coverage.FactFirstDay);
        Assert.False(metrics.Coverage.RetentionConfigured);
    }

    [Fact]
    public async Task TheWindow_IsDerivedFromTheDeploymentClock_NotTheMachines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var metrics = await host.GetJsonAsync<MetricsResponse>("/api/metrics?days=7", cookie, ct);

        Assert.Equal(new DateOnly(2026, 6, 15), metrics.To);
        Assert.Equal(new DateOnly(2026, 6, 9), metrics.From);
        Assert.Equal(host.Clock.UtcNow, metrics.GeneratedAt);
    }

    [Fact]
    public async Task WithoutViewAnalytics_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var response = await host.GetAsync("/api/metrics", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AReversedWindow_Is400()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.GetAsync(
            "/api/metrics?from=2026-05-01&to=2026-04-01", cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
