using System.Net;
using Modbot.Api.Features.People;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.People;

/// <summary>
/// The People list narrowed to one instance's run, and to who was there at given moments.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PeopleInInstanceTests
{
    private const ModbotPermissions Both = ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog;

    private readonly PostgresFixture _db;

    public PeopleInInstanceTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Ada is there from 5 to 20 and again from 40 to 60, Bea from 10 to 50, and Cy joins at 30 and
    /// is never seen to leave, so he runs to the last report at 60. Minutes after the instance opened.
    /// </summary>
    private static async Task<(Guid Instance, DateTimeOffset Start)> SeedAsync(ReadSurfaceTestHost host)
    {
        // Whole seconds, so what comes back from the database is the instant written.
        var started = host.Clock.UtcNow.AddHours(-4);
        var t = started.AddTicks(-(started.Ticks % TimeSpan.TicksPerSecond));

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, Ct);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(2), t.AddHours(2), Ct);

        foreach (var (id, name) in new[] { ("usr_ada", "Ada"), ("usr_bea", "Bea"), ("usr_cy", "Cy"), ("usr_dee", "Dee") })
            await PlacesFixtures.PersonAsync(host, id, name, t, Ct);

        async Task Write(string type, string who, int minute)
            => await host.WriteFactAsync(PresenceFact(type, who, t.AddMinutes(minute), "wrld_a", "39047"), Ct);

        await Write(FactType.InstanceJoined, "usr_ada", 5);
        await Write(FactType.InstanceJoined, "usr_bea", 10);
        await Write(FactType.InstanceLeft, "usr_ada", 20);
        await Write(FactType.InstanceJoined, "usr_cy", 30);
        await Write(FactType.InstanceJoined, "usr_ada", 40);
        await Write(FactType.InstanceLeft, "usr_bea", 50);
        await Write(FactType.InstanceLeft, "usr_ada", 60);

        return (instance.Id, t);
    }

    private static string Moment(DateTimeOffset t, int minute)
        => Uri.EscapeDataString(t.AddMinutes(minute).ToUniversalTime().ToString("O"));

    private static async Task<string[]> WhoAsync(ReadSurfaceTestHost host, string url, string cookie)
        => [.. (await host.GetJsonAsync<PeopleListResponse>(url, cookie, Ct)).People.Select(p => p.UserId).Order()];

    [Fact]
    public async Task AnInstance_NarrowsThePeopleToWhoWasThere_OneRowEach()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var (instance, t) = await SeedAsync(host);
        var cookie = await host.SignedInAsync(Both, Ct);

        var list = await host.GetJsonAsync<PeopleListResponse>($"/api/people?instance={instance}", cookie, Ct);

        Assert.Equal(3, list.Total);
        Assert.Equal(["usr_ada", "usr_bea", "usr_cy"], list.People.Select(p => p.UserId).Order());

        // Two visits, one row: first arrival, last leave, and a count.
        var ada = list.People.Single(p => p.UserId == "usr_ada").Visit!;
        Assert.Equal(t.AddMinutes(5), ada.Arrived);
        Assert.True(ada.ArrivalSeen);
        Assert.Equal(t.AddMinutes(60), ada.Left);
        Assert.True(ada.LeaveSeen);
        Assert.Equal(2, ada.Visits);

        var cy = list.People.Single(p => p.UserId == "usr_cy").Visit!;
        Assert.Equal((t.AddMinutes(30), t.AddMinutes(60), false, 1), (cy.Arrived, cy.Left, cy.LeaveSeen, cy.Visits));
    }

    [Fact]
    public async Task WithoutAnInstance_RowsCarryNoVisit()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        var list = await host.GetJsonAsync<PeopleListResponse>("/api/people", cookie, Ct);

        Assert.Equal(4, list.Total);
        Assert.All(list.People, p => Assert.Null(p.Visit));
    }

    [Fact]
    public async Task HereAt_KeepsWhoseVisitCoveredThatMoment_TheEndExcluded()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var (instance, t) = await SeedAsync(host);
        var cookie = await host.SignedInAsync(Both, Ct);

        Assert.Equal(
            ["usr_ada", "usr_bea"],
            await WhoAsync(host, $"/api/people?instance={instance}&hereAt={Moment(t, 15)}", cookie));

        // Ada left at 20, so her visit does not cover 20; it starts at 5, so it covers 5.
        Assert.Equal(["usr_bea"], await WhoAsync(host, $"/api/people?instance={instance}&hereAt={Moment(t, 20)}", cookie));
        Assert.Equal(["usr_ada"], await WhoAsync(host, $"/api/people?instance={instance}&hereAt={Moment(t, 5)}", cookie));
    }

    [Fact]
    public async Task EveryMomentHolds_AndSomebodyWhoCameBackWasHereAtBothVisits()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var (instance, t) = await SeedAsync(host);
        var cookie = await host.SignedInAsync(Both, Ct);

        // Ada was here at 15 and at 45 (two visits); Bea at both too; Cy only at 45.
        Assert.Equal(
            ["usr_ada", "usr_bea"],
            await WhoAsync(host, $"/api/people?instance={instance}&hereAt={Moment(t, 15)}&hereAt={Moment(t, 45)}", cookie));

        // Here at 15 and not here at 25: Ada was away at 25, Bea was not.
        Assert.Equal(
            ["usr_ada"],
            await WhoAsync(host, $"/api/people?instance={instance}&hereAt={Moment(t, 15)}&notHereAt={Moment(t, 25)}", cookie));
    }

    [Fact]
    public async Task NotHereAt_KeepsWhoWasSeenInTheInstanceButNotThen()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var (instance, t) = await SeedAsync(host);
        var cookie = await host.SignedInAsync(Both, Ct);

        // Dee was never in the instance, so she is not "not here": the list is the instance's people.
        Assert.Equal(["usr_cy"], await WhoAsync(host, $"/api/people?instance={instance}&notHereAt={Moment(t, 15)}", cookie));

        // Ada was away between her visits; Bea is still here at 25; Cy has not come yet.
        Assert.Equal(
            ["usr_ada", "usr_cy"],
            await WhoAsync(host, $"/api/people?instance={instance}&notHereAt={Moment(t, 25)}", cookie));
    }

    [Fact]
    public async Task HereAtAndNotHereAt_NeedAnInstance()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var (_, t) = await SeedAsync(host);
        var cookie = await host.SignedInAsync(Both | ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync($"/api/people?hereAt={Moment(t, 15)}", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync($"/api/people?notHereAt={Moment(t, 15)}", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task AnInstanceNeedsSeeAnalyticsAndSeeAuditLog_AndStillNoFlagsOrLinksWithoutSeeProfiles()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var (instance, _) = await SeedAsync(host);

        var analytics = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var auditLog = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var profiles = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync($"/api/people?instance={instance}", analytics, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync($"/api/people?instance={instance}", auditLog, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync($"/api/people?instance={instance}", profiles, Ct)).StatusCode);

        // Both, and neither See members nor See profiles: allowed, with no membership restriction.
        var both = await host.SignedInAsync(Both, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/people?instance={instance}", both, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/people?instance={instance}&membership=not-member", both, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync($"/api/people?instance={instance}&flagged=true", both, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync($"/api/people?instance={instance}&linked=linked", both, Ct)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownInstance_Is404()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var cookie = await host.SignedInAsync(Both, Ct);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/people?instance={Guid.NewGuid()}", cookie, Ct)).StatusCode);
    }
}
