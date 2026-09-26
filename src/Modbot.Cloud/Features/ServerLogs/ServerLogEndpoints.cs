using Microsoft.AspNetCore.Mvc;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.EventBackup;
using Modbot.Cloud.Features.Registry;

namespace Modbot.Cloud.Features.ServerLogs;

/// <summary>
/// <c>POST /api/v1/logs</c>: a registered Modbot deployment's batch of its own log lines.
/// </summary>
/// <remarks>
/// <para>
/// The same order as the event batch endpoint, and for the same reasons: the secret is checked
/// (<c>401</c>), batches a minute are counted (<c>429</c>), the body is read within its size limits
/// (<c>413</c>), the batch is checked (<c>400</c>), lines an hour are counted (<c>429</c>), and then
/// it is stored. Counting batches before reading the body means a flood costs Cloud a hash check
/// rather than a decompression.
/// </para>
/// <para>
/// <strong>The credential is the server registry's</strong> — the same
/// <c>Bearer &lt;serverId&gt;.&lt;secret&gt;</c> the deployment reports with. Not a desktop install's,
/// and not one of its own: a deployment registering with Cloud twice would give Cloud two ids for
/// one thing and no way to join them, and the join is exactly what lets the account that claimed
/// the server read its own logs.
/// </para>
/// <para>
/// A <c>200</c> is Cloud's last word on every line in the batch, and the deployment moves its
/// place-marker past them.
/// </para>
/// </remarks>
public static class ServerLogEndpoints
{
    public static IEndpointRouteBuilder MapServerLogs(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/logs", ReceiveAsync).RequireServer();
        return app;
    }

    internal static async Task<IResult> ReceiveAsync(
        [FromServices] CloudContext cloud,
        [FromServices] LogBatchWriter writer,
        [FromServices] ServerLogLimits limits,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        var server = await ServerAccess.RequiredAsync(http);
        var key = server.Id.ToString("N");

        if (limits.Batches.TryTake(key) is { } batchWait)
            return CloudError.TooMany(http, batchWait, "Too many batches from this server.");

        var (outcome, raw) = await LogBatchReader.ReadAsync(http.Request, ct);

        switch (outcome)
        {
            case BatchReadOutcome.TooLarge:
                return CloudError.Result(
                    StatusCodes.Status413PayloadTooLarge, "batch_too_large", "The batch is too large.");
            case BatchReadOutcome.Malformed:
                return CloudError.Result(
                    StatusCodes.Status400BadRequest, "malformed_batch", "The batch could not be read.");
        }

        var (batch, problem) = LogBatchCheck.Check(raw!);

        if (batch is null)
        {
            return problem == "too_many"
                ? CloudError.Result(
                    StatusCodes.Status413PayloadTooLarge, "batch_too_large",
                    $"A batch may carry {ServerLogLimits.MaxLinesPerBatch} lines.")
                : CloudError.Result(StatusCodes.Status400BadRequest, "malformed_batch", problem ?? "The batch is not usable.");
        }

        if (limits.Lines.TryTake(key, batch.Lines.Count) is { } lineWait)
            return CloudError.TooMany(http, lineWait, "Too many log lines from this server.");

        var receivedAt = time.GetUtcNow();
        var result = await writer.WriteAsync(server.Id, batch, receivedAt, ct);

        // The registry moves "last seen" on its six-hourly report. A log batch is a far better sign
        // of life than that, so it moves it too -- but only once it is already a minute stale, so a
        // busy deployment does not write the main database every second.
        if (receivedAt - server.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            server.LastSeenAt = receivedAt;
            await cloud.SaveChangesAsync(ct);
        }

        return Results.Ok(result);
    }
}
