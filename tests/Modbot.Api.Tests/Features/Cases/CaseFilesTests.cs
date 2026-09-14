using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Cases;
using Modbot.Api.Features.Evidence;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Cases;

/// <summary>
/// Case files: gated the way the design says, one per ban, the profile snapshot copied from what
/// Modbot holds without touching it, every write a fact, evidence cited by the case file id, and
/// the "unwritten" list the accountability signal will read.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CaseFilesTests
{
    private const string Group = "grp_1";
    private const string Banned = "usr_banned";
    private const string Moderator = "usr_gunner";

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public CaseFilesTests(PostgresFixture db) => _db = db;

    /// <summary>
    /// A ban recorded from the audit log an hour ago, and the person's rows as the syncs would
    /// have left them: a profile fetched two hours ago, a membership with a role, and the ban
    /// list's entry.
    /// </summary>
    private static async Task SeedBanAsync(ReadSurfaceTestHost host, CancellationToken ct, string entryId = "gaud_1")
    {
        host.Clock.UtcNow = Day;

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(ct);
            settings.ManagedGroupId = Group;

            db.VRChatUsers.Add(new VRChatUser
            {
                UserId = Banned,
                DisplayName = "GayHater59",
                Bio = "join my discord",
                Pronouns = "he/him",
                Tags = """["system_trust_known"]""",
                AgeVerificationStatus = "hidden",
                RawProfile = """{"id":"usr_banned","displayName":"GayHater59","bio":"join my discord"}""",
                FirstSeenAt = Day.AddDays(-30),
                LastSeenAt = Day.AddHours(-1),
                LastRefreshedAt = Day.AddHours(-2),
            });

            db.GroupMembers.Add(new GroupMember
            {
                GroupId = Group,
                UserId = Banned,
                Roles = """["grol_member"]""",
                JoinedAt = Day.AddDays(-20),
                MembershipStatus = "member",
                FirstSeenAt = Day.AddDays(-20),
                LastSeenAt = Day.AddHours(-3),
            });

            db.GroupBans.Add(new GroupBan
            {
                GroupId = Group,
                UserId = Banned,
                BannedAt = Day.AddHours(-1),
                FirstSeenAt = Day.AddMinutes(-30),
                LastSeenAt = Day.AddMinutes(-30),
            });

            await db.SaveChangesAsync(ct);
        }

        await host.WriteFactAsync(
            AuditFact(FactType.MemberBanned, Banned, Day.AddHours(-1), actor: Moderator, actorName: "Gunner24",
                extra: new JsonObject { ["auditEntryId"] = entryId }),
            ct);
    }

    private static async Task<List<BanReasonView>> ReasonsAsync(ReadSurfaceTestHost host, string cookie, CancellationToken ct)
        => (await host.GetJsonAsync<BanReasonListResponse>("/api/settings/ban-reasons", cookie, ct)).Reasons.ToList();

    private static async Task<CaseFileView> WriteAsync(
        ReadSurfaceTestHost host, string cookie, CancellationToken ct, string userId = Banned, string? entryId = null, string text = "Kept following people between instances shouting slurs.")
    {
        var reasons = await ReasonsAsync(host, cookie, ct);
        var harassment = reasons.Single(r => r.Label == "Harassment").Id;

        var response = await host.PostJsonAsync("/api/cases", new { userId, auditEntryId = entryId, reasonIds = new[] { harassment }, writtenReason = text }, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return Read<CaseFileCreatedResponse>(await response.Content.ReadAsStringAsync(ct)).Case;
    }

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Web)!;

    // ── Access ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnauthenticatedCaller_Gets401_OnEveryEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/cases", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/cases/missing", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/cases/lookup?userId=x", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync($"/api/cases/{id}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsync("/api/cases", JsonContent.Create(new { userId = "x" }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PutAsync($"/api/cases/{id}", JsonContent.Create(new { }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsync($"/api/cases/{id}/withdraw", JsonContent.Create(new { note = "x" }), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsync($"/api/cases/{id}/capture-again", null, ct)).StatusCode);
    }

    [Fact]
    public async Task ReadsNeedViewProfile_WritingNeedsBan()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SeedBanAsync(host, ct);

        var reader = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers | ModbotPermissions.Kick, ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/cases", reader, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/cases/missing", reader, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync($"/api/cases/lookup?userId={Banned}", reader, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync($"/api/cases/{Guid.NewGuid()}", reader, ct)).StatusCode);

        // ViewProfile reads, but cannot write without Ban.
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var reasons = await ReasonsAsync(host, viewer, ct);
        var refused = await host.PostJsonAsync("/api/cases", new { userId = Banned, reasonIds = new[] { reasons[0].Id }, writtenReason = "x" }, viewer, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // Somebody who may ban writes it; the viewer may read it but not change it.
        var banner = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var written = await WriteAsync(host, banner, ct);

        var seen = await host.GetJsonAsync<CaseFileView>($"/api/cases/{written.Id}", viewer, ct);
        Assert.False(seen.CanEdit);
        Assert.False(seen.CanAttach);
        Assert.False(seen.CanViewEvidence);
        Assert.Null(seen.Evidence);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync($"/api/cases/{written.Id}", new { reasonIds = new[] { reasons[0].Id }, writtenReason = "changed" }, viewer, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync($"/api/cases/{written.Id}/withdraw", new { note = "no" }, viewer, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync($"/api/cases/{written.Id}/capture-again", null, viewer, ct)).StatusCode);
    }

    // ── Writing ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Writing_LinksTheBan_TakesTheSnapshot_AsksForARefresh_AndIsAFact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SeedBanAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var reasons = await ReasonsAsync(host, cookie, ct);
        var harassment = reasons.Single(r => r.Label == "Harassment");
        var hate = reasons.Single(r => r.Label == "Hate speech");

        host.Clock.UtcNow = Day.AddMinutes(5);

        var response = await host.PostJsonAsync("/api/cases", new
        {
            userId = Banned,
            reasonIds = new[] { hate.Id, harassment.Id },
            writtenReason = "## What happened\n\nFollowed two members between instances shouting slurs. <script>alert(1)</script>",
        }, cookie, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = Read<CaseFileCreatedResponse>(await response.Content.ReadAsStringAsync(ct));
        var view = created.Case;

        // The ban it is about, found from the newest recorded ban of the person.
        Assert.Equal(Banned, view.UserId);
        Assert.Equal("GayHater59", view.DisplayName);
        Assert.Equal("gaud_1", view.AuditEntryId);
        Assert.NotNull(view.BanFactId);
        Assert.Equal(Day.AddHours(-1), view.BannedAt);
        Assert.Equal(Moderator, view.BannedBy?.Id);
        Assert.Equal("Gunner24", view.BannedBy?.Name);
        Assert.Equal(Group, view.GroupId);

        // Who wrote it, and what: reasons in button order, the text as typed.
        Assert.False(string.IsNullOrEmpty(view.AuthorUsername));
        Assert.Equal(["Harassment", "Hate speech"], view.Reasons.Select(r => r.Label));
        Assert.Contains("<script>", view.WrittenReason);
        Assert.True(view.CanEdit);
        Assert.False(view.Withdrawn);

        // The snapshot: the rows as they stood, with how old the profile was.
        var snapshot = view.Snapshot;
        Assert.Equal(Day.AddMinutes(5), snapshot.TakenAt);
        Assert.Equal(Day.AddHours(-2), snapshot.ProfileRefreshedAt);
        Assert.Equal(TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(5)).TotalSeconds, snapshot.ProfileAgeSecondsAtCapture);
        Assert.Null(snapshot.RecapturedAt);
        Assert.False(snapshot.CanCaptureAgain);
        Assert.Contains("Taken 10 Mar 2026 12:05 UTC", snapshot.Explanation);

        var profile = snapshot.Profile!.Value;
        Assert.Equal("GayHater59", profile.GetProperty("displayName").GetString());
        Assert.Equal("join my discord", profile.GetProperty("bio").GetString());
        Assert.Equal("he/him", profile.GetProperty("pronouns").GetString());
        Assert.Equal("system_trust_known", profile.GetProperty("tags")[0].GetString());
        Assert.Equal("hidden", profile.GetProperty("ageVerificationStatus").GetString());
        Assert.False(profile.GetProperty("eighteenPlus").GetProperty("verified").GetBoolean());
        Assert.Equal("join my discord", profile.GetProperty("raw").GetProperty("bio").GetString());

        var membership = snapshot.Membership!.Value;
        Assert.True(membership.GetProperty("isMember").GetBoolean());
        Assert.Equal("grol_member", membership.GetProperty("roleIds")[0].GetString());
        Assert.Equal(Day.AddDays(-20), membership.GetProperty("joinedAt").GetDateTimeOffset());

        var ban = snapshot.BanListEntry!.Value;
        Assert.Equal(Day.AddHours(-1), ban.GetProperty("bannedAt").GetDateTimeOffset());

        // A fresher profile was asked for, at the tier a moderator's own look uses.
        Assert.Equal("Queued", created.RefreshOutcome);
        var pending = host.Queue.PendingFor(Banned);
        Assert.NotNull(pending);
        Assert.Equal(RefreshReason.OpenedInModbot, pending!.Reason);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // The stored row was read, never written: same profile, same refresh time.
        var row = await db.VRChatUsers.AsNoTracking().SingleAsync(u => u.UserId == Banned, ct);
        Assert.Equal(Day.AddHours(-2), row.LastRefreshedAt);
        Assert.Equal("join my discord", row.Bio);

        // The fact: about the banned person, by the Modbot account, carrying the content.
        var fact = await db.Events.AsNoTracking().SingleAsync(e => e.Type == FactType.ReportCreated, ct);
        Assert.Equal(Banned, fact.SubjectId);
        Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal(FactSource.Manual, fact.Source);
        Assert.Equal(view.AuthorUserId.ToString(), fact.ActorId);
        var data = JsonDocument.Parse(fact.Data).RootElement;
        Assert.Equal(view.Id.ToString(), data.GetProperty("caseId").GetString());
        Assert.Equal("gaud_1", data.GetProperty("auditEntryId").GetString());
        Assert.Contains("shouting slurs", data.GetProperty("writtenReason").GetString());
        Assert.Equal(2, data.GetProperty("reasonLabels").GetArrayLength());

        // It is in the person's list, and the lookup finds it.
        var list = await host.GetJsonAsync<CaseFileListResponse>($"/api/cases?userId={Banned}", cookie, ct);
        var summary = Assert.Single(list.Cases);
        Assert.Equal(view.Id, summary.Id);
        Assert.Equal(0, summary.EvidenceCount);

        var lookup = await host.GetJsonAsync<List<CaseFileLookup>>($"/api/cases/lookup?userId={Banned}&userId=usr_other", cookie, ct);
        Assert.Equal(view.Id, lookup.Single(l => l.UserId == Banned).CaseId);
        Assert.Null(lookup.Single(l => l.UserId == "usr_other").CaseId);
    }

    [Fact]
    public async Task OneCaseFilePerBan_ASecondIs409NamingTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SeedBanAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var first = await WriteAsync(host, cookie, ct);

        var reasons = await ReasonsAsync(host, cookie, ct);
        var again = await host.PostJsonAsync("/api/cases", new { userId = Banned, auditEntryId = "gaud_1", reasonIds = new[] { reasons[0].Id }, writtenReason = "again" }, cookie, ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.Equal(first.Id.ToString(), body.GetProperty("caseId").GetString());

        // An audit entry nobody recorded is a 404, not a case file about nothing.
        var unknown = await host.PostJsonAsync("/api/cases", new { userId = Banned, auditEntryId = "gaud_nope", reasonIds = new[] { reasons[0].Id }, writtenReason = "x" }, cookie, ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task OtherNeedsAWrittenReason_AndAtLeastOneReasonIsRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SeedBanAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var reasons = await ReasonsAsync(host, cookie, ct);
        var other = reasons.Single(r => r.Label == "Other");
        var spam = reasons.Single(r => r.Label == "Spam");

        var noReason = await host.PostJsonAsync("/api/cases", new { userId = Banned, reasonIds = Array.Empty<Guid>(), writtenReason = "x" }, cookie, ct);
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var otherBlank = await host.PostJsonAsync("/api/cases", new { userId = Banned, reasonIds = new[] { other.Id }, writtenReason = "  " }, cookie, ct);
        Assert.Equal(HttpStatusCode.BadRequest, otherBlank.StatusCode);
        Assert.Contains("Other", await otherBlank.Content.ReadAsStringAsync(ct));

        var unknown = await host.PostJsonAsync("/api/cases", new { userId = Banned, reasonIds = new[] { Guid.NewGuid() }, writtenReason = "x" }, cookie, ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Spam alone needs no words; the case file still records that none were given.
        var spamOnly = await host.PostJsonAsync("/api/cases", new { userId = Banned, reasonIds = new[] { spam.Id }, writtenReason = (string?)null }, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, spamOnly.StatusCode);
        Assert.Equal(string.Empty, Read<CaseFileCreatedResponse>(await spamOnly.Content.ReadAsStringAsync(ct)).Case.WrittenReason);
    }

    [Fact]
    public async Task APersonWithNoProfileYet_GetsASnapshotThatSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var view = await WriteAsync(host, cookie, ct, userId: "usr_never_seen");

        Assert.Null(view.AuditEntryId);
        Assert.Null(view.BannedAt);
        Assert.Null(view.Snapshot.Profile);
        Assert.Null(view.Snapshot.Membership);
        Assert.Null(view.Snapshot.ProfileRefreshedAt);
        Assert.Contains("No profile had been fetched", view.Snapshot.Explanation);

        // Asking for the refresh created the row, so the sync will get to them.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        Assert.True(await db.VRChatUsers.AnyAsync(u => u.UserId == "usr_never_seen", ct));
        Assert.NotNull(host.Queue.PendingFor("usr_never_seen"));
    }

    // ── Editing, withdrawing, capturing again ──────────────────────────────────────────────

    [Fact]
    public async Task Editing_ChangesTheContent_AndRecordsBeforeAndAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SeedBanAsync(host, ct);

        var author = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var written = await WriteAsync(host, author, ct);
        var reasons = await ReasonsAsync(host, author, ct);
        var evasion = reasons.Single(r => r.Label == "Ban evasion");

        host.Clock.UtcNow = Day.AddHours(1);

        // A different moderator who may ban edits it.
        var colleague = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var edited = await host.PutJsonAsync($"/api/cases/{written.Id}", new { reasonIds = new[] { evasion.Id }, writtenReason = "Same person as usr_old, banned last month." }, colleague, ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var view = Read<CaseFileView>(await edited.Content.ReadAsStringAsync(ct));
        Assert.Equal(["Ban evasion"], view.Reasons.Select(r => r.Label));
        Assert.Equal("Same person as usr_old, banned last month.", view.WrittenReason);
        Assert.Equal(Day.AddHours(1), view.UpdatedAt);
        Assert.NotEqual(view.AuthorUsername, view.UpdatedByUsername);
        Assert.Equal(written.AuthorUsername, view.AuthorUsername);

        // The snapshot is untouched by an edit.
        Assert.Equal(written.Snapshot.TakenAt, view.Snapshot.TakenAt);
        Assert.Equal(written.Snapshot.Profile!.Value.GetRawText(), view.Snapshot.Profile!.Value.GetRawText());

        // Saving the same thing again writes nothing.
        var same = await host.PutJsonAsync($"/api/cases/{written.Id}", new { reasonIds = new[] { evasion.Id }, writtenReason = "Same person as usr_old, banned last month." }, colleague, ct);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var fact = await db.Events.AsNoTracking().SingleAsync(e => e.Type == FactType.ReportUpdated, ct);
        var data = JsonDocument.Parse(fact.Data).RootElement;
        Assert.Contains("shouting slurs", data.GetProperty("before").GetProperty("writtenReason").GetString());
        Assert.Contains("usr_old", data.GetProperty("after").GetProperty("writtenReason").GetString());
        Assert.Equal(written.Id.ToString(), data.GetProperty("caseId").GetString());
    }

    [Fact]
    public async Task Withdrawing_NeedsANote_KeepsTheRow_AndMakesTheBanUnwrittenAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SeedBanAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var written = await WriteAsync(host, cookie, ct);

        Assert.Empty((await host.GetJsonAsync<UnwrittenBanListResponse>("/api/cases/missing", cookie, ct)).Bans);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostJsonAsync($"/api/cases/{written.Id}/withdraw", new { note = " " }, cookie, ct)).StatusCode);

        host.Clock.UtcNow = Day.AddHours(2);
        var withdrawn = await host.PostJsonAsync($"/api/cases/{written.Id}/withdraw", new { note = "Wrong person; the name matched somebody else." }, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);

        var view = Read<CaseFileView>(await withdrawn.Content.ReadAsStringAsync(ct));
        Assert.True(view.Withdrawn);
        Assert.Equal(Day.AddHours(2), view.WithdrawnAt);
        Assert.Equal("Wrong person; the name matched somebody else.", view.WithdrawnNote);
        Assert.False(view.CanEdit);

        // Still readable, out of the default list, in the full one; the ban is unwritten again.
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/cases/{written.Id}", cookie, ct)).StatusCode);
        Assert.Empty((await host.GetJsonAsync<CaseFileListResponse>("/api/cases", cookie, ct)).Cases);
        Assert.Single((await host.GetJsonAsync<CaseFileListResponse>("/api/cases?includeWithdrawn=true", cookie, ct)).Cases);
        Assert.Single((await host.GetJsonAsync<UnwrittenBanListResponse>("/api/cases/missing", cookie, ct)).Bans);
        Assert.Null((await host.GetJsonAsync<List<CaseFileLookup>>($"/api/cases/lookup?userId={Banned}", cookie, ct)).Single().CaseId);

        // No more changes to a withdrawn one; a new one can be written for the same ban.
        var reasons = await ReasonsAsync(host, cookie, ct);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PutJsonAsync($"/api/cases/{written.Id}", new { reasonIds = new[] { reasons[0].Id }, writtenReason = "x" }, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostJsonAsync($"/api/cases/{written.Id}/withdraw", new { note = "again" }, cookie, ct)).StatusCode);
        var replacement = await WriteAsync(host, cookie, ct);
        Assert.NotEqual(written.Id, replacement.Id);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        Assert.Equal(2, await db.CaseFiles.CountAsync(ct));
        var fact = await db.Events.AsNoTracking().SingleAsync(e => e.Type == FactType.ReportWithdrawn, ct);
        Assert.Contains("Wrong person", fact.Data);
    }

    [Fact]
    public async Task CapturingAgain_WaitsForANewerProfile_HappensOnce_AndKeepsTheFirstInTheFact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SeedBanAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var written = await WriteAsync(host, cookie, ct);

        // Nothing newer yet: refused, and the view says so.
        Assert.False(written.Snapshot.CanCaptureAgain);
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostJsonAsync($"/api/cases/{written.Id}/capture-again", null, cookie, ct)).StatusCode);

        // The profile sync answers: the person has already scrubbed their bio.
        host.Clock.UtcNow = Day.AddMinutes(10);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var row = await db.VRChatUsers.SingleAsync(u => u.UserId == Banned, ct);
            row.Bio = "";
            row.DisplayName = "totally_new_name";
            row.LastRefreshedAt = Day.AddMinutes(10);
            await db.SaveChangesAsync(ct);
        }

        var before = await host.GetJsonAsync<CaseFileView>($"/api/cases/{written.Id}", cookie, ct);
        Assert.True(before.Snapshot.CanCaptureAgain);
        Assert.Contains("newer profile", before.Snapshot.Explanation);
        // Still the first snapshot until somebody presses the button.
        Assert.Equal("GayHater59", before.Snapshot.Profile!.Value.GetProperty("displayName").GetString());

        host.Clock.UtcNow = Day.AddMinutes(11);
        var captured = await host.PostJsonAsync($"/api/cases/{written.Id}/capture-again", null, cookie, ct);
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);

        var after = Read<CaseFileView>(await captured.Content.ReadAsStringAsync(ct));
        Assert.Equal("totally_new_name", after.Snapshot.Profile!.Value.GetProperty("displayName").GetString());
        Assert.Equal(Day.AddMinutes(11), after.Snapshot.TakenAt);
        Assert.Equal(Day.AddMinutes(11), after.Snapshot.RecapturedAt);
        Assert.Equal(Day.AddMinutes(10), after.Snapshot.ProfileRefreshedAt);
        Assert.False(after.Snapshot.CanCaptureAgain);
        Assert.Contains("captured again", after.Snapshot.Explanation);

        // Once only.
        Assert.Equal(HttpStatusCode.Conflict, (await host.PostJsonAsync($"/api/cases/{written.Id}/capture-again", null, cookie, ct)).StatusCode);

        using var check = host.Services.CreateScope();
        var events = check.ServiceProvider.GetRequiredService<ModbotContext>().Events;
        var fact = await events.AsNoTracking().SingleAsync(e => e.Type == FactType.ReportSnapshotRecaptured, ct);
        var previous = JsonDocument.Parse(fact.Data).RootElement.GetProperty("previous");
        Assert.Equal("GayHater59", previous.GetProperty("profile").GetProperty("displayName").GetString());
        Assert.Equal("join my discord", previous.GetProperty("profile").GetProperty("bio").GetString());
    }

    // ── The unwritten list ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Missing_ListsBansInTheWindowWithNoCaseFile_NewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SeedBanAsync(host, ct);

        // Two more bans: one recent and later lifted, one too old for the window.
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_second", Day.AddDays(-2), actor: Moderator, actorName: "Gunner24", extra: new JsonObject { ["auditEntryId"] = "gaud_2" }), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberUnbanned, "usr_second", Day.AddDays(-1), actor: Moderator, actorName: "Gunner24"), ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_ancient", Day.AddDays(-400), actor: Moderator, extra: new JsonObject { ["auditEntryId"] = "gaud_0" }), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);

        var all = await host.GetJsonAsync<UnwrittenBanListResponse>("/api/cases/missing", cookie, ct);
        Assert.Equal(30, all.Days);
        Assert.Equal(2, all.Total);
        Assert.Equal([Banned, "usr_second"], all.Bans.Select(b => b.UserId));
        Assert.Equal("gaud_1", all.Bans[0].AuditEntryId);
        Assert.Equal("Gunner24", all.Bans[0].BannedBy?.Name);
        Assert.Null(all.Bans[0].LiftedAt);
        Assert.Equal(Day.AddDays(-1), all.Bans[1].LiftedAt);

        // A wider window reaches the old one.
        Assert.Equal(3, (await host.GetJsonAsync<UnwrittenBanListResponse>("/api/cases/missing?days=1000", cookie, ct)).Total);

        // Writing one up removes it.
        await WriteAsync(host, cookie, ct);
        var remaining = await host.GetJsonAsync<UnwrittenBanListResponse>("/api/cases/missing", cookie, ct);
        Assert.Equal("usr_second", Assert.Single(remaining.Bans).UserId);
    }

    // ── Evidence ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EvidenceUploadedWithTheCaseFileId_ShowsOnTheCaseFile_ToThoseWhoMayViewIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);
        await SeedBanAsync(host, ct);

        var admin = await host.SignedInAsync(ModbotPermissions.Administrator, ct);
        var configured = await host.PutJsonAsync("/api/settings/evidence/backend", new { backend = "Filesystem", root = host.EvidenceRoot }, admin, ct);
        var setup = Read<EvidenceSetupResponse>(await configured.Content.ReadAsStringAsync(ct));
        Assert.True(setup.Succeeded, setup.Message);

        var moderator = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile | ModbotPermissions.UploadEvidence | ModbotPermissions.ViewEvidence, ct);
        var written = await WriteAsync(host, moderator, ct);
        Assert.True(written.CanAttach);
        Assert.True(written.EvidenceDelivery.Configured);
        Assert.True(written.EvidenceDelivery.UploadsAllowed);
        Assert.False(written.EvidenceDelivery.DirectDelivery);
        Assert.Contains("image/png", written.EvidenceDelivery.AcceptedTypes);

        // The three-phase upload, citing the case file.
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(Encoding.UTF8.GetBytes($"case-file-{Guid.NewGuid():N}"))
            .ToArray();

        var begun = await host.PostJsonAsync("/api/evidence/uploads", new { fileName = "slurs.png", contentType = "image/png", length = png.Length, reportId = written.Id.ToString() }, moderator, ct);
        Assert.Equal(HttpStatusCode.OK, begun.StatusCode);
        var ticket = Read<EvidenceUploadTicketView>(await begun.Content.ReadAsStringAsync(ct));

        (await host.PutBytesAsync(ticket.TransferUrl, png, moderator, ct)).EnsureSuccessStatusCode();
        var committed = await host.PostJsonAsync($"/api/evidence/uploads/{ticket.UploadId}/commit", new { }, moderator, ct);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var stored = Read<EvidenceCommitResponse>(await committed.Content.ReadAsStringAsync(ct));

        var view = await host.GetJsonAsync<CaseFileView>($"/api/cases/{written.Id}", moderator, ct);
        Assert.True(view.CanViewEvidence);
        var item = Assert.Single(view.Evidence!);
        Assert.Equal(stored.Hash, item.Hash);
        Assert.Equal("image/png", item.ContentType);
        Assert.Equal("slurs.png", item.FileName);
        Assert.Equal(written.Id.ToString(), item.ReportId);

        var summary = Assert.Single((await host.GetJsonAsync<CaseFileListResponse>($"/api/cases?userId={Banned}", moderator, ct)).Cases);
        Assert.Equal(1, summary.EvidenceCount);

        // Without ViewEvidence the case file reads, and the evidence list is withheld.
        var reader = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var withheld = await host.GetJsonAsync<CaseFileView>($"/api/cases/{written.Id}", reader, ct);
        Assert.Null(withheld.Evidence);
        Assert.False(withheld.CanViewEvidence);
    }

    [Fact]
    public async Task AnUnknownCaseFile_Is404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.ViewProfile, ct);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync($"/api/cases/{id}", cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PutJsonAsync($"/api/cases/{id}", new { reasonIds = new[] { Guid.NewGuid() }, writtenReason = "x" }, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostJsonAsync($"/api/cases/{id}/withdraw", new { note = "x" }, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostJsonAsync($"/api/cases/{id}/capture-again", null, cookie, ct)).StatusCode);
    }
}
