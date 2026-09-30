using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// Evidence design §10 and §6: how bytes leave Modbot, and what it takes to delete them.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EvidenceServingTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Moderator =
        ModbotPermissions.ManageSettings
        | ModbotPermissions.UploadEvidence
        | ModbotPermissions.ViewEvidence;

    private async Task<List<ModbotEvent>> FactsAsync(string type, string subject)
    {
        await using var context = db.NewContext();

        return await context.Events.AsNoTracking()
            .Where(e => e.Type == type && e.SubjectId == subject)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    private static JsonElement Data(ModbotEvent fact) => JsonDocument.Parse(fact.Data).RootElement;

    /// <summary>
    /// Every byte Modbot serves goes out as an attachment, typed by Modbot, with nosniff and a
    /// sandbox CSP.
    /// </summary>
    /// <remarks>
    /// A moderation tool where staff routinely open files uploaded by other staff is a near-ideal
    /// stored-XSS target. These four headers are the answer for the proxied path; on a presigned
    /// one only two of them can be signed, which is why the format allowlist is closed rather than
    /// advisory.
    /// </remarks>
    [Fact]
    public async Task BytesAreServedAsAnAttachment_TypedByModbot_AndNotSniffable()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var bytes = EvidenceUploads.Png("serving-headers");
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, bytes, "proof.png", await host.NewCaseAsync(cookie, Ct), Ct);

        var response = await host.GetAsync($"/api/evidence/{stored.Hash}", cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));

        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>
    /// A filename a person typed cannot break out of the header it is put in.
    /// </summary>
    /// <remarks>
    /// The filename is hostile input and always has been. It is never part of a key — keys are the
    /// hash and nothing else — so the only place it can do harm is here.
    /// </remarks>
    [Fact]
    public async Task AHostileFilenameCannotBreakTheDispositionHeader()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var stored = await EvidenceUploads.UploadAsync(
            host,
            cookie,
            EvidenceUploads.Png("serving-filename"),
            "ev\"il\\..\\..\\etc\\passwd.png",
            await host.NewCaseAsync(cookie, Ct),
            Ct);

        var response = await host.GetAsync($"/api/evidence/{stored.Hash}", cookie, Ct);

        response.EnsureSuccessStatusCode();

        var name = response.Content.Headers.ContentDisposition?.FileName;

        Assert.NotNull(name);
        Assert.DoesNotContain('"', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain('/', name);
    }

    /// <summary>
    /// Never existed, destroyed, and unavailable are three different answers.
    /// </summary>
    /// <remarks>
    /// Collapsing them into a 404 would make a store that lost its objects look like a case file
    /// that never had evidence — the exact confusion design §8's detection exists to prevent.
    /// </remarks>
    [Fact]
    public async Task NeverExistedAndDestroyedAreDifferentAnswers()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator | ModbotPermissions.DestroyEvidence, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var never = await host.GetAsync($"/api/evidence/{new string('7', 64)}", cookie, Ct);
        Assert.Equal(HttpStatusCode.NotFound, never.StatusCode);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-destroyed"), "gone.png", caseId, Ct);

        var destroyed = await host.ReadAsync<EvidenceDestroyResponse>(
            await host.PostAsync(
                $"/api/evidence/{stored.Hash}/destroy",
                cookie,
                new { reason = "subject erasure request", caseId },
                Ct),
            Ct);

        Assert.True(destroyed.Destroyed, destroyed.Message);

        var gone = await host.GetAsync($"/api/evidence/{stored.Hash}", cookie, Ct);
        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);

        // Everything except the bytes survives, so "this case had a video and somebody deleted it
        // on this date" stays answerable.
        var metadata = await host.ReadAsync<EvidenceObjectView>(
            await host.GetAsync($"/api/evidence/{stored.Hash}/metadata", cookie, Ct), Ct);

        Assert.True(metadata.Destroyed);
        Assert.Equal("subject erasure request", metadata.DestroyedReason);
        Assert.Equal("image/png", metadata.ContentType);
    }

    /// <summary>
    /// Bytes a case file still holds are not destroyed, and the case files are named back.
    /// </summary>
    /// <remarks>
    /// Content addressing means two case files can share one object, so destroying on the strength
    /// of one case file would take evidence off another nobody was looking at. That failure is
    /// quiet and unrecoverable, which is why it is refused rather than warned about.
    /// </remarks>
    [Fact]
    public async Task BytesACaseFileStillHoldsAreNotDestroyed()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator | ModbotPermissions.DestroyEvidence, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-refcount"), "shared.png", caseId, Ct);

        // From nowhere in particular: no case file is letting go of it.
        var result = await host.ReadAsync<EvidenceDestroyResponse>(
            await host.PostAsync(
                $"/api/evidence/{stored.Hash}/destroy", cookie, new { reason = "tidying up" }, Ct),
            Ct);

        Assert.False(result.Destroyed);
        Assert.Equal([caseId], result.BlockedByReports);
        Assert.Contains("case file", result.Message, StringComparison.Ordinal);

        // And the bytes are still there, which is the thing that actually matters.
        var served = await host.GetAsync($"/api/evidence/{stored.Hash}", cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    /// <summary>
    /// Destroying from one case file when another still holds the file is refused, names the other,
    /// and leaves the first holding it too: nothing changes on a refusal.
    /// </summary>
    [Fact]
    public async Task DestroyingFromOneCaseFileIsRefusedWhileAnotherHoldsItAndChangesNothing()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator | ModbotPermissions.DestroyEvidence, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var first = await host.NewCaseAsync(cookie, Ct);
        var second = await host.NewCaseAsync(cookie, Ct);
        var bytes = EvidenceUploads.Png("serving-two-cases");

        var stored = await EvidenceUploads.UploadAsync(host, cookie, bytes, "a.png", first, Ct);
        await EvidenceUploads.UploadAsync(host, cookie, bytes, "b.png", second, Ct);

        var result = await host.ReadAsync<EvidenceDestroyResponse>(
            await host.PostAsync(
                $"/api/evidence/{stored.Hash}/destroy", cookie, new { reason = "tidying up", caseId = first }, Ct),
            Ct);

        Assert.False(result.Destroyed);
        Assert.Equal([second], result.BlockedByReports);

        await using var context = db.NewContext();
        Assert.True((await context.EvidenceAttachments.SingleAsync(a => a.CaseId == first, Ct)).IsOn);
        Assert.True((await context.EvidenceAttachments.SingleAsync(a => a.CaseId == second, Ct)).IsOn);
        Assert.False((await context.EvidenceBlobs.SingleAsync(b => b.Hash == stored.Hash, Ct)).IsDestroyed);
        Assert.Empty(await FactsAsync(FactType.EvidenceDestroyed, first));
    }

    /// <summary>
    /// The case file the destroy is done from lets go of the file as part of it: it is taken off,
    /// then destroyed, and both are written down. A withdrawn case file can still destroy.
    /// </summary>
    [Fact]
    public async Task DestroyingFromTheOnlyCaseFileTakesItOffAndDestroysAndWritesBoth()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator | ModbotPermissions.DestroyEvidence, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-destroy-from"), "gone.png", caseId, Ct);

        // Withdrawn after the file went on: nothing on it can be changed now, except destroying.
        await using (var context = db.NewContext())
        {
            var row = await context.CaseFiles.SingleAsync(c => c.Id == Guid.Parse(caseId), Ct);
            row.WithdrawnAt = host.Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        var result = await host.ReadAsync<EvidenceDestroyResponse>(
            await host.PostAsync(
                $"/api/evidence/{stored.Hash}/destroy",
                cookie,
                new { reason = "the subject asked", caseId },
                Ct),
            Ct);

        Assert.True(result.Destroyed, result.Message);

        await using var check = db.NewContext();
        var held = await check.EvidenceAttachments.AsNoTracking().SingleAsync(a => a.CaseId == caseId, Ct);
        Assert.False(held.IsOn);
        Assert.Equal(host.WhoIs(cookie).Username, held.TakenOffByName);
        Assert.True((await check.EvidenceBlobs.AsNoTracking().SingleAsync(b => b.Hash == stored.Hash, Ct)).IsDestroyed);

        var detached = Assert.Single(await FactsAsync(FactType.EvidenceDetached, caseId));
        Assert.Equal(stored.Hash, Data(detached).GetProperty("hash").GetString());

        var destroyed = Assert.Single(await FactsAsync(FactType.EvidenceDestroyed, caseId));
        Assert.Equal(stored.Hash, Data(destroyed).GetProperty("hash").GetString());
        Assert.Equal("gone.png", Data(destroyed).GetProperty("fileName").GetString());
        Assert.Equal("the subject asked", Data(destroyed).GetProperty("reason").GetString());
        Assert.Equal(host.WhoIs(cookie).Id.ToString(), destroyed.ActorId);
    }

    /// <summary>
    /// A file taken off every case file can be destroyed from the trace it left on the last one,
    /// which has nothing left to take off.
    /// </summary>
    [Fact]
    public async Task ATakenOffFileCanBeDestroyedFromItsTrace()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator | ModbotPermissions.DestroyEvidence, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-trace-destroy"), "old.png", caseId, Ct);

        (await host.PostAsync($"/api/cases/{caseId}/evidence/{stored.Hash}/take-off", cookie, new { }, Ct))
            .EnsureSuccessStatusCode();

        var result = await host.ReadAsync<EvidenceDestroyResponse>(
            await host.PostAsync(
                $"/api/evidence/{stored.Hash}/destroy", cookie, new { reason = "cleanup", caseId }, Ct),
            Ct);

        Assert.True(result.Destroyed, result.Message);

        // The take-off was already done, so the destroy adds no second one.
        Assert.Single(await FactsAsync(FactType.EvidenceDetached, caseId));
        Assert.Single(await FactsAsync(FactType.EvidenceDestroyed, caseId));
    }

    /// <summary>Destroying without a reason is refused: the reason outlives the bytes.</summary>
    [Fact]
    public async Task DestroyingWithoutAReasonIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator | ModbotPermissions.DestroyEvidence, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-noreason"), "x.png", await host.NewCaseAsync(cookie, Ct), Ct);

        var response = await host.PostAsync(
            $"/api/evidence/{stored.Hash}/destroy", cookie, new { reason = "   " }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Reading a case file does not carry the right to watch the video attached to it.
    /// </summary>
    /// <remarks>
    /// Evidence about a person — possibly video of them, in their own words and their own voice —
    /// is categorically more sensitive than the line of audit log it hangs off, and design §14 puts
    /// it behind its own flag for exactly that reason.
    /// </remarks>
    [Fact]
    public async Task WithoutViewEvidence_Is403()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var admin = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, admin, Ct);

        var caseId = await host.NewCaseAsync(admin, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, admin, EvidenceUploads.Png("serving-403"), "private.png", caseId, Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.GetAsync($"/api/evidence/{stored.Hash}", cookie, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.GetAsync($"/api/evidence?reportId={caseId}", cookie, Ct)).StatusCode);
    }

    /// <summary>Destroying is the narrowest permission, and viewing does not imply it.</summary>
    [Fact]
    public async Task WithoutDestroyEvidence_Is403()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-destroy-403"), "x.png", caseId, Ct);

        var response = await host.PostAsync(
            $"/api/evidence/{stored.Hash}/destroy", cookie, new { reason = "no", caseId }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A range request is answered from the middle of the object, which is what video seeking is.
    /// </summary>
    [Fact]
    public async Task ARangeRequestIsAnsweredFromTheMiddle()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var bytes = EvidenceUploads.Png("serving-range");
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, bytes, "clip.png", await host.NewCaseAsync(cookie, Ct), Ct);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/evidence/{stored.Hash}");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Range", "bytes=8-15");

        var response = await host.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(bytes[8..16], await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(
            $"bytes 8-15/{bytes.Length}",
            Assert.Single(response.Content.Headers.GetValues("Content-Range")));
    }

    /// <summary>A range that starts past the end of the object cannot be satisfied.</summary>
    [Fact]
    public async Task ARangeBeyondTheEndIs416()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-range-past"), "clip.png", await host.NewCaseAsync(cookie, Ct), Ct);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/evidence/{stored.Hash}");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Range", "bytes=9000-9100");

        var response = await host.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    /// <summary>The list of what a case file holds is answered without touching the store.</summary>
    [Fact]
    public async Task ACaseFilesEvidenceIsListedFromTheDatabase()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);

        await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-list-1"), "one.png", caseId, Ct);
        await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-list-2"), "two.png", caseId, Ct);

        var listed = await host.ReadAsync<List<EvidenceObjectView>>(
            await host.GetAsync($"/api/evidence?reportId={caseId}", cookie, Ct), Ct);

        Assert.Equal(2, listed.Count);
        Assert.All(listed, e => Assert.Equal(caseId, e.ReportId));

        await using var context = db.NewContext();
        Assert.Equal(2, await context.EvidenceAttachments.CountAsync(a => a.CaseId == caseId, Ct));
    }

    // ── Taking off ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Taking a file off one case file ends that case file's hold and nothing else: the bytes stay,
    /// the other case file keeps its hold, and the case file keeps a trace saying who did it.
    /// </summary>
    [Fact]
    public async Task TakingAFileOffOneCaseFileLeavesTheBytesAndTheOtherCaseFile()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var first = await host.NewCaseAsync(cookie, Ct);
        var second = await host.NewCaseAsync(cookie, Ct);
        var bytes = EvidenceUploads.Png("serving-take-off");

        var stored = await EvidenceUploads.UploadAsync(host, cookie, bytes, "a.png", first, Ct);
        await EvidenceUploads.UploadAsync(host, cookie, bytes, "b.png", second, Ct);

        var response = await host.PostAsync(
            $"/api/cases/{first}/evidence/{stored.Hash}/take-off", cookie, new { }, Ct);

        response.EnsureSuccessStatusCode();

        await using (var context = db.NewContext())
        {
            var off = await context.EvidenceAttachments.AsNoTracking().SingleAsync(a => a.CaseId == first, Ct);
            Assert.False(off.IsOn);
            Assert.Equal(host.WhoIs(cookie).Id, off.TakenOffByUserId);
            Assert.Equal(host.WhoIs(cookie).Username, off.TakenOffByName);
            Assert.Equal(host.Clock.UtcNow, off.TakenOffAt);

            Assert.True((await context.EvidenceAttachments.AsNoTracking().SingleAsync(a => a.CaseId == second, Ct)).IsOn);
            Assert.False((await context.EvidenceBlobs.AsNoTracking().SingleAsync(b => b.Hash == stored.Hash, Ct)).IsDestroyed);
        }

        Assert.Equal(
            HttpStatusCode.OK,
            (await host.GetAsync($"/api/evidence/{stored.Hash}", cookie, Ct)).StatusCode);

        var fact = Assert.Single(await FactsAsync(FactType.EvidenceDetached, first));
        Assert.Equal(stored.Hash, Data(fact).GetProperty("hash").GetString());
        Assert.Equal("a.png", Data(fact).GetProperty("fileName").GetString());
        Assert.Equal(host.WhoIs(cookie).Id.ToString(), fact.ActorId);
        Assert.Empty(await FactsAsync(FactType.EvidenceDetached, second));

        // The file is still on the second and, taken off the first, can be destroyed from neither
        // until the second lets go: taking off is what makes destroying possible, one case file at
        // a time.
        (await host.PostAsync($"/api/cases/{second}/evidence/{stored.Hash}/take-off", cookie, new { }, Ct))
            .EnsureSuccessStatusCode();

        await using var after = db.NewContext();
        Assert.Empty(await new Modbot.Api.Features.Evidence.DatabaseEvidenceMetadata(after, host.Clock)
            .ReferencesAsync(Modbot.Evidence.Storage.EvidenceHash.Parse(stored.Hash), Ct));
    }

    /// <summary>
    /// Taking a file off is a change to the case file, so it is for the author or somebody who may
    /// ban, not for whoever may upload, and not once the case file is withdrawn.
    /// </summary>
    [Fact]
    public async Task TakingAFileOffFollowsTheRuleForEditingTheCaseFile()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var author = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, author, Ct);

        var caseId = await host.NewCaseAsync(author, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, author, EvidenceUploads.Png("serving-take-off-rules"), "a.png", caseId, Ct);

        var path = $"/api/cases/{caseId}/evidence/{stored.Hash}/take-off";

        var stranger = await host.SignedInAsync(ModbotPermissions.UploadEvidence, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsync(path, stranger, new { }, Ct)).StatusCode);

        var viewer = await host.SignedInAsync(ModbotPermissions.ViewEvidence, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostAsync(path, viewer, new { }, Ct)).StatusCode);

        var missing = await host.PostAsync(
            $"/api/cases/{Guid.NewGuid()}/evidence/{stored.Hash}/take-off", author, new { }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var notOnIt = await host.PostAsync(
            $"/api/cases/{caseId}/evidence/{new string('7', 64)}/take-off", author, new { }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, notOnIt.StatusCode);

        await using (var context = db.NewContext())
        {
            var row = await context.CaseFiles.SingleAsync(c => c.Id == Guid.Parse(caseId), Ct);
            row.WithdrawnAt = host.Clock.UtcNow;
            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await host.PostAsync(path, author, new { }, Ct)).StatusCode);
    }

    /// <summary>
    /// Putting a file back after it was taken off is a new hold: the old one stays as the trace of
    /// the take-off, and the case file shows the file once.
    /// </summary>
    [Fact]
    public async Task PuttingAFileBackAfterATakeOffIsANewHold()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var bytes = EvidenceUploads.Png("serving-put-back");
        var stored = await EvidenceUploads.UploadAsync(host, cookie, bytes, "a.png", caseId, Ct);

        (await host.PostAsync($"/api/cases/{caseId}/evidence/{stored.Hash}/take-off", cookie, new { }, Ct))
            .EnsureSuccessStatusCode();

        await EvidenceUploads.UploadAsync(host, cookie, bytes, "a.png", caseId, Ct);

        await using var context = db.NewContext();
        var rows = await context.EvidenceAttachments.AsNoTracking().Where(a => a.CaseId == caseId).ToListAsync(Ct);

        Assert.Equal(2, rows.Count);
        Assert.Single(rows, a => a.IsOn);
        Assert.Single(rows, a => !a.IsOn);
    }

    // ── Looking and downloading ────────────────────────────────────────────────────────────

    /// <summary>
    /// A page showing the file passes <c>view=true</c>. That is written as "Evidence viewed", once,
    /// naming the case file, the file and who: a second look within ten minutes adds nothing, and
    /// one after them adds a line.
    /// </summary>
    [Fact]
    public async Task ALookIsWrittenOncePerPersonPerFilePerTenMinutes()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-view"), "shot.png", caseId, Ct);

        var path = $"/api/evidence/{stored.Hash}?case={caseId}&view=true";

        (await host.GetAsync(path, cookie, Ct)).EnsureSuccessStatusCode();

        var first = Assert.Single(await FactsAsync(FactType.EvidenceViewed, caseId));
        Assert.Equal(host.WhoIs(cookie).Id.ToString(), first.ActorId);
        Assert.Equal(stored.Hash, Data(first).GetProperty("hash").GetString());
        Assert.Equal("shot.png", Data(first).GetProperty("fileName").GetString());
        Assert.Equal(caseId, Data(first).GetProperty("caseId").GetString());

        host.Clock.Advance(TimeSpan.FromMinutes(9));
        (await host.GetAsync(path, cookie, Ct)).EnsureSuccessStatusCode();
        Assert.Single(await FactsAsync(FactType.EvidenceViewed, caseId));

        host.Clock.Advance(TimeSpan.FromMinutes(2));
        (await host.GetAsync(path, cookie, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(2, (await FactsAsync(FactType.EvidenceViewed, caseId)).Count);

        // A look is not a download, so no download was written.
        Assert.Empty(await FactsAsync(FactType.EvidenceDownloaded, caseId));
    }

    /// <summary>
    /// A video asks for its bytes in pieces every time it is played or skipped through. Those are
    /// one look, not a hundred.
    /// </summary>
    [Fact]
    public async Task RangeRequestsOfAPlayingVideoAddNoLinesWithinTenMinutes()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-view-ranges"), "clip.png", caseId, Ct);

        foreach (var range in new[] { "bytes=0-7", "bytes=8-15", "bytes=16-23", "bytes=0-7" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/evidence/{stored.Hash}?case={caseId}&view=true");
            request.Headers.Add("Cookie", cookie);
            request.Headers.Add("Range", range);

            Assert.Equal(HttpStatusCode.PartialContent, (await host.Client.SendAsync(request, Ct)).StatusCode);
        }

        Assert.Single(await FactsAsync(FactType.EvidenceViewed, caseId));
    }

    /// <summary>The limit is per person: somebody else's look is their own line.</summary>
    [Fact]
    public async Task TwoPeopleLookingAreTwoLines()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var first = await host.SignedInAsync(Moderator, Ct);
        var second = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, first, Ct);

        var caseId = await host.NewCaseAsync(first, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, first, EvidenceUploads.Png("serving-view-two"), "shot.png", caseId, Ct);

        var path = $"/api/evidence/{stored.Hash}?case={caseId}&view=true";
        (await host.GetAsync(path, first, Ct)).EnsureSuccessStatusCode();
        (await host.GetAsync(path, second, Ct)).EnsureSuccessStatusCode();

        var facts = await FactsAsync(FactType.EvidenceViewed, caseId);
        Assert.Equal(
            new[] { host.WhoIs(first).Id.ToString(), host.WhoIs(second).Id.ToString() }.Order().ToList(),
            facts.Select(f => f.ActorId!).Order().ToList());
    }

    /// <summary>
    /// Downloading is its own entry, written every time, naming the case file, the file and who.
    /// A request that does not say it is only a look is a download.
    /// </summary>
    [Fact]
    public async Task EveryDownloadIsWrittenAsItsOwnLine()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-download"), "shot.png", caseId, Ct);

        (await host.GetAsync($"/api/evidence/{stored.Hash}?case={caseId}", cookie, Ct)).EnsureSuccessStatusCode();
        (await host.GetAsync($"/api/evidence/{stored.Hash}?case={caseId}", cookie, Ct)).EnsureSuccessStatusCode();

        // Even with no case file said: the one that holds it is named.
        (await host.GetAsync($"/api/evidence/{stored.Hash}", cookie, Ct)).EnsureSuccessStatusCode();

        var facts = await FactsAsync(FactType.EvidenceDownloaded, caseId);
        Assert.Equal(3, facts.Count);
        Assert.All(facts, fact =>
        {
            Assert.Equal(host.WhoIs(cookie).Id.ToString(), fact.ActorId);
            Assert.Equal(stored.Hash, Data(fact).GetProperty("hash").GetString());
            Assert.Equal("shot.png", Data(fact).GetProperty("fileName").GetString());
        });

        Assert.Empty(await FactsAsync(FactType.EvidenceViewed, caseId));
    }

    /// <summary>
    /// A request that got nothing leaves no line: a destroyed file, or one that never existed, was
    /// not looked at.
    /// </summary>
    [Fact]
    public async Task ARequestThatServedNothingWritesNothing()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator | ModbotPermissions.DestroyEvidence, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-nothing"), "gone.png", caseId, Ct);

        (await host.PostAsync(
            $"/api/evidence/{stored.Hash}/destroy", cookie, new { reason = "cleanup", caseId }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Gone, (await host.GetAsync($"/api/evidence/{stored.Hash}?case={caseId}", cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await host.GetAsync($"/api/evidence/{stored.Hash}?case={caseId}&view=true", cookie, Ct)).StatusCode);

        Assert.Empty(await FactsAsync(FactType.EvidenceDownloaded, caseId));
        Assert.Empty(await FactsAsync(FactType.EvidenceViewed, caseId));
    }

    /// <summary>
    /// A case file the request names that the file was never on is not taken on trust: the line
    /// names the case file that does hold it.
    /// </summary>
    [Fact]
    public async Task AWrongCaseFileInTheRequestIsNotTrusted()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var holder = await host.NewCaseAsync(cookie, Ct);
        var unrelated = await host.NewCaseAsync(cookie, Ct);
        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-wrong-case"), "shot.png", holder, Ct);

        (await host.GetAsync($"/api/evidence/{stored.Hash}?case={unrelated}", cookie, Ct)).EnsureSuccessStatusCode();

        Assert.Single(await FactsAsync(FactType.EvidenceDownloaded, holder));
        Assert.Empty(await FactsAsync(FactType.EvidenceDownloaded, unrelated));
    }
}
