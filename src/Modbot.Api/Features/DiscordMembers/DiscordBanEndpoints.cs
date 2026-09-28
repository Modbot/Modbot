using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Names;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.DiscordMembers;

/// <summary>One ban in the Discord server, standing or lifted.</summary>
/// <param name="DisplayName">The name they had in the server, else their Discord name. Null when neither is known.</param>
/// <param name="Reason">The reason the moderator gave, when Discord or its audit log said. Untrusted text.</param>
/// <param name="BannedAt">When the ban was issued, when Modbot saw it or the audit log said. Null for a ban found already in place.</param>
/// <param name="FirstSeenAt">When Modbot first saw the ban.</param>
/// <param name="LiftedAt">When the ban was lifted, or null while it stands.</param>
public sealed record DiscordBanView(
    string UserId,
    string? Username,
    string? DisplayName,
    string? AvatarUrl,
    string? Reason,
    DateTimeOffset? BannedAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset? LiftedAt);

/// <param name="GuildId">The server in settings, or null when none is set.</param>
/// <param name="ListedAt">When the whole ban list was last read from Discord. Null until it has been.</param>
/// <param name="CanRead">Whether the bot holds Ban Members, which reading the whole list needs.</param>
/// <param name="Standing">Bans that stand, whatever the filters.</param>
/// <param name="Now">The server's clock (spec 4.4).</param>
public sealed record DiscordBanListCoverage(
    string? GuildId,
    DateTimeOffset? ListedAt,
    bool CanRead,
    int Standing,
    DateTimeOffset Now);

/// <param name="Total">Rows matching the filters, across every page.</param>
public sealed record DiscordBanListResponse(
    IReadOnlyList<DiscordBanView> Bans,
    int Total,
    int Page,
    int PageSize,
    DiscordBanListCoverage Coverage);

/// <summary>
/// <c>GET /api/discord/bans</c>: the Discord server's ban list as the bot last saw it, for the Bans
/// page's Discord list.
/// </summary>
/// <remarks>
/// Read from <c>discord_ban</c>, which the bot keeps (<c>DiscordBanList</c>). Gated on
/// <see cref="ModbotPermissions.ViewAuditLog"/>, like the group's ban list beside it.
/// </remarks>
public static class DiscordBanEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapDiscordBans(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/discord/bans", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? search,
                [FromQuery] string? status,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (status is not (null or "" or "current" or "lifted" or "all"))
                    return Results.BadRequest(new { error = "`status` is current, lifted or all." });

                return Results.Ok(await ListAsync(db, clock, search, status, page, pageSize, ct));
            })
            .WithTags("Discord")
            .RequireAuthorization()
            .RequiresFlag(ModbotPermissions.ViewAuditLog)
            .WithName("GetDiscordBans")
            .WithSummary("List Discord bans")
            .WithDescription(
                "The Discord server's bans as the bot last saw them. Bans that stand by default; "
                + "`status=lifted` shows bans that were lifted, `status=all` both. `search` matches the "
                + "name, username and id, case-insensitively. Newest first.\n\n"
                + "The whole list is read from Discord when the bot signs in and once a day, which needs "
                + "Ban Members (`coverage.canRead`); bans and unbans seen as they happen change the list "
                + "between reads. `bannedAt` is null for a ban that was already in place when it was "
                + "found, because Discord's list carries no date. `coverage.listedAt` is null until the "
                + "whole list has been read once.")
            .Produces<DiscordBanListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    public static async Task<DiscordBanListResponse> ListAsync(
        ModbotContext db,
        IModbotClock clock,
        string? search,
        string? status,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(1, page ?? 1);

        var guildId = await DiscordMemberEndpoints.GuildIdAsync(db, ct);
        var server = guildId is null
            ? null
            : await db.DiscordServers.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct);

        var inGuild = db.DiscordBans.AsNoTracking().Where(b => b.GuildId == guildId);

        var query = status switch
        {
            "lifted" => inGuild.Where(b => b.LiftedAt != null),
            "all" => inGuild,
            _ => inGuild.Where(b => b.LiftedAt == null),
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var pattern = NameSearch.Pattern(term);

            query = query.Where(b =>
                (b.DisplayName != null && EF.Functions.ILike(b.DisplayName, pattern, "\\"))
                || (b.Username != null && EF.Functions.ILike(b.Username, pattern, "\\"))
                || b.UserId == term);
        }

        var total = await query.CountAsync(ct);

        // Newest first: a ban seen as it happened by when, one found already in place by when it was found.
        var rows = await query
            .OrderByDescending(b => b.LiftedAt ?? b.BannedAt ?? b.FirstSeenAt)
            .ThenBy(b => b.UserId)
            .Skip((number - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return new DiscordBanListResponse(
            rows.Select(View).ToList(),
            total,
            number,
            size,
            new DiscordBanListCoverage(
                guildId,
                server?.BansListedAt,
                server?.BotCanBanMembers ?? false,
                await inGuild.CountAsync(b => b.LiftedAt == null, ct),
                clock.UtcNow));
    }

    private static DiscordBanView View(DiscordBan b) => new(
        b.UserId,
        b.Username,
        b.DisplayName,
        b.AvatarUrl,
        b.Reason,
        b.BannedAt,
        b.FirstSeenAt,
        b.LiftedAt);
}
