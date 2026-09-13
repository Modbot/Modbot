using System.Text;
using Modbot.Api.Features.Evidence;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// The three-phase upload, driven the way a browser drives it.
/// </summary>
/// <remarks>
/// Shared rather than repeated because almost every test about serving or destroying evidence
/// needs some first, and because a helper that goes through the real endpoints keeps those tests
/// honest — nothing here reaches past the HTTP surface to plant a row.
/// </remarks>
internal static class EvidenceUploads
{
    /// <summary>Points the deployment at its scratch directory and sets it up.</summary>
    public static async Task ConfigureAsync(
        EvidenceApiTestHost host, string cookie, CancellationToken ct)
    {
        var response = await host.PutAsync(
            "/api/settings/evidence/backend",
            cookie,
            new { backend = "Filesystem", root = host.Root },
            ct);

        var result = await host.ReadAsync<EvidenceSetupResponse>(response, ct);

        Assert.True(result.Succeeded, result.Message);
    }

    /// <summary>Begin, transfer, commit.</summary>
    public static async Task<EvidenceCommitResponse> UploadAsync(
        EvidenceApiTestHost host,
        string cookie,
        byte[] bytes,
        string fileName,
        string? reportId,
        CancellationToken ct)
    {
        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName, contentType = "image/png", length = bytes.Length, reportId },
                ct),
            ct);

        var transferred = await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, ct);
        transferred.EnsureSuccessStatusCode();

        var committed = await host.PostAsync(
            $"/api/evidence/uploads/{ticket.UploadId}/commit", cookie, new { }, ct);

        committed.EnsureSuccessStatusCode();

        return await host.ReadAsync<EvidenceCommitResponse>(committed, ct);
    }

    /// <summary>
    /// A PNG, recognisable from its own first eight bytes and distinct per caller.
    /// </summary>
    /// <remarks>
    /// Distinct because the store is content-addressed and the database is shared across the
    /// assembly: two tests uploading identical bytes would be writing the same row, and each would
    /// pass or fail depending on which ran first.
    /// </remarks>
    public static byte[] Png(string salt)
        => [.. (byte[])[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A],
            .. Encoding.UTF8.GetBytes(salt.PadRight(32, '.'))];

    /// <summary>
    /// An SVG, which is a script execution format wearing an image's file extension.
    /// </summary>
    public static byte[] Svg()
        => Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
}
