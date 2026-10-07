using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Reports;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Reports;

/// <summary>
/// The Reports surface (Discord commands design §3.4): See reports reads and counts, Handle reports
/// closes, a report about a staff account needs Review tickets as well, the reporter is shown to
/// everybody who may read, and closing needs a note and writes a fact without the note or the reporter.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ReportsApiTests
{
    private const string Reporter = "100000000000000001";
    private const string Reported = "200000000000000002";
    private const string StaffDiscord = "300000000000000003";

    private readonly PostgresFixture _db;

    public ReportsApiTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<Guid> AddAsync(
        ReadSurfaceTestHost host,
        string reported = Reported,
        string reporter = Reporter,
        string state = MemberReportStates.Open,
        DateTimeOffset? at = null,
        bool withMessage = true)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var report = new MemberReport
        {
            ReporterDiscordId = reporter,
            ReporterName = "Quillfeather",
            ReportedDiscordId = reported,
            ReportedName = "Reported",
            Text = "they were rude",
            MessageId = withMessage ? Guid.NewGuid().ToString("N") : null,
            MessageChannelId = withMessage ? "55" : null,
            MessageChannelName = withMessage ? "general" : null,
            MessageSentAt = withMessage ? host.Clock.UtcNow : null,
            MessageText = withMessage ? "the quoted words" : null,
            MessageAttachments = withMessage ? ["cat.png"] : null,
            MessageUrl = withMessage ? "https://discord.com/channels/1/55/77" : null,
            State = state,
            CreatedAt = at ?? host.Clock.UtcNow,
            ClosedAt = state == MemberReportStates.Closed ? host.Clock.UtcNow : null,
            ClosedByUsername = state == MemberReportStates.Closed ? "alice" : null,
            CloseNote = state == MemberReportStates.Closed ? "done" : null,
        };

        db.MemberReports.Add(report);
        await db.SaveChangesAsync(Ct);
        return report.Id;
    }

    /// <summary>A staff account whose Discord account is proven, so a report about that id is a report about staff.</summary>
    private static async Task MakeStaffAsync(ReadSurfaceTestHost host, string discordId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var user = await TestAccounts.CreateAsync(db, "staffer_" + Guid.NewGuid().ToString("N")[..6], TestAccounts.Password, ModbotPermissions.ViewMembers, linked: true, Ct);
        user.DiscordUserId = discordId;
        user.DiscordVerifiedAt = host.Clock.UtcNow;
        await db.SaveChangesAsync(Ct);
    }

    // ── Permissions ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/reports")]
    [InlineData("/api/reports/open-count")]
    public async Task AnUnauthenticatedCaller_Gets401(string path)
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(path, Ct)).StatusCode);
    }

    [Fact]
    public async Task WithoutSeeReports_Is403_EvenForSomebodyWhoCanReadEverythingElse_AndHandleReportsAloneIsNotEnough()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var id = await AddAsync(host);

        var reader = await host.SignedInAsync(
            ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewProfile | ModbotPermissions.ReviewTickets | ModbotPermissions.WriteNotes, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/reports", reader, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/reports/open-count", reader, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync($"/api/reports/{id}/close", new { note = "x" }, reader, Ct)).StatusCode);

        var handlerOnly = await host.SignedInAsync(ModbotPermissions.HandleReports, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/reports", handlerOnly, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync($"/api/reports/{id}/close", new { note = "x" }, handlerOnly, Ct)).StatusCode);
    }

    [Fact]
    public async Task SeeReportsReads_ButCannotClose()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var id = await AddAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewReports, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/reports", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync($"/api/reports/{id}/close", new { note = "x" }, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheBuiltInModeratorRoleHoldsBoth_AndTheViewerHoldsNeither()
    {
        Assert.True(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.ViewReports));
        Assert.True(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.HandleReports));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.ViewReports));
        Assert.False(BuiltInRoles.ViewerPermissions.HasFlag(ModbotPermissions.HandleReports));
        Assert.False(BuiltInRoles.ModeratorPermissions.HasFlag(ModbotPermissions.ReviewTickets));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // A moderator, as the role gives it, can read and close.
        var cookie = await host.SignedInAsync(BuiltInRoles.ModeratorPermissions, Ct);
        var id = await AddAsync(host);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/reports", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"/api/reports/{id}/close", new { note = "handled" }, cookie, Ct)).StatusCode);
    }

    // ── The list ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheList_ShowsWhoReported_AndTheQuotedMessage_OldestOpenFirst_WithTheOpenCount()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var older = await AddAsync(host, at: host.Clock.UtcNow.AddHours(-2));
        var newer = await AddAsync(host, reporter: "400000000000000004", at: host.Clock.UtcNow.AddHours(-1), withMessage: false);
        await AddAsync(host, reporter: "500000000000000005", state: MemberReportStates.Closed);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewReports, Ct);
        var list = await host.GetJsonAsync<MemberReportList>("/api/reports", cookie, Ct);

        Assert.Equal(2, list.OpenCount);
        Assert.Equal([older, newer], list.Reports.Select(r => r.Id).ToList());

        var first = list.Reports[0];
        Assert.Equal(Reporter, first.Reporter.DiscordId);
        Assert.Equal("Quillfeather", first.Reporter.Name);
        Assert.Equal(Reported, first.About.DiscordId);
        Assert.Equal("they were rude", first.Text);
        Assert.Equal("open", first.State);
        Assert.Equal("the quoted words", first.Message!.Text);
        Assert.Equal("general", first.Message.ChannelName);
        Assert.Equal(["cat.png"], first.Message.Attachments);
        Assert.Equal("https://discord.com/channels/1/55/77", first.Message.Url);

        Assert.Null(list.Reports[1].Message);

        var closed = await host.GetJsonAsync<MemberReportList>("/api/reports?state=closed", cookie, Ct);
        var one = Assert.Single(closed.Reports);
        Assert.Equal("closed", one.State);
        Assert.Equal("done", one.CloseNote);
        Assert.Equal("alice", one.ClosedByUsername);
        Assert.Equal(2, closed.OpenCount);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/reports?state=all", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task AReportWhoseTextWasRemoved_ComesBackWithNoTextAndNoMessage_AndItsRecord()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var id = await AddAsync(host, state: MemberReportStates.Closed);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            await MemberReportsAccessTestHelper.RemoveWordsAsync(db, id, host.Clock.UtcNow, Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewReports, Ct);
        var closed = await host.GetJsonAsync<MemberReportList>("/api/reports?state=closed", cookie, Ct);

        var view = Assert.Single(closed.Reports);
        Assert.Null(view.Text);
        Assert.Null(view.Message);
        Assert.NotNull(view.TextRemovedAt);
        Assert.Equal(Reporter, view.Reporter.DiscordId);
        Assert.Equal(Reported, view.About.DiscordId);
    }

    // ── Reports about staff ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AReportAboutAStaffAccount_IsShownOnlyToSomebodyWhoAlsoHoldsReviewTickets()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await MakeStaffAsync(host, StaffDiscord);

        var ordinary = await AddAsync(host);
        var aboutStaff = await AddAsync(host, reported: StaffDiscord, reporter: "400000000000000004");

        var plain = await host.SignedInAsync(ModbotPermissions.ViewReports | ModbotPermissions.HandleReports, Ct);
        var reviewer = await host.SignedInAsync(ModbotPermissions.ViewReports | ModbotPermissions.HandleReports | ModbotPermissions.ReviewTickets, Ct);
        var admin = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var plainList = await host.GetJsonAsync<MemberReportList>("/api/reports", plain, Ct);
        Assert.Equal([ordinary], plainList.Reports.Select(r => r.Id).ToList());
        Assert.Equal(1, plainList.OpenCount);
        Assert.Equal(1, (await host.GetJsonAsync<OpenReportCount>("/api/reports/open-count", plain, Ct)).Open);

        foreach (var cookie in new[] { reviewer, admin })
        {
            var list = await host.GetJsonAsync<MemberReportList>("/api/reports", cookie, Ct);
            Assert.Equal(2, list.Reports.Count);
            Assert.Equal(2, list.OpenCount);
            Assert.Equal(2, (await host.GetJsonAsync<OpenReportCount>("/api/reports/open-count", cookie, Ct)).Open);
        }

        // Closing one the caller may not see is answered as if it were not there, and changes nothing.
        var refused = await host.PostJsonAsync($"/api/reports/{aboutStaff}/close", new { note = "closing it anyway" }, plain, Ct);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            Assert.Equal(MemberReportStates.Open, (await db.MemberReports.AsNoTracking().FirstAsync(r => r.Id == aboutStaff, Ct)).State);
        }

        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"/api/reports/{aboutStaff}/close", new { note = "reviewed" }, reviewer, Ct)).StatusCode);
    }

    // ── Closing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Closing_NeedsANote_RecordsWho_WritesAFactWithoutTheNoteOrTheReporter_AndOnlyOnce()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var id = await AddAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewReports | ModbotPermissions.HandleReports, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostJsonAsync($"/api/reports/{id}/close", new { note = "   " }, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostJsonAsync($"/api/reports/{id}/close", new { note = new string('x', 2001) }, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostJsonAsync($"/api/reports/{Guid.NewGuid()}/close", new { note = "x" }, cookie, Ct)).StatusCode);

        var closed = await host.PostJsonAsync($"/api/reports/{id}/close", new { note = "Spoke to Quillfeather and the member." }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var view = JsonSerializer.Deserialize<MemberReportView>(await closed.Content.ReadAsStringAsync(Ct), Web)!;
        Assert.Equal("closed", view.State);
        Assert.Equal("Spoke to Quillfeather and the member.", view.CloseNote);
        Assert.False(string.IsNullOrEmpty(view.ClosedByUsername));

        Assert.Equal(HttpStatusCode.Conflict, (await host.PostJsonAsync($"/api/reports/{id}/close", new { note = "again" }, cookie, Ct)).StatusCode);

        // Gone from the open list and the count.
        var open = await host.GetJsonAsync<MemberReportList>("/api/reports", cookie, Ct);
        Assert.Empty(open.Reports);
        Assert.Equal(0, open.OpenCount);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var fact = await db.Events.AsNoTracking().SingleAsync(e => e.Type == FactType.MemberReportClosed, Ct);

        Assert.Equal(Reported, fact.SubjectId);
        Assert.NotNull(fact.ActorId);
        Assert.Contains(id.ToString(), fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("Spoke to", fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("Quillfeather", fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(Reporter, fact.Data, StringComparison.Ordinal);
    }
}

/// <summary>What retention does to a row, for a test that reads it through the API.</summary>
internal static class MemberReportsAccessTestHelper
{
    public static async Task RemoveWordsAsync(ModbotContext db, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var report = await db.MemberReports.FirstAsync(r => r.Id == id, ct);
        report.Text = null;
        report.MessageId = null;
        report.MessageChannelId = null;
        report.MessageChannelName = null;
        report.MessageSentAt = null;
        report.MessageText = null;
        report.MessageAttachments = null;
        report.MessageUrl = null;
        report.TextRemovedAt = now;
        await db.SaveChangesAsync(ct);
    }
}
