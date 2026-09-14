using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.My.Auth;
using Modbot.My.Common;
using Modbot.My.Data;
using Npgsql;

namespace Modbot.My.Features.Instances;

/// <summary>
/// Deployments registering themselves and reporting usage (central services spec 4.2 and 5), and the
/// root-key reads of what they sent.
/// </summary>
public static class InstanceEndpoints
{
    public const int MaxIdLength = 64;
    public const int MaxVersionLength = 64;

    public static IEndpointRouteBuilder MapInstances(this IEndpointRouteBuilder app)
    {
        // Open: a deployment calls these itself, and has no key.
        app.MapPost("/api/instances/register", RegisterAsync);
        app.MapPost("/api/instances/{instanceId}/usage", ReportUsageAsync);

        // Reading the registry needs the root API key.
        app.MapGet("/api/instances", ListAsync).RequireRootApiKey();
        app.MapGet("/api/instances/{instanceId}", GetAsync).RequireRootApiKey();

        return app;
    }

    internal static async Task<IResult> RegisterAsync(
        [FromBody] RegisterRequest request,
        [FromServices] MyContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var instanceId = request.InstanceId?.Trim();
        if (string.IsNullOrEmpty(instanceId) || string.IsNullOrWhiteSpace(request.InstanceUrl))
            return Refuse("instanceId and instanceUrl are required.");

        if (instanceId.Length > MaxIdLength)
            return Refuse($"instanceId must be at most {MaxIdLength} characters.");

        if (!InstanceUrl.TryNormalise(request.InstanceUrl, out var url))
            return Refuse("instanceUrl must be an absolute https URL.");

        var version = string.IsNullOrWhiteSpace(request.Version) ? null : request.Version.Trim();
        if (version?.Length > MaxVersionLength)
            return Refuse($"version must be at most {MaxVersionLength} characters.");

        var now = time.GetUtcNow();
        var row = await db.RegisteredInstances.FirstOrDefaultAsync(i => i.InstanceId == instanceId, ct);

        if (row is null)
        {
            row = new RegisteredInstance
            {
                InstanceId = instanceId,
                InstanceUrl = url,
                Version = version,
                RegisteredAt = now,
                LastSeenAt = now,
            };

            db.RegisteredInstances.Add(row);
        }
        else
        {
            // Deployments move between hosts; the id is what identifies them across the move.
            row.InstanceUrl = url;
            row.Version = version ?? row.Version;
            row.LastSeenAt = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two first registrations of the same id raced and the other won. This one is now a
            // re-registration, and the second pass finds the row.
            db.ChangeTracker.Clear();
            return await RegisterAsync(request, db, time, ct);
        }

        return Results.Ok(new RegisterResponse(true, row.InstanceId, row.RegisteredAt));
    }

    internal static async Task<IResult> ReportUsageAsync(
        [FromRoute] string instanceId,
        [FromBody] UsageReport report,
        [FromServices] MyContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var row = await db.RegisteredInstances.FirstOrDefaultAsync(i => i.InstanceId == instanceId, ct);

        // Never an implicit create: usage reporting must not be what enrols a deployment.
        if (row is null)
            return Results.NotFound(new { error = "Unknown instanceId. Register first." });

        var version = string.IsNullOrWhiteSpace(report.Version) ? null : report.Version.Trim();
        if (version?.Length > MaxVersionLength)
            return Refuse($"version must be at most {MaxVersionLength} characters.");

        var now = time.GetUtcNow();

        row.Version = version ?? row.Version;
        row.LastSeenAt = now;
        row.AnalyticsEnabled = true;
        row.LastUsageReportAt = now;
        row.ScaleBucket = report.ScaleBucket;
        row.PairedClients = report.PairedClients;
        row.DiscordConnected = report.DiscordConnected;
        row.TermListsImported = report.TermListsImported?.ToList();
        row.RateLimitColdStops = report.RateLimitColdStops;
        row.WafBlocks = report.WafBlocks;

        await db.SaveChangesAsync(ct);
        return Results.Accepted();
    }

    internal static async Task<IResult> ListAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] MyContext db,
        CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);

        var total = await db.RegisteredInstances.CountAsync(ct);
        var rows = await db.RegisteredInstances
            .AsNoTracking()
            .OrderByDescending(i => i.LastSeenAt)
            .ThenBy(i => i.InstanceId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return Results.Ok(new Page<InstanceView>(total, skip, take, rows.Select(InstanceView.From).ToList()));
    }

    internal static async Task<IResult> GetAsync(
        [FromRoute] string instanceId,
        [FromServices] MyContext db,
        CancellationToken ct)
    {
        var row = await db.RegisteredInstances.AsNoTracking().FirstOrDefaultAsync(i => i.InstanceId == instanceId, ct);

        return row is null
            ? Results.NotFound(new { error = "No instance has that id." })
            : Results.Ok(InstanceView.From(row));
    }

    private static IResult Refuse(string error) => Results.BadRequest(new { error });
}
