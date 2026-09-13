using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// Evidence design §9: begin, transfer, commit — and what happens to a file that lies.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EvidenceUploadTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A file goes in, and the metadata row describes it exactly once it is committed.
    /// </summary>
    [Fact]
    public async Task AFileIsHashed_Typed_AndAttachedOnlyAtCommit()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence, Ct);

        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var bytes = EvidenceUploads.Png("upload-happy-path");

        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName = "proof.png", contentType = "image/png", length = bytes.Length, reportId = "report-upload" },
                Ct),
            Ct);

        Assert.False(ticket.Presigned);
        Assert.Contains("image/png", ticket.AcceptedTypes);

        await using (var context = db.NewContext())
        {
            // Phase 1 attaches nothing. A moderator who closes the tab here leaves no trace on a
            // case file at all.
            Assert.Equal(0, await context.EvidenceBlobs.CountAsync(b => b.ReportId == "report-upload", Ct));
        }

        var staged = await host.ReadAsync<EvidenceStagedView>(
            await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, Ct), Ct);

        Assert.Equal(bytes.Length, staged.ByteSize);

        await using (var context = db.NewContext())
        {
            // Still nothing. Bytes are in the store under a staging key, attached to nothing.
            Assert.Equal(0, await context.EvidenceBlobs.CountAsync(b => b.ReportId == "report-upload", Ct));
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

            Assert.Equal("report-upload", row.ReportId);
            Assert.Equal("proof.png", row.FileName);
            Assert.Equal(bytes.Length, row.ByteSize);
            Assert.Equal((short)Modbot.Evidence.Options.EvidenceBackend.Filesystem, row.Backend);
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

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence, Ct);

        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var bytes = EvidenceUploads.Svg();

        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName = "harmless.png", contentType = "image/png", length = bytes.Length, reportId = "report-svg" },
                Ct),
            Ct);

        (await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, Ct)).EnsureSuccessStatusCode();

        var committed = await host.PostAsync(
            $"/api/evidence/uploads/{ticket.UploadId}/commit", cookie, new { }, Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, committed.StatusCode);

        await using var context = db.NewContext();
        Assert.Equal(0, await context.EvidenceBlobs.CountAsync(b => b.ReportId == "report-svg", Ct));
    }

    /// <summary>A declared type that is not on the allowlist is refused before a byte moves.</summary>
    [Fact]
    public async Task ADeclaredTypeOffTheAllowlistIsRefusedAtPhaseOne()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence, Ct);

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

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence, Ct);

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

        var admin = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence, Ct);

        await EvidenceUploads.ConfigureAsync(host, admin, Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewEvidence, Ct);

        var response = await host.PostAsync(
            "/api/evidence/uploads", cookie, new { fileName = "proof.png" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
