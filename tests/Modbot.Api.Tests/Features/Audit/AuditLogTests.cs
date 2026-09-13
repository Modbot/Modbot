using System.Net;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Audit;

[Collection(nameof(PostgresCollection))]
public class AuditLogTests
{
    private readonly PostgresFixture _db;

    public AuditLogTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static FactRecord Ban(string subject, string actor, DateTimeOffset at) => new()
    {
        Type = FactType.MemberBanned,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        ActorPlatform = FactPlatform.VRChat,
        ActorId = actor,
        Source = FactSource.AuditLog,
        Data = new JsonObject
        {
            ["actorDisplayName"] = "RedZu",
            ["description"] = $"{actor} banned {subject}",
        },
    };

    private static FactRecord SettingsChanged(DateTimeOffset at) => new()
    {
        Type = FactType.SettingsChanged,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.Modbot,
        SubjectId = "settings",
        ActorPlatform = FactPlatform.Modbot,
        ActorId = "alice",
        Source = FactSource.Modbot,
        Data = new JsonObject { ["setting"] = "SmtpHost" },
    };

    [Fact]
    public async Task AModeratorSeesBans_AndNotSettingsChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Ban("usr_a", "usr_mod", Day), ct);
        await host.WriteFactAsync(SettingsChanged(Day.AddMinutes(1)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit", cookie, ct);

        Assert.Equal([FactType.MemberBanned], page.Entries.Select(e => e.Type));
    }

    [Fact]
    public async Task AnOperatorSeesSettingsChanges_AndNotBans()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Ban("usr_a", "usr_mod", Day), ct);
        await host.WriteFactAsync(SettingsChanged(Day.AddMinutes(1)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit", cookie, ct);

        // The operator's own sign-in is in this log too now (accounts and access design §6), so
        // the assertion is about the split rather than about the exact list.
        var types = page.Entries.Select(e => e.Type).ToList();
        Assert.Contains(FactType.SettingsChanged, types);
        Assert.Contains(FactType.Login, types);
        Assert.DoesNotContain(FactType.MemberBanned, types);
    }

    [Fact]
    public async Task AskingForATypeYouCannotSee_DoesNotReturnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(SettingsChanged(Day), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);

        // The server is the enforcement point, never the SPA. A caller can name any type they
        // like in the query string; the answer is filtered regardless.
        var page = await host.GetJsonAsync<AuditPage>(
            "/api/audit?type=SettingsChanged", cookie, ct);

        Assert.Empty(page.Entries);
    }

    [Fact]
    public async Task NeitherPermission_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, ct);
        var response = await host.GetAsync("/api/audit", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnauthenticatedCaller_Is401()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync("/api/audit", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AWindowedFact_ReportsItsWindow_RatherThanAnInstant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(
            new FactRecord
            {
                Type = FactType.MemberLeft,
                OccurredAt = Day,
                // Spec 5.3: a sync diff knows only that they left between two polls.
                OccurredBefore = Day.AddMinutes(5),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = "usr_gone",
                Source = FactSource.SyncDiff,
            },
            ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit", cookie, ct);

        var entry = Assert.Single(page.Entries);

        Assert.Equal(TimePrecision.Window, entry.Precision);
        Assert.Equal(Day.AddMinutes(5), entry.OccurredBefore);
    }

    [Fact]
    public async Task AnAuditLogFact_ReportsAnExactTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Ban("usr_a", "usr_mod", Day), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit", cookie, ct);

        var entry = Assert.Single(page.Entries);

        Assert.Equal(TimePrecision.Exact, entry.Precision);
        Assert.Null(entry.OccurredBefore);
        Assert.Equal("RedZu", entry.ActorName);
    }

    [Fact]
    public async Task PagingByKeyset_ReturnsEveryEntryExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        // All at the same instant on purpose: VRChat's audit entries share timestamps freely, and
        // a cursor on time alone would drop every entry sharing the page boundary's second.
        for (var i = 0; i < 7; i++)
            await host.WriteFactAsync(Ban($"usr_{i}", "usr_mod", Day), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);

        var seen = new List<long>();
        var path = "/api/audit?limit=3";

        for (var page = 0; page < 5; page++)
        {
            var result = await host.GetJsonAsync<AuditPage>(path, cookie, ct);
            seen.AddRange(result.Entries.Select(e => e.Id));

            if (result.Next is null)
                break;

            path = $"/api/audit?limit=3&beforeOccurredAt={Uri.EscapeDataString(result.Next.OccurredAt.ToString("o"))}"
                + $"&beforeId={result.Next.Id}";
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task FilteringBySubject_NarrowsToThatPerson()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Ban("usr_a", "usr_mod", Day), ct);
        await host.WriteFactAsync(Ban("usr_b", "usr_mod", Day.AddMinutes(1)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?subject=usr_a", cookie, ct);

        Assert.Equal(["usr_a"], page.Entries.Select(e => e.SubjectId));
    }

    [Fact]
    public async Task AnIdWithNoVRChatShape_IsMatchedRatherThanRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        // Spec 3.1.1: legacy ids follow no structure. An id that does not start with "usr_" is a
        // legacy id, not a malformed request.
        await host.WriteFactAsync(Ban("8JoV9XEdpo", "usr_mod", Day), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?subject=8JoV9XEdpo", cookie, ct);

        Assert.Single(page.Entries);
    }

    [Fact]
    public async Task Filters_OfferOnlyWhatTheCallerMayRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Ban("usr_a", "usr_mod", host.Clock.UtcNow.AddDays(-1)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var filters = await host.GetJsonAsync<AuditFilters>("/api/audit/filters", cookie, ct);

        Assert.True(filters.CanViewModeration);
        Assert.False(filters.CanViewOperational);
        Assert.DoesNotContain(filters.Types, t => t.Value == FactType.SettingsChanged);
        Assert.Contains(filters.Actors, a => a.Id == "usr_mod" && a.Name == "RedZu");
    }

    [Fact]
    public async Task Coverage_ReportsWhereTheTimelineActuallyStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var old = Day.AddYears(-1);
        await host.WriteFactAsync(Ban("usr_a", "usr_mod", old), ct);
        await host.WriteFactAsync(Ban("usr_b", "usr_mod", Day), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit", cookie, ct);

        Assert.Equal(old, page.Coverage.OldestFact);

        // observed_at is stamped by the server from IModbotClock when the fact is written, so the
        // entry read during the catch-up from a year ago was still first learned of today.
        Assert.Equal(host.Clock.UtcNow, page.Coverage.FirstObservedAt);
        Assert.False(page.Coverage.CatchUpComplete);
    }
}
