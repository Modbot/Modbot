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

/// <param name="OwnerEmail">
/// Who runs it, when the address said so on a register visit. <strong>Admin only</strong> — it is
/// stored so the maintainer can reach an operator, and appears nowhere else (register details spec 3.4).
/// </param>
public sealed record AdminPageServerView(
    [property: JsonPropertyName("serverUrl")] string ServerUrl,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("visits")] int Visits,
    [property: JsonPropertyName("alsoRegistered")] bool AlsoRegistered,
    [property: JsonPropertyName("groupId")] string? GroupId,
    [property: JsonPropertyName("groupName")] string? GroupName,
    [property: JsonPropertyName("groupIconUrl")] string? GroupIconUrl,
    [property: JsonPropertyName("groupBannerUrl")] string? GroupBannerUrl,
    [property: JsonPropertyName("ownerEmail")] string? OwnerEmail)
{
    /// <summary>
    /// The same address under its old name, added 2026-09-26 when "instance" became "server".
    /// Drop it with the <c>/page-instances</c> routes.
    /// </summary>
    [JsonPropertyName("instanceUrl")]
    public string OldServerUrl => ServerUrl;
}

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
    [property: JsonPropertyName("pageServers")] int PageServers,
    [property: JsonPropertyName("pageOnly")] int PageOnly)
{
    /// <summary>
    /// The same count under its old name, added 2026-09-26 when "instance" became "server".
    /// Drop it with the <c>/page-instances</c> routes.
    /// </summary>
    [JsonPropertyName("pageInstances")]
    public int OldPageServers => PageServers;
}

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
        admin.MapGet("/page-servers", PageServersAsync);
        admin.MapDelete("/page-servers", DeletePageServerAsync);

        // The old paths, kept 2026-09-26 when "instance" became "server" so a caller of the old
        // name keeps working. Drop them, and the old JSON names above, once nothing calls them.
        admin.MapGet("/page-instances", PageServersAsync);
        admin.MapDelete("/page-instances", DeletePageServerAsync);
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
            .Select(s => new
            {
                Server = s,
                Email = db.Accounts.Where(a => a.Id == s.AccountId).Select(a => a.Email).FirstOrDefault(),
                OwnerEmail = db.VisitedServers
                    .Where(v => v.ServerUrl == s.PublicAddress)
                    .Select(v => v.OwnerEmail)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Results.Ok(new Page<AdminServerView>(
            total,
            skip,
            take,
            rows
                .Select(r => new AdminServerView(
                    ServerView.From(r.Server), r.Email, r.Server.IpAddress, r.OwnerEmail))
                .ToList()));
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

        var ownerEmail = row.PublicAddress is { } address
            ? await db.VisitedServers
                .Where(v => v.ServerUrl == address)
                .Select(v => v.OwnerEmail)
                .FirstOrDefaultAsync(ct)
            : null;

        return Results.Ok(new AdminServerView(ServerView.From(row), email, row.IpAddress, ownerEmail));
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

    internal static async Task<IResult> PageServersAsync(
        [FromQuery] string? search,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] CloudContext db,
        CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);
        var query = db.PageServers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(i => EF.Functions.ILike(i.ServerUrl, Search.Contains(search)));

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(i => i.LastSeenAt)
            .ThenBy(i => i.ServerUrl)
            .Skip(skip)
            .Take(take)
            .Select(i => new
            {
                Server = i,
                AlsoRegistered = db.RegisteredServers.Any(s => s.PublicAddress == i.ServerUrl),
                Learned = db.VisitedServers.FirstOrDefault(s => s.ServerUrl == i.ServerUrl),
            })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new AdminPageServerView(
                r.Server.ServerUrl,
                r.Server.FirstSeenAt,
                r.Server.LastSeenAt,
                r.Server.Visits,
                r.AlsoRegistered,
                r.Learned?.GroupId,
                r.Learned?.GroupName,
                r.Learned?.GroupIconUrl,
                r.Learned?.GroupBannerUrl,
                r.Learned?.OwnerEmail))
            .ToList();

        return Results.Ok(new Page<AdminPageServerView>(total, skip, take, items));
    }

    /// <summary>
    /// Forgets a noted address: its row, and the visitor lists that would otherwise keep offering it
    /// to the people who opened it.
    /// </summary>
    internal static async Task<IResult> DeletePageServerAsync(
        [FromQuery] string? url,
        [FromServices] CloudContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Results.BadRequest(new { error = "url is required." });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var deleted = await db.PageServers.Where(i => i.ServerUrl == url).ExecuteDeleteAsync(ct);
        await db.VisitorServers.Where(v => v.ServerUrl == url).ExecuteDeleteAsync(ct);

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
            await db.PageServers.CountAsync(ct),
            await db.PageServers.CountAsync(i => !db.RegisteredServers.Any(s => s.PublicAddress == i.ServerUrl), ct)));
    }
}
