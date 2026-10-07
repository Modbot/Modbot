using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Live.Stream;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// The two member-report facts (<c>modbot.report.open</c> and <c>modbot.report.close</c>) about a
/// staff account are shown only to somebody who holds Review tickets as well as See the operational
/// log, wherever facts are shown: the audit log, a person's timeline, the event feed and the live
/// stream (Discord commands design §17). Facts about anybody else, and every other operational
/// fact, are unaffected.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StaffReportFactVisibilityTests
{
    private const string Staff = "200000000000000009";
    private const string Ordinary = "200000000000000002";

    private static readonly string[] ReportTypes = [FactType.MemberReportOpened, FactType.MemberReportClosed];

    /// <summary>The operational log, and nothing that reviews.</summary>
    private const ModbotPermissions OperationalLogOnly = ModbotPermissions.ViewOperationalLog | ModbotPermissions.ViewProfile;

    /// <summary>The operational log, and Review tickets.</summary>
    private const ModbotPermissions OperationalLogAndReviews = OperationalLogOnly | ModbotPermissions.ReviewTickets;

    private readonly PostgresFixture _db;

    public StaffReportFactVisibilityTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A staff account whose proven Discord id is <see cref="Staff"/>; both report facts about it and
    /// about <see cref="Ordinary"/>; and one other operational fact about the staff account.
    /// </summary>
    private static async Task SeedAsync(ReadSurfaceTestHost host)
    {
        await host.ResetAsync(Ct);

        var account = await host.CreateUserAsync($"staff_{Guid.NewGuid():N}", "hunter2", ModbotPermissions.ViewMembers, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var row = await db.Users.FirstAsync(u => u.Id == account.Id, Ct);
            row.DiscordUserId = Staff;
            row.DiscordVerifiedAt = host.Clock.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        foreach (var subject in new[] { Staff, Ordinary })
        {
            foreach (var type in ReportTypes)
                await host.WriteFactAsync(Fact(host, type, subject), Ct);
        }

        await host.WriteFactAsync(Fact(host, FactType.DiscordLinkPrompted, Staff), Ct);
    }

    private static FactRecord Fact(ReadSurfaceTestHost host, string type, string subject) => new()
    {
        Type = type,
        OccurredAt = host.Clock.UtcNow,
        SubjectPlatform = FactPlatform.Discord,
        SubjectId = subject,
        Source = FactSource.Modbot,
        Data = new JsonObject { ["reportId"] = Guid.NewGuid().ToString() },
    };

    private static int ReportFactsAbout(IEnumerable<AuditEntry> entries, string subject)
        => entries.Count(e => ReportTypes.Contains(e.Type) && e.SubjectId == subject);

    [Fact]
    public async Task TheAuditLog_AndAPersonsTimeline_LeaveOutReportFactsAboutStaff_ForTheOperationalLogWithoutReviewTickets()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await SeedAsync(host);

        var plain = await host.SignedInAsync(OperationalLogOnly, Ct);
        var reviewer = await host.SignedInAsync(OperationalLogAndReviews, Ct);
        var admin = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        foreach (var path in new[]
                 {
                     "/api/audit",
                     $"/api/audit?subject={Staff}&subjectPlatform=Discord",
                     $"/api/audit?person={Staff}&personPlatform=Discord",
                 })
        {
            // No report fact about the staff account, and still the staff account's other fact.
            var seenByPlain = await host.GetJsonAsync<AuditPage>(path, plain, Ct);
            Assert.Equal(0, ReportFactsAbout(seenByPlain.Entries, Staff));
            Assert.Contains(seenByPlain.Entries, e => e.Type == FactType.DiscordLinkPrompted && e.SubjectId == Staff);

            // Asking for the types by name does not get past it either.
            var asked = await host.GetJsonAsync<AuditPage>(
                $"{path}{(path.Contains('?') ? '&' : '?')}type={FactType.MemberReportOpened}&type={FactType.MemberReportClosed}", plain, Ct);
            Assert.Equal(0, ReportFactsAbout(asked.Entries, Staff));

            // Both with Review tickets, and as an administrator.
            foreach (var cookie in new[] { reviewer, admin })
            {
                var seen = await host.GetJsonAsync<AuditPage>(path, cookie, Ct);
                Assert.Equal(2, ReportFactsAbout(seen.Entries, Staff));
            }
        }

        // A report about somebody who is not a staff account is still there for the operational log.
        var about = await host.GetJsonAsync<AuditPage>($"/api/audit?subject={Ordinary}&subjectPlatform=Discord", plain, Ct);
        Assert.Equal(2, ReportFactsAbout(about.Entries, Ordinary));

        var all = await host.GetJsonAsync<AuditPage>("/api/audit", plain, Ct);
        Assert.Equal(2, ReportFactsAbout(all.Entries, Ordinary));
    }

    [Fact]
    public async Task OneEntry_AndAroundIt_AreNotFound_ForAReportFactAboutStaff_WithoutReviewTickets()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await SeedAsync(host);

        var plain = await host.SignedInAsync(OperationalLogOnly, Ct);
        var reviewer = await host.SignedInAsync(OperationalLogAndReviews, Ct);

        long staffFact, ordinaryFact, otherFact;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            staffFact = await db.Events.Where(e => e.Type == FactType.MemberReportOpened && e.SubjectId == Staff).Select(e => e.Id).SingleAsync(Ct);
            ordinaryFact = await db.Events.Where(e => e.Type == FactType.MemberReportOpened && e.SubjectId == Ordinary).Select(e => e.Id).SingleAsync(Ct);
            otherFact = await db.Events.Where(e => e.Type == FactType.DiscordLinkPrompted && e.SubjectId == Staff).Select(e => e.Id).SingleAsync(Ct);
        }

        foreach (var suffix in new[] { string.Empty, "/around" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/audit/entries/{staffFact}{suffix}", plain, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/audit/entries/{staffFact}{suffix}", reviewer, Ct)).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/audit/entries/{ordinaryFact}{suffix}", plain, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/audit/entries/{otherFact}{suffix}", plain, Ct)).StatusCode);
        }

        // "Around" a fact the caller can read does not list the report facts about staff beside it.
        var around = await host.GetJsonAsync<AuditAround>($"/api/audit/entries/{otherFact}/around", plain, Ct);
        Assert.Equal(0, ReportFactsAbout(around.AboutSubject ?? [], Staff));

        var aroundForReviewer = await host.GetJsonAsync<AuditAround>($"/api/audit/entries/{otherFact}/around", reviewer, Ct);
        Assert.Equal(2, ReportFactsAbout(aroundForReviewer.AboutSubject ?? [], Staff));
    }

    [Fact]
    public async Task TheEventFeed_LeavesOutReportFactsAboutStaff_ForTheOperationalLogWithoutReviewTickets()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await SeedAsync(host);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        Assert.True(EventFilter.TryCreate(null, null, out var everything, out _));

        async Task<IReadOnlyList<ModbotEvent>> ReadAsync(ModbotPermissions held)
            => (await EventPollEndpoint.ReadAsync(
                db, new FactFeed(db), 0, 100, everything, held, host.Clock.UtcNow, 100, 3, Ct)).Events;

        var plain = await ReadAsync(OperationalLogOnly);
        Assert.DoesNotContain(plain, e => ReportTypes.Contains(e.Type) && e.SubjectId == Staff);
        Assert.Equal(2, plain.Count(e => ReportTypes.Contains(e.Type) && e.SubjectId == Ordinary));
        Assert.Contains(plain, e => e.Type == FactType.DiscordLinkPrompted && e.SubjectId == Staff);

        foreach (var held in new[] { OperationalLogAndReviews, ModbotPermissions.Administrator })
        {
            var seen = await ReadAsync(held);
            Assert.Equal(2, seen.Count(e => ReportTypes.Contains(e.Type) && e.SubjectId == Staff));
            Assert.Equal(2, seen.Count(e => ReportTypes.Contains(e.Type) && e.SubjectId == Ordinary));
        }

        // The single-fact check the socket and webhooks use agrees.
        var staffFact = await db.Events.AsNoTracking().FirstAsync(e => e.Type == FactType.MemberReportClosed && e.SubjectId == Staff, Ct);
        Assert.False(await EventVisibility.CanSeeAsync(db, OperationalLogOnly, staffFact, host.Clock.UtcNow, Ct));
        Assert.True(await EventVisibility.CanSeeAsync(db, OperationalLogAndReviews, staffFact, host.Clock.UtcNow, Ct));

        var ordinaryFact = await db.Events.AsNoTracking().FirstAsync(e => e.Type == FactType.MemberReportClosed && e.SubjectId == Ordinary, Ct);
        Assert.True(await EventVisibility.CanSeeAsync(db, OperationalLogOnly, ordinaryFact, host.Clock.UtcNow, Ct));
    }

    [Fact]
    public async Task TheLiveStream_LeavesOutReportFactsAboutStaff_ForTheOperationalLogWithoutReviewTickets()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await SeedAsync(host);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var reader = new LiveReader(db);

        async Task<IReadOnlyList<LiveEvent>> ReadAsync(ModbotPermissions held)
            => (await reader.ReadAsync(0, 100, LiveScope.ForPerson(held), host.Clock.UtcNow, 100, 3, Ct)).Events;

        var plain = await ReadAsync(OperationalLogOnly);
        Assert.DoesNotContain(plain, e => ReportTypes.Contains(e.Type) && e.Subject.Id == Staff);
        Assert.Equal(2, plain.Count(e => ReportTypes.Contains(e.Type) && e.Subject.Id == Ordinary));
        Assert.Contains(plain, e => e.Type == FactType.DiscordLinkPrompted && e.Subject.Id == Staff);

        foreach (var held in new[] { OperationalLogAndReviews, ModbotPermissions.Administrator })
        {
            var seen = await ReadAsync(held);
            Assert.Equal(2, seen.Count(e => ReportTypes.Contains(e.Type) && e.Subject.Id == Staff));
            Assert.Equal(2, seen.Count(e => ReportTypes.Contains(e.Type) && e.Subject.Id == Ordinary));
        }
    }
}
