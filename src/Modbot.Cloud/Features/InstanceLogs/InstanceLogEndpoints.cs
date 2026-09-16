using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.EventBackup;
using Modbot.Cloud.Features.Installs;

namespace Modbot.Cloud.Features.InstanceLogs;

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
/// The credential is the same one the desktop clients use: an install id and secret, registered at
/// <c>POST /api/v1/installs</c>. A deployment registers with <c>platform: "server"</c>, which is how
/// the admin viewer tells the two apart. It is a deliberately small assumption — when Cloud grows
/// accounts and an instance registry, a deployment's credential becomes an account's, and this
/// endpoint changes in one place.
/// </para>
/// <para>
/// A <c>200</c> is Cloud's last word on every line in the batch, and the deployment moves its
/// place-marker past them.
/// </para>
/// </remarks>
public static class InstanceLogEndpoints
{
    /// <summary>The platform a Modbot deployment registers as, rather than <c>windows</c>.</summary>
    public const string ServerPlatform = "server";

    /// <summary>How stale <c>install.last_seen_at</c> may get before a batch updates it.</summary>
    private static readonly TimeSpan LastSeenEvery = TimeSpan.FromMinutes(1);

    public static IEndpointRouteBuilder MapInstanceLogs(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/logs", ReceiveAsync);
        return app;
    }

    internal static async Task<IResult> ReceiveAsync(
        [FromServices] CloudContext cloud,
        [FromServices] LogBatchWriter writer,
        [FromServices] InstanceLogLimits limits,
        [FromServices] TimeProvider time,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

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
                    $"A batch may carry {InstanceLogLimits.MaxLinesPerBatch} lines.")
                : CloudError.Result(StatusCodes.Status400BadRequest, "malformed_batch", problem ?? "The batch is not usable.");
        }

        if (limits.Lines.TryTake(key, batch.Lines.Count) is { } lineWait)
            return CloudError.TooMany(http, lineWait, "Too many log lines from this install.");

        var receivedAt = time.GetUtcNow();
        var result = await writer.WriteAsync(installId, batch, receivedAt, ct);

        await NoteSeenAsync(cloud, install, batch, receivedAt, ct);

        return Results.Ok(result);
    }

    /// <summary>
    /// Keeps the install row current without writing the main database on every batch.
    /// </summary>
    private static async Task NoteSeenAsync(
        CloudContext cloud, Install install, CheckedLogBatch batch, DateTimeOffset now, CancellationToken ct)
    {
        var version = batch.ServerVersion is { Length: > 0 } v
            ? (v.Length > Install.MaxVersionLength ? v[..Install.MaxVersionLength] : v)
            : install.ClientVersion;

        var changed = !string.Equals(install.ClientVersion, version, StringComparison.Ordinal);

        if (!changed && now - install.LastSeenAt < LastSeenEvery)
            return;

        install.ClientVersion = version;
        install.LastSeenAt = now;

        await cloud.SaveChangesAsync(ct);
    }

    private static IResult Unauthorised() =>
        CloudError.Result(StatusCodes.Status401Unauthorized, "unauthorised", "This install is not known.");
}
