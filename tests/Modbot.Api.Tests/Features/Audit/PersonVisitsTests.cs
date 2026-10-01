using System.Net;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// Where one person was seen, a visit per row, made from the reports the time-in-world figures add up.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PersonVisitsTests
{
    private readonly PostgresFixture _db;

    public PersonVisitsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Evening = new(2026, 5, 7, 19, 0, 0, TimeSpan.Zero);

    private const string Ada = "usr_ada";

    private static FactRecord Presence(
        string type,
        string subject,
        DateTimeOffset at,
        string instance = "39047",
        string world = "wrld_a",
        JsonObject? data = null) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        WorldId = world,
        InstanceId = instance,
        Source = FactSource.Companion,
        Data = data ?? new JsonObject { ["displayName"] = subject == Ada ? "Ada" : "Bob" },
    };

    private static FactRecord Avatar(string name, DateTimeOffset at, string instance = "39047") =>
        Presence(FactType.AvatarChanged, Ada, at, instance, data: new JsonObject { ["displayName"] = "Ada", ["avatarName"] = name });

    [Fact]
    public async Task AnArrivalAndALeaveAreOneVisit()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await host.WriteFactAsync(Presence(FactType.InstanceJoined, Ada, Evening), Ct);
        await host.WriteFactAsync(Presence(FactType.InstanceLeft, Ada, Evening.AddMinutes(30)), Ct);
        // Somebody else still there later: the instance's last report is not Ada's leave.
        await host.WriteFactAsync(Presence(FactType.InstanceJoined, "usr_bob", Evening.AddMinutes(45)), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var page = await host.GetJsonAsync<PersonVisitsPage>($"/api/audit/visits?person={Ada}", cookie, Ct);

        var visit = Assert.Single(page.Visits);
        Assert.Equal(FactType.InstanceJoined, visit.Arrived.Type);
        Assert.NotNull(visit.Left);
        Assert.Equal(Evening.AddMinutes(30), visit.Until);
        Assert.Equal("Ada", visit.Name);
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task AVisitNobodySawEndRunsToTheLastReportFromThatInstance()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await host.WriteFactAsync(Presence(FactType.InstanceJoined, Ada, Evening), Ct);
        await host.WriteFactAsync(Presence(FactType.InstanceJoined, "usr_bob", Evening.AddHours(1)), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var page = await host.GetJsonAsync<PersonVisitsPage>($"/api/audit/visits?person={Ada}", cookie, Ct);

        var visit = Assert.Single(page.Visits);
        Assert.Null(visit.Left);
        Assert.Equal(Evening.AddHours(1), visit.Until);
    }

    [Fact]
    public async Task VisitsAreNewestFirst_AndPageWithoutRepeating()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await host.WriteFactAsync(Presence(FactType.InstanceJoined, Ada, Evening, instance: "1"), Ct);
        await host.WriteFactAsync(Presence(FactType.InstanceLeft, Ada, Evening.AddMinutes(10), instance: "1"), Ct);
        await host.WriteFactAsync(Presence(FactType.InstanceJoined, Ada, Evening.AddHours(1), instance: "2"), Ct);
        await host.WriteFactAsync(Presence(FactType.InstanceLeft, Ada, Evening.AddHours(1).AddMinutes(10), instance: "2"), Ct);
        await host.WriteFactAsync(Presence(FactType.InstanceJoined, Ada, Evening.AddHours(2), instance: "3"), Ct);
        await host.WriteFactAsync(Presence(FactType.InstanceLeft, Ada, Evening.AddHours(2).AddMinutes(10), instance: "3"), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var first = await host.GetJsonAsync<PersonVisitsPage>($"/api/audit/visits?person={Ada}&limit=2", cookie, Ct);
        Assert.Equal(["3", "2"], first.Visits.Select(v => v.Arrived.InstanceId));
        Assert.NotNull(first.Next);

        var next = first.Next!;
        var second = await host.GetJsonAsync<PersonVisitsPage>(
            $"/api/audit/visits?person={Ada}&limit=2&beforeStartedAt={Uri.EscapeDataString(next.OccurredAt.ToString("O"))}&beforeId={next.Id}",
            cookie,
            Ct);

        Assert.Equal(["1"], second.Visits.Select(v => v.Arrived.InstanceId));
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task AVisitListsTheAvatarsWornDuringIt_AndNoOthers()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await host.WriteFactAsync(Avatar("Before", Evening.AddMinutes(-30), instance: "other"), Ct);
        await host.WriteFactAsync(Presence(FactType.InstanceJoined, Ada, Evening), Ct);
        await host.WriteFactAsync(Avatar("Fox", Evening.AddMinutes(5)), Ct);
        await host.WriteFactAsync(Avatar("Robot", Evening.AddMinutes(10)), Ct);
        await host.WriteFactAsync(Avatar("Fox", Evening.AddMinutes(15)), Ct);
        await host.WriteFactAsync(Presence(FactType.InstanceLeft, Ada, Evening.AddMinutes(20)), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var page = await host.GetJsonAsync<PersonVisitsPage>($"/api/audit/visits?person={Ada}", cookie, Ct);

        var visit = page.Visits.Single(v => v.Arrived.InstanceId == "39047");
        Assert.Equal(["Fox", "Robot"], visit.Avatars);
    }

    [Fact]
    public async Task VisitsNeedTheAuditLogsPermission()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // The arrivals and leaves a visit is made of are moderation entries; See profiles alone
        // does not read them in the log, and does not read them here.
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var response = await host.GetAsync($"/api/audit/visits?person={Ada}", cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
