using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.My.Common;
using Modbot.My.Data;
using Modbot.My.Features.Admin;
using Npgsql;

namespace Modbot.My.Features.Instances;

/// <summary>
/// Deployments registering themselves and reporting usage (central services spec 4.2 and 5), and the
/// admin reads and deletes of what they sent.
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

        app.MapGet("/api/instances", ListAsync).RequireAdmin();
        app.MapGet("/api/instances/{instanceId}", GetAsync).RequireAdmin();
        app.MapGet("/api/instances/{instanceId}/ip-history", IpHistoryAsync).RequireAdmin();
        app.MapDelete("/api/instances/{instanceId}", DeleteAsync).RequireAdmin();

        return app;
    }

    internal static async Task<IResult> RegisterAsync(
        [FromBody] RegisterRequest request,
        [FromServices] MyContext db,
        [FromServices] TimeProvider time,
        HttpContext http,
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
        var ip = ClientAddress.From(http)?.ToString();
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
                IpAddress = ip,
            };

            db.RegisteredInstances.Add(row);
        }
        else
        {
            // Deployments move between hosts; the id is what identifies them across the move.
            row.InstanceUrl = url;
            row.Version = version ?? row.Version;
            row.LastSeenAt = now;
            row.IpAddress = ip ?? row.IpAddress;
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
            return await RegisterAsync(request, db, time, http, ct);
        }

        await RecordIpAsync(db, row.InstanceId, ip, now, ct);
        return Results.Ok(new RegisterResponse(true, row.InstanceId, row.RegisteredAt));
    }

    internal static async Task<IResult> ReportUsageAsync(
        [FromRoute] string instanceId,
        [FromBody] UsageReport report,
        [FromServices] MyContext db,
        [FromServices] TimeProvider time,
        HttpContext http,
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
        var ip = ClientAddress.From(http)?.ToString();

        row.Version = version ?? row.Version;
        row.LastSeenAt = now;
        row.IpAddress = ip ?? row.IpAddress;
        row.AnalyticsEnabled = true;
        row.LastUsageReportAt = now;
        row.ScaleBucket = report.ScaleBucket;
        row.PairedClients = report.PairedClients;
        row.DiscordConnected = report.DiscordConnected;
        row.TermListsImported = report.TermListsImported?.ToList();
        row.RateLimitColdStops = report.RateLimitColdStops;
        row.WafBlocks = report.WafBlocks;

        await db.SaveChangesAsync(ct);
        await RecordIpAsync(db, row.InstanceId, ip, now, ct);

        return Results.Accepted();
    }

    /// <summary>
    /// One statement, so two calls from a new address at the same moment cannot both insert.
    /// </summary>
    private static Task RecordIpAsync(MyContext db, string instanceId, string? ip, DateTimeOffset now, CancellationToken ct) =>
        ip is null
            ? Task.CompletedTask
            : db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO registered_instance_ip (instance_id, ip_address, first_seen_at, last_seen_at, requests)
                VALUES ({instanceId}, {ip}, {now}, {now}, 1)
                ON CONFLICT (instance_id, ip_address) DO UPDATE
                SET last_seen_at = GREATEST(registered_instance_ip.last_seen_at, EXCLUDED.last_seen_at),
                    requests = registered_instance_ip.requests + 1
                """,
                ct);

    internal static async Task<IResult> ListAsync(
        [FromQuery] string? search,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] MyContext db,
        CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);

        var query = db.RegisteredInstances.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Search.Contains(search);
            query = query.Where(i =>
                EF.Functions.ILike(i.InstanceId, pattern)
                || EF.Functions.ILike(i.InstanceUrl, pattern)
                || (i.Version != null && EF.Functions.ILike(i.Version, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
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

    internal static async Task<IResult> IpHistoryAsync(
        [FromRoute] string instanceId,
        [FromServices] MyContext db,
        CancellationToken ct)
    {
        if (!await db.RegisteredInstances.AnyAsync(i => i.InstanceId == instanceId, ct))
            return Results.NotFound(new { error = "No instance has that id." });

        var rows = await db.RegisteredInstanceIps
            .AsNoTracking()
            .Where(i => i.InstanceId == instanceId)
            .OrderByDescending(i => i.LastSeenAt)
            .ToListAsync(ct);

        return Results.Ok(new InstanceIpHistory(rows
            .Select(i => new InstanceIpView(i.IpAddress, i.FirstSeenAt, i.LastSeenAt, i.Requests))
            .ToList()));
    }

    internal static async Task<IResult> DeleteAsync(
        [FromRoute] string instanceId,
        [FromServices] MyContext db,
        CancellationToken ct)
    {
        // Its IP history goes with it (the foreign key cascades).
        var deleted = await db.RegisteredInstances.Where(i => i.InstanceId == instanceId).ExecuteDeleteAsync(ct);

        return deleted == 0
            ? Results.NotFound(new { error = "No instance has that id." })
            : Results.NoContent();
    }

    private static IResult Refuse(string error) => Results.BadRequest(new { error });
}
