using System.Net;
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
        var stored = await EvidenceUploads.UploadAsync(host, cookie, bytes, "proof.png", "report-serve", Ct);

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
            "report-filename",
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

        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-destroyed"), "gone.png", null, Ct);

        var destroyed = await host.ReadAsync<EvidenceDestroyResponse>(
            await host.PostAsync(
                $"/api/evidence/{stored.Hash}/destroy",
                cookie,
                new { reason = "subject erasure request" },
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
    /// Bytes a report still cites are not destroyed, and the reports are named back.
    /// </summary>
    /// <remarks>
    /// Content addressing means two case files can share one object, so destroying on the strength
    /// of one report would take evidence out of another nobody was looking at. That failure is
    /// quiet and unrecoverable, which is why it is refused rather than warned about.
    /// </remarks>
    [Fact]
    public async Task BytesACaseFileStillCitesAreNotDestroyed()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator | ModbotPermissions.DestroyEvidence, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-refcount"), "shared.png", "report-cited", Ct);

        var result = await host.ReadAsync<EvidenceDestroyResponse>(
            await host.PostAsync(
                $"/api/evidence/{stored.Hash}/destroy", cookie, new { reason = "tidying up" }, Ct),
            Ct);

        Assert.False(result.Destroyed);
        Assert.Equal(["report-cited"], result.BlockedByReports);

        // And the bytes are still there, which is the thing that actually matters.
        var served = await host.GetAsync($"/api/evidence/{stored.Hash}", cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
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
            host, cookie, EvidenceUploads.Png("serving-noreason"), "x.png", null, Ct);

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

        var stored = await EvidenceUploads.UploadAsync(
            host, admin, EvidenceUploads.Png("serving-403"), "private.png", "report-403", Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.GetAsync($"/api/evidence/{stored.Hash}", cookie, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.GetAsync("/api/evidence?reportId=report-403", cookie, Ct)).StatusCode);
    }

    /// <summary>Destroying is the narrowest permission, and viewing does not imply it.</summary>
    [Fact]
    public async Task WithoutDestroyEvidence_Is403()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var stored = await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-destroy-403"), "x.png", null, Ct);

        var response = await host.PostAsync(
            $"/api/evidence/{stored.Hash}/destroy", cookie, new { reason = "no" }, Ct);

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
        var stored = await EvidenceUploads.UploadAsync(host, cookie, bytes, "clip.png", null, Ct);

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
            host, cookie, EvidenceUploads.Png("serving-range-past"), "clip.png", null, Ct);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/evidence/{stored.Hash}");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Range", "bytes=9000-9100");

        var response = await host.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    /// <summary>The list of what a case file holds is answered without touching the store.</summary>
    [Fact]
    public async Task ACaseFilesEvidenceIsListedFromTheProjection()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-list-1"), "one.png", "report-list", Ct);
        await EvidenceUploads.UploadAsync(
            host, cookie, EvidenceUploads.Png("serving-list-2"), "two.png", "report-list", Ct);

        var listed = await host.ReadAsync<List<EvidenceObjectView>>(
            await host.GetAsync("/api/evidence?reportId=report-list", cookie, Ct), Ct);

        Assert.Equal(2, listed.Count);
        Assert.All(listed, e => Assert.Equal("report-list", e.ReportId));

        await using var context = db.NewContext();
        Assert.Equal(2, await context.EvidenceBlobs.CountAsync(b => b.ReportId == "report-list", Ct));
    }
}
