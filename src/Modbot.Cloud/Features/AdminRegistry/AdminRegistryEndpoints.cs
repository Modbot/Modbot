using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Registry;
using Modbot.Cloud.Features.Site;

namespace Modbot.Cloud.Features.AdminRegistry;

/// <param name="Servers">How many registered servers this account holds.</param>
public sealed record AdminAccountView(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("emailVerified")] bool EmailVerified,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lastSignedInAt")] DateTimeOffset? LastSignedInAt,
    [property: JsonPropertyName("servers")] int Servers);

public sealed record AdminPageInstanceView(
    [property: JsonPropertyName("instanceUrl")] string InstanceUrl,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("visits")] int Visits,
    [property: JsonPropertyName("alsoRegistered")] bool AlsoRegistered);

/// <param name="Registered">Servers that registered themselves.</param>
/// <param name="ActiveLast30Days">Of those, seen in the last 30 days.</param>
/// <param name="Claimed">Of those, held by an account.</param>
/// <param name="ByVersion">Registered servers per release.</param>
/// <param name="PageOnly">Addresses known only from a my.modbot.co page visit.</param>
public sealed record RegistryStats(
    [property: JsonPropertyName("accounts")] int Accounts,
    [property: JsonPropertyName("registered")] int Registered,
    [property: JsonPropertyName("activeLast30Days")] int ActiveLast30Days,
    [property: JsonPropertyName("claimed")] int Claimed,
    [property: JsonPropertyName("byVersion")] IReadOnlyDictionary<string, int> ByVersion,
    [property: JsonPropertyName("pageInstances")] int PageInstances,
    [property: JsonPropertyName("pageOnly")] int PageOnly);

/// <summary>
/// Cloud admin: every account, every registered server, what each reported, and the addresses noted
/// by my.modbot.co's pages.
/// </summary>
/// <remarks>
/// <strong>Every endpoint here needs the admin sign-in</strong> — the <c>/admin</c> cookie or
/// <c>Authorization: Bearer &lt;ROOT_API_KEY&gt;</c>. The proxy key opens none of it, and neither
/// does an account session.
/// </remarks>
public static class AdminRegistryEndpoints
{
    public static IEndpointRouteBuilder MapAdminRegistry(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAdmin();

        admin.MapGet("/accounts", AccountsAsync);
        admin.MapGet("/servers", ServersAsync);
        admin.MapGet("/servers/{serverId:guid}", ServerAsync);
        admin.MapGet("/servers/{serverId:guid}/reports", ReportsAsync);
        admin.MapDelete("/servers/{serverId:guid}", DeleteServerAsync);
        admin.MapGet("/page-instances", PageInstancesAsync);
        admin.MapDelete("/page-instances", DeletePageInstanceAsync);
        admin.MapGet("/registry-stats", StatsAsync);

        return app;
    }

    internal static async Task<IResult> AccountsAsync(
        [FromQuery] string? search,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] CloudContext db,
        CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);
        var query = db.Accounts.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(a => EF.Functions.ILike(a.Email, Search.Contains(search)));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(a => new AdminAccountView(
                a.Id,
                a.Email,
                a.EmailVerifiedAt != null,
                a.CreatedAt,
                a.LastSignedInAt,
                db.RegisteredServers.Count(s => s.AccountId == a.Id)))
            .ToListAsync(ct);

        return Results.Ok(new Page<AdminAccountView>(total, skip, take, rows));
    }

    internal static async Task<IResult> ServersAsync(
        [FromQuery] string? search,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] CloudContext db,
        CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);
        var query = db.RegisteredServers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Search.Contains(search);
            query = query.Where(s =>
                (s.PublicAddress != null && EF.Functions.ILike(s.PublicAddress, pattern))
                || (s.GroupId != null && EF.Functions.ILike(s.GroupId, pattern))
                || (s.GroupName != null && EF.Functions.ILike(s.GroupName, pattern))
                || (s.Version != null && EF.Functions.ILike(s.Version, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(s => s.LastSeenAt)
            .Skip(skip)
            .Take(take)
            .Select(s => new { Server = s, Email = db.Accounts.Where(a => a.Id == s.AccountId).Select(a => a.Email).FirstOrDefault() })
            .ToListAsync(ct);

        return Results.Ok(new Page<AdminServerView>(
            total,
            skip,
            take,
            rows.Select(r => new AdminServerView(ServerView.From(r.Server), r.Email, r.Server.IpAddress)).ToList()));
    }

    internal static async Task<IResult> ServerAsync(
        [FromRoute] Guid serverId,
        [FromServices] CloudContext db,
        CancellationToken ct)
    {
        var row = await db.RegisteredServers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId, ct);
        if (row is null)
            return Results.NotFound(new { error = "No server has that id." });

        var email = row.AccountId is { } accountId
            ? await db.Accounts.Where(a => a.Id == accountId).Select(a => a.Email).FirstOrDefaultAsync(ct)
            : null;

        return Results.Ok(new AdminServerView(ServerView.From(row), email, row.IpAddress));
    }

    internal static async Task<IResult> ReportsAsync(
        [FromRoute] Guid serverId,
        [FromQuery] int? limit,
        [FromServices] CloudContext db,
        CancellationToken ct)
    {
        if (!await db.RegisteredServers.AnyAsync(s => s.Id == serverId, ct))
            return Results.NotFound(new { error = "No server has that id." });

        var (_, take) = Paging.Clamp(0, limit);

        var rows = await db.ServerReports
            .AsNoTracking()
            .Where(r => r.ServerId == serverId)
            .OrderByDescending(r => r.ReportedAt)
            .Take(take)
            .Select(r => new ServerReportView(
                r.ReportedAt,
                r.Version,
                r.HostPlatform,
                r.GroupId,
                r.GroupName,
                r.DiscordConnected,
                r.TermListsImported,
                r.RateLimitColdStops,
                r.WafBlocks,
                r.AiModerationEnabled,
                r.IpAddress))
            .ToListAsync(ct);

        return Results.Ok(new { items = rows });
    }

    /// <summary>Deleting a server deletes its reports and unclaims it (the foreign key cascades).</summary>
    internal static async Task<IResult> DeleteServerAsync(
        [FromRoute] Guid serverId,
        [FromServices] CloudContext db,
        CancellationToken ct)
    {
        var deleted = await db.RegisteredServers.Where(s => s.Id == serverId).ExecuteDeleteAsync(ct);

        return deleted == 0
            ? Results.NotFound(new { error = "No server has that id." })
            : Results.NoContent();
    }

    internal static async Task<IResult> PageInstancesAsync(
        [FromQuery] string? search,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] CloudContext db,
        CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);
        var query = db.PageInstances.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(i => EF.Functions.ILike(i.InstanceUrl, Search.Contains(search)));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(i => i.LastSeenAt)
            .ThenBy(i => i.InstanceUrl)
            .Skip(skip)
            .Take(take)
            .Select(i => new AdminPageInstanceView(
                i.InstanceUrl,
                i.FirstSeenAt,
                i.LastSeenAt,
                i.Visits,
                db.RegisteredServers.Any(s => s.PublicAddress == i.InstanceUrl)))
            .ToListAsync(ct);

        return Results.Ok(new Page<AdminPageInstanceView>(total, skip, take, rows));
    }

    /// <summary>
    /// Forgets a noted address: its row, and the visitor lists that would otherwise keep offering it
    /// to the people who opened it.
    /// </summary>
    internal static async Task<IResult> DeletePageInstanceAsync(
        [FromQuery] string? url,
        [FromServices] CloudContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Results.BadRequest(new { error = "url is required." });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var deleted = await db.PageInstances.Where(i => i.InstanceUrl == url).ExecuteDeleteAsync(ct);
        await db.VisitorInstances.Where(v => v.InstanceUrl == url).ExecuteDeleteAsync(ct);

        await transaction.CommitAsync(ct);

        return deleted == 0
            ? Results.NotFound(new { error = "No entry has that address." })
            : Results.NoContent();
    }

    internal static async Task<IResult> StatsAsync(
        [FromServices] CloudContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-30);

        var byVersion = await db.RegisteredServers
            .Where(s => s.Version != null)
            .GroupBy(s => s.Version!)
            .Select(g => new { Version = g.Key, Count = g.Count() })
            .ToDictionaryAsync(v => v.Version, v => v.Count, ct);

        return Results.Ok(new RegistryStats(
            await db.Accounts.CountAsync(ct),
            await db.RegisteredServers.CountAsync(ct),
            await db.RegisteredServers.CountAsync(s => s.LastSeenAt >= since, ct),
            await db.RegisteredServers.CountAsync(s => s.AccountId != null, ct),
            byVersion,
            await db.PageInstances.CountAsync(ct),
            await db.PageInstances.CountAsync(i => !db.RegisteredServers.Any(s => s.PublicAddress == i.InstanceUrl), ct)));
    }
}
