using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.DailyTotals;
using Modbot.Api.Features.Appeals;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Appeals;

/// <summary>
/// Appeal tracking and the yearly report (batch 3, step G): who may write an appeal down and who may
/// answer it, what each write leaves in the log, and the numbers a year adds up to.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AppealTests
{
    private const string Person = "usr_appealed";
    private const string Other = "usr_somebody_else";

    private static readonly DateTimeOffset Day = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const ModbotPermissions Reader = ModbotPermissions.ViewProfile;
    private const ModbotPermissions Staff = ModbotPermissions.ViewProfile | ModbotPermissions.Unban;

    private readonly PostgresFixture _db;

    public AppealTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private async Task<ReadSurfaceTestHost> ReadyAsync()
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        host.Clock.UtcNow = Day;

        return host;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(Ct), Web)!;

    private static Task<HttpResponseMessage> LogAsync(
        ReadSurfaceTestHost host,
        string cookie,
        string? userId = Person,
        DateOnly? receivedOn = null,
        Guid? caseFileId = null,
        string? note = null)
        => host.PostJsonAsync("/api/appeals", new { userId, receivedOn, caseFileId, note }, cookie, Ct);

    private static Task<HttpResponseMessage> DecideAsync(ReadSurfaceTestHost host, string cookie, Guid id, string outcome)
        => host.PostJsonAsync($"/api/appeals/{id}/decide", new { outcome }, cookie, Ct);

    private static async Task<AppealView> LoggedAsync(
        ReadSurfaceTestHost host, string cookie, string userId = Person, DateOnly? receivedOn = null)
    {
        var response = await LogAsync(host, cookie, userId, receivedOn);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await ReadAsync<AppealView>(response);
    }

    private static async Task<List<ModbotEvent>> FactsAsync(ReadSurfaceTestHost host, string type)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.Events.AsNoTracking()
            .Where(e => e.Type == type)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    private static async Task<Guid> CaseFileAsync(ReadSurfaceTestHost host, string userId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var file = new CaseFile
        {
            UserId = userId,
            AuthorUserId = Guid.CreateVersion7(),
            AuthorUsername = "mod",
            WrittenReason = "The group's own record of its own decision.",
            CreatedAt = host.Clock.UtcNow,
            UpdatedAt = host.Clock.UtcNow,
            SnapshotTakenAt = host.Clock.UtcNow,
        };

        db.CaseFiles.Add(file);
        await db.SaveChangesAsync(Ct);

        return file.Id;
    }

    private static async Task CountedAsync(ReadSurfaceTestHost host, string metric, DateOnly day, string dimension, decimal value)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.DailyTotals.Add(new DailyTotal
        {
            Day = day,
            Metric = metric,
            Dimension = dimension,
            Value = value,
            Origin = DailyTotalOrigin.Counted,
        });

        await db.SaveChangesAsync(Ct);
    }

    // ── Logging ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Logging_NeedsUnban_AndSeeingProfilesAloneIsRefused()
    {
        await using var host = await ReadyAsync();

        var reader = await host.SignedInAsync(Reader, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await LogAsync(host, reader)).StatusCode);
        Assert.Empty(await FactsAsync(host, FactType.AppealLogged));

        var staff = await host.SignedInAsync(Staff, Ct);
        Assert.Equal(HttpStatusCode.OK, (await LogAsync(host, staff)).StatusCode);
    }

    [Fact]
    public async Task Logging_KeepsTheAppeal_WritesOneFact_AndDefaultsToToday()
    {
        await using var host = await ReadyAsync();
        var cookie = await host.SignedInAsync(Staff, Ct);
        var caseFileId = await CaseFileAsync(host, Person);

        var response = await LogAsync(host, cookie, caseFileId: caseFileId, note: "  Says it was a mistake  ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var appeal = await ReadAsync<AppealView>(response);
        Assert.Equal(Person, appeal.UserId);
        Assert.Equal(new DateOnly(2026, 10, 13), appeal.ReceivedOn);
        Assert.Equal("pending", appeal.Outcome);
        Assert.Equal(caseFileId, appeal.CaseFileId);
        Assert.Equal("Says it was a mistake", appeal.Note);
        Assert.True(appeal.CanDecide);

        var fact = Assert.Single(await FactsAsync(host, FactType.AppealLogged));
        Assert.Equal(Person, fact.SubjectId);
        Assert.Equal(FactSource.Manual, fact.Source);
        Assert.Contains("Says it was a mistake", fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logging_RefusesWhatCannotBeTrue_AndLeavesNothingBehind()
    {
        await using var host = await ReadyAsync();
        var cookie = await host.SignedInAsync(Staff, Ct);
        var elsewhere = await CaseFileAsync(host, Other);

        // A day to come.
        Assert.Equal(HttpStatusCode.BadRequest, (await LogAsync(host, cookie, receivedOn: new DateOnly(2026, 10, 14))).StatusCode);

        // A note over the limit.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await LogAsync(host, cookie, note: new string('x', Appeal.MaxNoteLength + 1))).StatusCode);

        // A case file about somebody else, and one that does not exist.
        Assert.Equal(HttpStatusCode.BadRequest, (await LogAsync(host, cookie, caseFileId: elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await LogAsync(host, cookie, caseFileId: Guid.NewGuid())).StatusCode);

        // No person.
        Assert.Equal(HttpStatusCode.BadRequest, (await LogAsync(host, cookie, userId: "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LogAsync(host, cookie, userId: "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LogAsync(host, cookie, userId: null)).StatusCode);

        var list = await host.GetJsonAsync<AppealList>("/api/appeals", cookie, Ct);
        Assert.Empty(list.Appeals);
        Assert.Empty(await FactsAsync(host, FactType.AppealLogged));
    }

    // ── Deciding ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deciding_WorksOnce_AndASecondDecisionGets409()
    {
        await using var host = await ReadyAsync();
        var staff = await host.SignedInAsync(Staff, Ct);
        var appeal = await LoggedAsync(host, staff);

        var first = await DecideAsync(host, staff, appeal.Id, "upheld");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var decided = await ReadAsync<AppealView>(first);
        Assert.Equal("upheld", decided.Outcome);
        Assert.NotNull(decided.DecidedAt);
        Assert.False(decided.CanDecide);

        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(host, staff, appeal.Id, "turned-down")).StatusCode);

        // The first answer stands, and only it was written down.
        var read = await host.GetJsonAsync<AppealList>($"/api/appeals?userId={Person}", staff, Ct);
        Assert.Equal("upheld", Assert.Single(read.Appeals).Outcome);

        var fact = Assert.Single(await FactsAsync(host, FactType.AppealDecided));
        Assert.Equal(Person, fact.SubjectId);
        Assert.Contains("upheld", fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deciding_AtTheSameTime_LetsOneWin_AndWritesOneFact()
    {
        await using var host = await ReadyAsync();
        var staff = await host.SignedInAsync(Staff, Ct);
        var appeal = await LoggedAsync(host, staff);

        var both = await Task.WhenAll(
            DecideAsync(host, staff, appeal.Id, "upheld"),
            DecideAsync(host, staff, appeal.Id, "turned-down"));

        Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(both, r => r.StatusCode == HttpStatusCode.Conflict);

        var winner = await ReadAsync<AppealView>(both.Single(r => r.StatusCode == HttpStatusCode.OK));
        var read = await host.GetJsonAsync<AppealList>($"/api/appeals?userId={Person}", staff, Ct);
        Assert.Equal(winner.Outcome, Assert.Single(read.Appeals).Outcome);

        Assert.Single(await FactsAsync(host, FactType.AppealDecided));
    }

    [Fact]
    public async Task Deciding_NeedsUnban_AndNeedsAnOutcomeAndAnAppeal()
    {
        await using var host = await ReadyAsync();
        var staff = await host.SignedInAsync(Staff, Ct);
        var reader = await host.SignedInAsync(Reader, Ct);
        var appeal = await LoggedAsync(host, staff);

        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(host, reader, appeal.Id, "upheld")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(host, staff, appeal.Id, "maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(host, staff, Guid.NewGuid(), "upheld")).StatusCode);
        Assert.Empty(await FactsAsync(host, FactType.AppealDecided));

        // Somebody who only reads sees it, and is told they cannot answer.
        var seen = await host.GetJsonAsync<AppealList>("/api/appeals", reader, Ct);
        Assert.False(seen.CanLog);
        Assert.False(Assert.Single(seen.Appeals).CanDecide);
    }

    // ── Reading ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheList_NarrowsByPerson_Year_AndOutcome()
    {
        await using var host = await ReadyAsync();
        var staff = await host.SignedInAsync(Staff, Ct);

        var upheld = await LoggedAsync(host, staff, Person, new DateOnly(2025, 6, 1));
        await DecideAsync(host, staff, upheld.Id, "upheld");
        await LoggedAsync(host, staff, Person, new DateOnly(2026, 2, 1));
        await LoggedAsync(host, staff, Other, new DateOnly(2026, 3, 1));

        var all = await host.GetJsonAsync<AppealList>("/api/appeals", staff, Ct);
        Assert.Equal(3, all.Total);
        Assert.Equal(2, all.Waiting);
        Assert.Equal(new DateOnly(2026, 3, 1), all.Appeals[0].ReceivedOn);

        Assert.Equal(2, (await host.GetJsonAsync<AppealList>($"/api/appeals?userId={Person}", staff, Ct)).Total);
        Assert.Equal(2, (await host.GetJsonAsync<AppealList>("/api/appeals?year=2026", staff, Ct)).Total);

        var onlyUpheld = await host.GetJsonAsync<AppealList>("/api/appeals?outcome=upheld", staff, Ct);
        Assert.Equal(upheld.Id, Assert.Single(onlyUpheld.Appeals).Id);

        Assert.Equal(2, (await host.GetJsonAsync<AppealList>("/api/appeals?outcome=pending", staff, Ct)).Total);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/appeals?outcome=nope", staff, Ct)).StatusCode);
    }

    [Fact]
    public async Task Reading_NeedsToSeeProfiles()
    {
        await using var host = await ReadyAsync();
        var nobody = await host.SignedInAsync(ModbotPermissions.WriteNotes, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/appeals", nobody, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/appeals/report", nobody, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/appeals/report.csv", nobody, Ct)).StatusCode);
    }

    // ── The yearly report ──────────────────────────────────────────────────────────────────

    private static async Task SeedAYearAsync(ReadSurfaceTestHost host, string cookie)
    {
        // Two moderators' warnings add up; the day outside the year does not count.
        await CountedAsync(host, DailyTotalMetrics.ModeratorWarns, new DateOnly(2026, 2, 1), "mod_a", 3);
        await CountedAsync(host, DailyTotalMetrics.ModeratorWarns, new DateOnly(2026, 5, 1), "mod_b", 4);
        await CountedAsync(host, DailyTotalMetrics.ModeratorWarns, new DateOnly(2025, 12, 31), "mod_a", 100);
        await CountedAsync(host, DailyTotalMetrics.ModeratorInstanceKicks, new DateOnly(2026, 1, 1), "mod_a", 6);
        await CountedAsync(host, DailyTotalMetrics.ModeratorRemovals, new DateOnly(2026, 12, 31), "mod_a", 2);
        await CountedAsync(host, DailyTotalMetrics.ModeratorBans, new DateOnly(2026, 3, 3), "mod_a", 5);
        await CountedAsync(host, DailyTotalMetrics.ModeratorUnbans, new DateOnly(2026, 4, 4), "mod_a", 1);

        var upheld = await LoggedAsync(host, cookie, Person, new DateOnly(2026, 3, 1));
        var turnedDown = await LoggedAsync(host, cookie, Other, new DateOnly(2026, 2, 2));
        await LoggedAsync(host, cookie, "usr_third", new DateOnly(2025, 12, 30));

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(host, cookie, upheld.Id, "upheld")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(host, cookie, turnedDown.Id, "turned-down")).StatusCode);
    }

    private static long CountOf(YearlyReport report, string key) => report.Lines.Single(l => l.Key == key).Count;

    [Fact]
    public async Task TheReport_SumsTheDailyTotals_AndTheYearsAppeals()
    {
        await using var host = await ReadyAsync();
        var cookie = await host.SignedInAsync(Staff, Ct);
        await SeedAYearAsync(host, cookie);

        var report = await host.GetJsonAsync<YearlyReport>("/api/appeals/report?year=2026", cookie, Ct);

        Assert.Equal(2026, report.Year);
        Assert.Equal(7, CountOf(report, "warns"));
        Assert.Equal(6, CountOf(report, "instance-kicks"));
        Assert.Equal(2, CountOf(report, "removals"));
        Assert.Equal(5, CountOf(report, "bans"));
        Assert.Equal(1, CountOf(report, "unbans"));
        Assert.Equal(2, CountOf(report, "appeals-received"));
        Assert.Equal(1, CountOf(report, "appeals-upheld"));
        Assert.Equal(1, CountOf(report, "appeals-turned-down"));
        Assert.Equal(new DateOnly(2025, 12, 31), report.CountedSince);
        Assert.Equal(2026, report.Years[0]);
        Assert.Contains(2025, report.Years);

        // With no year it is this year.
        Assert.Equal(2026, (await host.GetJsonAsync<YearlyReport>("/api/appeals/report", cookie, Ct)).Year);

        // The year before has the day the other side of the new year, and the appeal received then.
        var before = await host.GetJsonAsync<YearlyReport>("/api/appeals/report?year=2025", cookie, Ct);
        Assert.Equal(100, CountOf(before, "warns"));
        Assert.Equal(1, CountOf(before, "appeals-received"));
        Assert.Equal(0, CountOf(before, "appeals-upheld"));
    }

    [Fact]
    public async Task TheReport_RefusesAYearThatIsNotOnTheList()
    {
        await using var host = await ReadyAsync();
        var cookie = await host.SignedInAsync(Staff, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/appeals/report?year=2027", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/appeals/report?year=1999", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/appeals/report.csv?year=2027", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheCsv_HasOneRowPerNumber()
    {
        await using var host = await ReadyAsync();
        var cookie = await host.SignedInAsync(Staff, Ct);
        await SeedAYearAsync(host, cookie);

        var response = await host.GetAsync("/api/appeals/report.csv?year=2026", cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("yearly-report-2026.csv", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var lines = (await response.Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            [
                "Year,Number,Count",
                "2026,Warnings,7",
                "2026,Instance kicks,6",
                "2026,Removed from group,2",
                "2026,Bans,5",
                "2026,Unbans,1",
                "2026,Appeals received,2",
                "2026,Appeals upheld,1",
                "2026,Appeals turned down,1",
            ],
            lines);
    }

    // ── Erasing a person ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APurge_CountsAndRemovesThePersonsAppeals_AndNobodyElses()
    {
        await using var host = await ReadyAsync();
        var staff = await host.SignedInAsync(Staff, Ct);
        await LoggedAsync(host, staff, Person);
        await LoggedAsync(host, staff, Person, new DateOnly(2026, 1, 5));
        await LoggedAsync(host, staff, Other);

        var admin = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var preview = await host.GetJsonAsync<PurgePreviewResponse>(
            $"/api/settings/purge?subjectId={Person}&platform=VRChat", admin, Ct);
        Assert.Equal(2, preview.Appeals);

        var purged = await host.PostJsonAsync(
            "/api/settings/purge", new { subjectId = Person, platform = "VRChat", confirmation = Person }, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);

        var left = await host.GetJsonAsync<AppealList>("/api/appeals", admin, Ct);
        Assert.Equal(Other, Assert.Single(left.Appeals).UserId);
    }
}
