using System.Net;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// The four pages share one gate: <c>ViewAnalytics</c>. Every one is checked, because a page
/// added later without the flag would be the one that leaks.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AnalyticsAccessTests
{
    public static readonly TheoryData<string> Pages =
    [
        "/api/analytics/group",
        "/api/analytics/team",
        "/api/analytics/worlds",
        "/api/analytics/instances",
        "/api/analytics/server",
    ];

    private readonly PostgresFixture _db;

    public AnalyticsAccessTests(PostgresFixture db) => _db = db;

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnUnauthenticatedCaller_Gets401(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync(path, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task WithoutViewAnalytics_Is403(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var response = await host.GetAsync(path, cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task WithViewAnalytics_Answers(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.GetAsync($"{path}?days=7", cookie, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AReversedWindow_Is400(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.GetAsync($"{path}?from=2026-05-01&to=2026-04-01", cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
