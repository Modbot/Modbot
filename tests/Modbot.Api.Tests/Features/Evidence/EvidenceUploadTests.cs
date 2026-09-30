using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// Evidence design §9: begin, transfer, commit — and what happens to a file that lies, that has
/// nowhere to go, or that would take a case file over its limit.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EvidenceUploadTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Uploader = ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence;

    /// <summary>
    /// A file goes in, and the metadata row describes it exactly once it is committed.
    /// </summary>
    [Fact]
    public async Task AFileIsHashed_Typed_AndAttachedOnlyAtCommit()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var bytes = EvidenceUploads.Png("upload-happy-path");

        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName = "proof.png", contentType = "image/png", length = bytes.Length, reportId = caseId },
                Ct),
            Ct);

        Assert.False(ticket.Presigned);
        Assert.Contains("image/png", ticket.AcceptedTypes);

        await using (var context = db.NewContext())
        {
            // Phase 1 attaches nothing. A moderator who closes the tab here leaves no trace on a
            // case file at all.
            Assert.Equal(0, await context.EvidenceAttachments.CountAsync(a => a.CaseId == caseId, Ct));
        }

        var staged = await host.ReadAsync<EvidenceStagedView>(
            await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, Ct), Ct);

        Assert.Equal(bytes.Length, staged.ByteSize);

        await using (var context = db.NewContext())
        {
            // Still nothing. Bytes are in the store under a staging key, attached to nothing.
            Assert.Equal(0, await context.EvidenceAttachments.CountAsync(a => a.CaseId == caseId, Ct));
        }

        var committed = await host.ReadAsync<EvidenceCommitResponse>(
            await host.PostAsync($"/api/evidence/uploads/{ticket.UploadId}/commit", cookie, new { }, Ct),
            Ct);

        Assert.Equal(staged.Hash, committed.Hash);

        // Modbot's determination from the leading bytes, not the client's claim and not the name.
        Assert.Equal("image/png", committed.ContentType);

        await using (var context = db.NewContext())
        {
            var row = await context.EvidenceBlobs.AsNoTracking()
                .SingleAsync(b => b.Hash == committed.Hash, Ct);

            Assert.Equal("proof.png", row.FileName);
            Assert.Equal(bytes.Length, row.ByteSize);
            Assert.Equal((short)Modbot.Evidence.Options.EvidenceBackend.Filesystem, row.Backend);

            var held = await context.EvidenceAttachments.AsNoTracking()
                .SingleAsync(a => a.CaseId == caseId, Ct);

            Assert.Equal(committed.Hash, held.Hash);
            Assert.Equal("proof.png", held.FileName);
            Assert.Equal(host.WhoIs(cookie).Id, held.AttachedByUserId);
            Assert.Equal(host.WhoIs(cookie).Username, held.AttachedByName);
            Assert.True(held.IsOn);
        }
    }

    /// <summary>
    /// An SVG named <c>.png</c> and declared as a PNG is still refused, and nothing is attached.
    /// </summary>
    /// <remarks>
    /// SVG is the single most common way an "image upload" becomes an XSS, and a moderation tool
    /// is a near-ideal target for one: the attacker is already authenticated and the audience is
    /// the people with the most permissions. The type is decided from the file's own bytes because
    /// both of the other two sources are attacker-chosen.
    /// </remarks>
    [Fact]
    public async Task AnSvgWearingAPngNameIsRefusedAtCommit()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var bytes = EvidenceUploads.Svg();

        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName = "harmless.png", contentType = "image/png", length = bytes.Length, reportId = caseId },
                Ct),
            Ct);

        (await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, Ct)).EnsureSuccessStatusCode();

        var committed = await host.PostAsync(
            $"/api/evidence/uploads/{ticket.UploadId}/commit", cookie, new { }, Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, committed.StatusCode);

        await using var context = db.NewContext();
        Assert.Equal(0, await context.EvidenceAttachments.CountAsync(a => a.CaseId == caseId, Ct));
    }

    /// <summary>A declared type that is not on the allowlist is refused before a byte moves.</summary>
    [Fact]
    public async Task ADeclaredTypeOffTheAllowlistIsRefusedAtPhaseOne()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var response = await host.PostAsync(
            "/api/evidence/uploads",
            cookie,
            new { fileName = "drawing.svg", contentType = "image/svg+xml" },
            Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    /// <summary>A file over the cap is refused, and the answer says so rather than 500ing.</summary>
    [Fact]
    public async Task AFileOverTheCapIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        (await host.PutAsync(
            "/api/settings/evidence/limits",
            cookie,
            new
            {
                maxFileBytes = 64,
                maxReportBytes = 0,
                maxDeploymentBytes = 0,
                directDeliveryEnabled = true,
            },
            Ct)).EnsureSuccessStatusCode();

        var bytes = EvidenceUploads.Png(new string('x', 200));

        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads", cookie, new { fileName = "big.png" }, Ct),
            Ct);

        Assert.Equal(64, ticket.MaxBytes);

        var transferred = await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, transferred.StatusCode);
    }

    /// <summary>
    /// Attaching somebody else's image to this deployment's disk is granted, never implied.
    /// </summary>
    [Fact]
    public async Task WithoutUploadEvidence_Is403()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var admin = await host.SignedInAsync(Uploader, Ct);

        await EvidenceUploads.ConfigureAsync(host, admin, Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewEvidence, Ct);

        var response = await host.PostAsync(
            "/api/evidence/uploads", cookie, new { fileName = "proof.png" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Where a file may go ────────────────────────────────────────────────────────────────

    /// <summary>Starts an upload, sends the bytes, and returns the commit's answer without asserting on it.</summary>
    private static async Task<HttpResponseMessage> CommitAsync(
        EvidenceApiTestHost host, string cookie, byte[] bytes, string? beganFor, string? commitTo, CancellationToken ct)
    {
        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName = "proof.png", contentType = "image/png", length = bytes.Length, reportId = beganFor },
                ct),
            ct);

        (await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, ct)).EnsureSuccessStatusCode();

        return await host.PostAsync(
            $"/api/evidence/uploads/{ticket.UploadId}/commit", cookie, new { reportId = commitTo }, ct);
    }

    /// <summary>A file with no case file to go on is refused, not stored and forgotten.</summary>
    [Fact]
    public async Task ACommitNamingNoCaseFileIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var response = await CommitAsync(host, cookie, EvidenceUploads.Png("commit-no-case"), null, null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var context = db.NewContext();
        Assert.Empty(await context.EvidenceAttachments.ToListAsync(Ct));
        Assert.Empty(await context.EvidenceBlobs.ToListAsync(Ct));
    }

    /// <summary>A case file that does not exist is not somewhere a file can go.</summary>
    [Fact]
    public async Task ACommitToACaseFileThatDoesNotExistIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var missing = Guid.NewGuid().ToString();

        var response = await CommitAsync(host, cookie, EvidenceUploads.Png("commit-missing-case"), null, missing, Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var notAnId = await CommitAsync(host, cookie, EvidenceUploads.Png("commit-not-an-id"), null, "report-1", Ct);
        Assert.Equal(HttpStatusCode.NotFound, notAnId.StatusCode);

        await using var context = db.NewContext();
        Assert.Empty(await context.EvidenceAttachments.ToListAsync(Ct));
    }

    /// <summary>A withdrawn case file cannot be changed, and that includes what is on it.</summary>
    [Fact]
    public async Task ACommitToAWithdrawnCaseFileIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct, withdrawn: true);

        var response = await CommitAsync(host, cookie, EvidenceUploads.Png("commit-withdrawn"), null, caseId, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// Only the author, or somebody who may ban, can put a file on a case file: the same rule that
    /// decides who may edit it.
    /// </summary>
    [Fact]
    public async Task ACommitToSomebodyElsesCaseFileNeedsTheBanPermission()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var author = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, author, Ct);

        var caseId = await host.NewCaseAsync(author, Ct);

        var other = await host.SignedInAsync(ModbotPermissions.UploadEvidence, Ct);
        var refused = await CommitAsync(host, other, EvidenceUploads.Png("commit-not-author"), null, caseId, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var moderator = await host.SignedInAsync(ModbotPermissions.UploadEvidence | ModbotPermissions.Ban, Ct);
        var accepted = await CommitAsync(host, moderator, EvidenceUploads.Png("commit-may-ban"), null, caseId, Ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    /// <summary>
    /// Beginning an upload for somebody else's case file is refused before anything is measured, so
    /// the size refusals cannot be used to read what that case file holds.
    /// </summary>
    [Fact]
    public async Task BeginningAnUploadForSomebodyElsesCaseFileIsRefusedBeforeItsSizeIsChecked()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var author = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, author, Ct);
        await SetLimitsAsync(host, author, file: 1000, report: 1500, deployment: 0);

        var caseId = await host.NewCaseAsync(author, Ct);
        await EvidenceUploads.UploadAsync(
            host, author, EvidenceUploads.Png(new string('a', 800)), "one.png", caseId, Ct);

        var stranger = await host.SignedInAsync(ModbotPermissions.UploadEvidence, Ct);

        // The size alone would have been refused with "already holds"; the answer is the 403 instead.
        var refused = await host.PostAsync(
            "/api/evidence/uploads",
            stranger,
            new { fileName = "two.png", contentType = "image/png", length = 808, reportId = caseId },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain("holds", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var missing = await host.PostAsync(
            "/api/evidence/uploads",
            stranger,
            new { fileName = "two.png", contentType = "image/png", length = 808, reportId = Guid.NewGuid().ToString() },
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// The same bytes on a second case file: the file is stored once, and both case files hold it.
    /// It used to appear on the first only, and "Attached." was shown on the second all the same.
    /// </summary>
    [Fact]
    public async Task TheSameFileCanBeOnTwoCaseFiles()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader | ModbotPermissions.ViewEvidence, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var first = await host.NewCaseAsync(cookie, Ct);
        var second = await host.NewCaseAsync(cookie, Ct);

        var bytes = EvidenceUploads.Png("two-case-files");
        var one = await EvidenceUploads.UploadAsync(host, cookie, bytes, "one.png", first, Ct);
        var two = await EvidenceUploads.UploadAsync(host, cookie, bytes, "two.png", second, Ct);

        Assert.Equal(one.Hash, two.Hash);

        await using var context = db.NewContext();
        Assert.Equal(1, await context.EvidenceBlobs.CountAsync(b => b.Hash == one.Hash, Ct));

        // Each case file holds it, under the name it was put on with.
        var held = await context.EvidenceAttachments.AsNoTracking()
            .Where(a => a.Hash == one.Hash).OrderBy(a => a.AttachedAt).ThenBy(a => a.Id).ToListAsync(Ct);

        Assert.Equal(
            new[] { first, second }.Order().ToList(),
            held.Select(a => a.CaseId).Order().ToList());
        Assert.Contains(held, a => a.CaseId == first && a.FileName == "one.png");
        Assert.Contains(held, a => a.CaseId == second && a.FileName == "two.png");

        // And the list of each shows it.
        foreach (var id in new[] { first, second })
        {
            var listed = await host.ReadAsync<List<EvidenceObjectView>>(
                await host.GetAsync($"/api/evidence?reportId={id}", cookie, Ct), Ct);

            Assert.Equal(one.Hash, Assert.Single(listed).Hash);
        }
    }

    /// <summary>
    /// Committing again after a commit that already worked adds no second hold and no second line in
    /// the log.
    /// </summary>
    [Fact]
    public async Task ARetriedCommitAttachesAndRecordsNothingTwice()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var bytes = EvidenceUploads.Png("commit-retried");

        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName = "proof.png", contentType = "image/png", length = bytes.Length, reportId = caseId },
                Ct),
            Ct);

        (await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, Ct)).EnsureSuccessStatusCode();

        var path = $"/api/evidence/uploads/{ticket.UploadId}/commit";
        (await host.PostAsync(path, cookie, new { }, Ct)).EnsureSuccessStatusCode();
        (await host.PostAsync(path, cookie, new { }, Ct)).EnsureSuccessStatusCode();

        await using var context = db.NewContext();
        Assert.Equal(1, await context.EvidenceAttachments.CountAsync(a => a.CaseId == caseId, Ct));
        Assert.Equal(1, await context.Events.CountAsync(e => e.Type == FactType.EvidenceAttached && e.SubjectId == caseId, Ct));
    }

    /// <summary>
    /// Putting a file on a case file is a fact against the account that did it: who, which case
    /// file, which file and the name it was put on under.
    /// </summary>
    [Fact]
    public async Task AttachingWritesAFactNamingTheCaseFileTheFileAndWho()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("fact-attach"), "proof.png", caseId, Ct);

        await using var context = db.NewContext();
        var fact = await context.Events.AsNoTracking()
            .SingleAsync(e => e.Type == FactType.EvidenceAttached && e.SubjectId == caseId, Ct);

        Assert.Equal(FactPlatform.Modbot, fact.SubjectPlatform);
        Assert.Equal(host.WhoIs(cookie).Id.ToString(), fact.ActorId);

        using var data = JsonDocument.Parse(fact.Data);
        Assert.Equal(caseId, data.RootElement.GetProperty("caseId").GetString());
        Assert.Equal(stored.Hash, data.RootElement.GetProperty("hash").GetString());
        Assert.Equal("proof.png", data.RootElement.GetProperty("fileName").GetString());
    }

    // ── The limits ─────────────────────────────────────────────────────────────────────────

    private static async Task SetLimitsAsync(
        EvidenceApiTestHost host, string cookie, long file, long report, long deployment)
        => (await host.PutAsync(
            "/api/settings/evidence/limits",
            cookie,
            new { maxFileBytes = file, maxReportBytes = report, maxDeploymentBytes = deployment, directDeliveryEnabled = true },
            Ct)).EnsureSuccessStatusCode();

    /// <summary>
    /// The per-case-file total is checked, and now that it is saved it is enforced: the second file
    /// that would take one case file over is refused with the sentence that says so, and another
    /// case file still has room.
    /// </summary>
    [Fact]
    public async Task ASecondFileThatWouldTakeACaseFileOverItsTotalIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);
        await SetLimitsAsync(host, cookie, file: 1000, report: 1500, deployment: 0);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png(new string('a', 800)), "one.png", caseId, Ct);

        var refused = await host.PostAsync(
            "/api/evidence/uploads",
            cookie,
            new { fileName = "two.png", contentType = "image/png", length = 808, reportId = caseId },
            Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Contains("This case file can hold", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var other = await host.NewCaseAsync(cookie, Ct);
        await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png(new string('b', 800)), "two.png", other, Ct);
    }

    /// <summary>
    /// A size the client lied about at the beginning is caught at commit, on the stored bytes, and
    /// nothing is attached.
    /// </summary>
    [Fact]
    public async Task ACaseFileTotalIsCheckedAgainAtCommit()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);
        await SetLimitsAsync(host, cookie, file: 1000, report: 1500, deployment: 0);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png(new string('a', 800)), "one.png", caseId, Ct);

        var bytes = EvidenceUploads.Png(new string('c', 800));

        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName = "two.png", contentType = "image/png", length = 10, reportId = caseId },
                Ct),
            Ct);

        (await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, Ct)).EnsureSuccessStatusCode();

        var committed = await host.PostAsync(
            $"/api/evidence/uploads/{ticket.UploadId}/commit", cookie, new { }, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, committed.StatusCode);

        await using var context = db.NewContext();
        Assert.Equal(1, await context.EvidenceAttachments.CountAsync(a => a.CaseId == caseId, Ct));
    }

    /// <summary>The whole-install total is checked across every case file.</summary>
    [Fact]
    public async Task AFileThatWouldTakeTheInstallOverItsTotalIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Uploader, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);
        await SetLimitsAsync(host, cookie, file: 1000, report: 0, deployment: 1500);

        await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png(new string('a', 800)), "one.png", await host.NewCaseAsync(cookie, Ct), Ct);

        var refused = await host.PostAsync(
            "/api/evidence/uploads",
            cookie,
            new { fileName = "two.png", contentType = "image/png", length = 808, reportId = await host.NewCaseAsync(cookie, Ct) },
            Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Contains("Evidence storage is limited to", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}
