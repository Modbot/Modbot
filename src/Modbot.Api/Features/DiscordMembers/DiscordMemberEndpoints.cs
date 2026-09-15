using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordMembers;

/// <summary>A role a Discord member holds, with its name as last read.</summary>
/// <param name="Name">Null when the role is not in the stored role list.</param>
/// <param name="Color">0xRRGGBB, zero for none.</param>
public sealed record DiscordMemberRoleView(string Id, string? Name, int Color);

/// <summary>One member of the Discord server, current or past.</summary>
/// <param name="DisplayName">The name the server shows: nickname, else global name, else username.</param>
/// <param name="JoinedAt">When Discord says they joined, for their current or last membership.</param>
/// <param name="LeftAt">When the bot saw them leave, or null while they are in the server.</param>
/// <param name="TimedOutUntil">When a timeout ends. Null, or in the past, when they are not timed out.</param>
/// <param name="IsPending">Still to pass membership screening.</param>
/// <param name="FirstSeenAt">When Modbot first saw them in the server.</param>
public sealed record DiscordMemberView(
    string UserId,
    string Username,
    string DisplayName,
    string? GlobalName,
    string? Nickname,
    string? AvatarUrl,
    bool IsBot,
    DateTimeOffset? JoinedAt,
    DateTimeOffset? LeftAt,
    IReadOnlyList<DiscordMemberRoleView> Roles,
    DateTimeOffset? TimedOutUntil,
    bool IsPending,
    DateTimeOffset? BoostingSince,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset UpdatedAt);

/// <param name="GuildId">The server in settings, or null when none is set.</param>
/// <param name="ListedAt">When the whole member list was first read. Null until then, and the list is partial.</param>
/// <param name="InServer">Members in the server now, whatever the filters.</param>
/// <param name="Now">The server's clock (spec 4.4), so ages are computed against it.</param>
public sealed record DiscordMemberListCoverage(
    string? GuildId,
    DateTimeOffset? ListedAt,
    int InServer,
    DateTimeOffset Now);

/// <param name="Total">Rows matching the filters, across every page.</param>
public sealed record DiscordMemberListResponse(
    IReadOnlyList<DiscordMemberView> Members,
    int Total,
    int Page,
    int PageSize,
    DiscordMemberListCoverage Coverage);

/// <summary>
/// The Discord server's members as the bot last saw them, current and past, with search.
/// </summary>
/// <remarks>
/// <para>
/// Read from <c>discord_member</c>, which the bot keeps: the whole list is read on every sign-in and
/// resume, and joins, leaves and changes keep it current between. Somebody who leaves keeps their
/// row, marked left. Not everybody in the VRChat group is in the Discord server and the other way
/// round, so this is its own list rather than a column on the group's.
/// </para>
/// <para>
/// Gated on <see cref="ModbotPermissions.ViewMembers"/>, like the group's member list.
/// </para>
/// </remarks>
public static class DiscordMemberEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapDiscordMembers(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord/members").WithTags("Discord").RequireAuthorization();

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? search,
                [FromQuery] string? state,
                [FromQuery] string? role,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (state is not (null or "" or "in-server" or "left" or "all"))
                    return Results.BadRequest(new { error = "`state` is in-server, left or all." });

                return Results.Ok(await ListAsync(db, clock, search, state, role, page, pageSize, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewMembers)
            .WithName("GetDiscordMembers")
            .WithSummary("The Discord server's members, current and past, with search")
            .WithDescription(
                "Members in the server by default; `state=left` shows people who left, `state=all` "
                + "both. `search` matches the display name, username, global name, nickname and the "
                + "id, case-insensitively. `role` is a Discord role id. Newest joiners first.\n\n"
                + "`coverage.listedAt` is null until the bot has read the whole member list once; the "
                + "list is partial until then.")
            .Produces<DiscordMemberListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id}", async (
                [FromRoute] string id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var guildId = await GuildIdAsync(db, ct);

                var row = await db.DiscordMembers.AsNoTracking()
                    .Where(m => m.UserId == id && (guildId == null || m.GuildId == guildId))
                    .FirstOrDefaultAsync(ct);

                if (row is null)
                    return Results.NotFound(new { error = "That person has not been seen in the Discord server." });

                var roles = await RoleNamesAsync(db, row.GuildId, ct);
                return Results.Ok(View(row, roles));
            })
            .RequiresFlag(ModbotPermissions.ViewMembers)
            .WithName("GetDiscordMember")
            .WithSummary("One member of the Discord server, current or past")
            .Produces<DiscordMemberView>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<DiscordMemberListResponse> ListAsync(
        ModbotContext db,
        IModbotClock clock,
        string? search,
        string? state,
        string? role,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(page ?? 1, 1);

        var guildId = await GuildIdAsync(db, ct);
        var server = guildId is null
            ? null
            : await db.DiscordServers.AsNoTracking().FirstOrDefaultAsync(s => s.GuildId == guildId, ct);

        var inGuild = db.DiscordMembers.AsNoTracking().Where(m => m.GuildId == guildId);

        var query = (state ?? string.Empty) switch
        {
            "left" => inGuild.Where(m => m.LeftAt != null),
            "all" => inGuild,
            _ => inGuild.Where(m => m.LeftAt == null),
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + Escape(search.Trim()) + "%";
            query = query.Where(m =>
                EF.Functions.ILike(m.DisplayName, pattern, "\\")
                || EF.Functions.ILike(m.Username, pattern, "\\")
                || (m.GlobalName != null && EF.Functions.ILike(m.GlobalName, pattern, "\\"))
                || (m.Nickname != null && EF.Functions.ILike(m.Nickname, pattern, "\\"))
                || m.UserId == search.Trim());
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            var holds = JsonSerializer.Serialize(new[] { role.Trim() });
            query = query.Where(m => EF.Functions.JsonContains(m.Roles, holds));
        }

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(m => m.JoinedAt ?? m.FirstSeenAt)
            .ThenBy(m => m.UserId)
            .Skip((number - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var roles = guildId is null ? new Dictionary<string, DiscordRole>() : await RoleNamesAsync(db, guildId, ct);

        return new DiscordMemberListResponse(
            rows.Select(r => View(r, roles)).ToList(),
            total,
            number,
            size,
            new DiscordMemberListCoverage(
                guildId,
                server?.MembersListedAt,
                await inGuild.CountAsync(m => m.LeftAt == null, ct),
                clock.UtcNow));
    }

    private static async Task<string?> GuildIdAsync(ModbotContext db, CancellationToken ct)
    {
        var guildId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(guildId) ? null : guildId.Trim();
    }

    private static async Task<Dictionary<string, DiscordRole>> RoleNamesAsync(ModbotContext db, string guildId, CancellationToken ct)
        => await db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId)
            .ToDictionaryAsync(r => r.RoleId, StringComparer.Ordinal, ct);

    private static DiscordMemberView View(DiscordMember row, IReadOnlyDictionary<string, DiscordRole> roles)
    {
        string[] ids;
        try
        {
            ids = JsonSerializer.Deserialize<string[]>(row.Roles) ?? [];
        }
        catch (JsonException)
        {
            ids = [];
        }

        return new DiscordMemberView(
            row.UserId,
            row.Username,
            row.DisplayName,
            row.GlobalName,
            row.Nickname,
            row.AvatarUrl,
            row.IsBot,
            row.JoinedAt,
            row.LeftAt,
            ids
                .Select(id => roles.TryGetValue(id, out var r)
                    ? new DiscordMemberRoleView(id, r.Name, r.Color)
                    : new DiscordMemberRoleView(id, null, 0))
                .OrderByDescending(r => roles.TryGetValue(r.Id, out var known) ? known.Position : -1)
                .ToList(),
            row.TimedOutUntil,
            row.IsPending,
            row.BoostingSince,
            row.FirstSeenAt,
            row.UpdatedAt);
    }

    /// <summary>A search typed with % or _ in it means those characters, not wildcards.</summary>
    private static string Escape(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
