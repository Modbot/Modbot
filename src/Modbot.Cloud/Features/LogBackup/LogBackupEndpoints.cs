using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Installs;

namespace Modbot.Cloud.Features.LogBackup;

/// <summary>
/// <c>POST /api/v1/logs</c>: a registered client's batch of VRChat log lines.
/// </summary>
/// <remarks>
/// <para>
/// In order: the install's secret is checked (<c>401</c>), its batches a minute are counted
/// (<c>429</c>), the body is read within its size limits (<c>413</c>), the batch is checked
/// (<c>400</c>), its lines an hour are counted (<c>429</c>), and then it is stored. Counting batches
/// before reading the body means a flood costs Cloud a hash check, not a decompression.
/// </para>
/// <para>
/// A <c>200</c> is Cloud's last word on every line in the batch — stored or already had — and the
/// client deletes the batch from its outbox.
/// </para>
/// </remarks>
public static class LogBackupEndpoints
{
    /// <summary>How stale <c>install.last_seen_at</c> may get before a batch updates it.</summary>
    private static readonly TimeSpan LastSeenEvery = TimeSpan.FromMinutes(1);

    public static IEndpointRouteBuilder MapLogBackup(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/logs", ReceiveAsync);
        return app;
    }

    internal static async Task<IResult> ReceiveAsync(
        [FromServices] CloudContext cloud,
        [FromServices] LogBatchWriter writer,
        [FromServices] LogBackupLimits limits,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        if (!InstallSecrets.TryRead(http.Request.Headers.Authorization.ToString(), out var installId, out var secret))
            return Unauthorised();

        var install = await cloud.Installs.SingleOrDefaultAsync(i => i.Id == installId, ct);
        if (install is null || !InstallSecrets.Matches(install.SecretHash, secret))
            return Unauthorised();

        var key = installId.ToString("N");
        if (limits.Batches.TryTake(key) is { } batchWait)
            return CloudError.TooMany(http, batchWait, "Too many batches from this install.");

        var (outcome, raw) = await LogBatchReader.ReadAsync(http.Request, ct);
        switch (outcome)
        {
            case LogBatchReadOutcome.TooLarge:
                return CloudError.Result(StatusCodes.Status413PayloadTooLarge, "batch_too_large", "The batch is too large.");
            case LogBatchReadOutcome.Malformed:
                return CloudError.Result(StatusCodes.Status400BadRequest, "malformed_batch", "The batch could not be read.");
        }

        var (batch, problem) = LogBatchCheck.Check(raw!);
        if (batch is null)
        {
            return raw!.Lines?.Count > LogBackupLimits.MaxLinesPerBatch
                ? CloudError.Result(StatusCodes.Status413PayloadTooLarge, "batch_too_large", problem!)
                : CloudError.Result(StatusCodes.Status400BadRequest, "malformed_batch", problem!);
        }

        if (limits.Lines.TryTake(key, batch.Lines.Count) is { } linesWait)
            return CloudError.TooMany(http, linesWait, "Too many lines from this install.");

        var receivedAt = time.GetUtcNow();
        var result = await writer.WriteAsync(installId, batch, receivedAt, ct);

        await NoteSeenAsync(cloud, install, batch, receivedAt, ct);

        return Results.Ok(result);
    }

    private static IResult Unauthorised() =>
        CloudError.Result(StatusCodes.Status401Unauthorized, "unauthorised", "This install is not known.");

    /// <summary>
    /// Last seen, the client's version and the paired server's id, written only when one of them
    /// changed or last seen is a minute old, so a busy install is not a write to the main database
    /// per batch.
    /// </summary>
    private static async Task NoteSeenAsync(CloudContext cloud, Install install, CheckedBatch batch, DateTimeOffset now, CancellationToken ct)
    {
        var version = batch.ClientVersion["client/".Length..];
        var changed = !string.Equals(install.ClientVersion, version, StringComparison.Ordinal)
            || !string.Equals(install.ModbotServerId, batch.ModbotServerId, StringComparison.Ordinal);

        if (!changed && now - install.LastSeenAt < LastSeenEvery)
            return;

        install.ClientVersion = version;
        install.ModbotServerId = batch.ModbotServerId;
        install.LastSeenAt = now;
        await cloud.SaveChangesAsync(ct);
    }
}
