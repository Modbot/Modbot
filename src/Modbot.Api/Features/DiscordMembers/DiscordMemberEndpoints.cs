using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.DiscordLink;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Names;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordMembers;

/// <summary>A role a Discord member holds, with its name as last read.</summary>
/// <param name="Name">Null when the role is not in the stored role list.</param>
/// <param name="Color">0xRRGGBB, zero for none.</param>
public sealed record DiscordMemberRoleView(string Id, string? Name, int Color);

/// <summary>The VRChat account a Discord member is linked to.</summary>
/// <param name="DisplayName">The VRChat name Modbot has stored now, else the one saved with the link.</param>
/// <param name="AvatarUrl">The VRChat picture, when the profile sync has fetched one.</param>
public sealed record LinkedVRChatView(string UserId, string? DisplayName, string? AvatarUrl);

/// <summary>One member of the Discord server, current or past.</summary>
/// <param name="DisplayName">The name the server shows: nickname, else global name, else username.</param>
/// <param name="PlainName">The display name in plain letters, when that differs from the display name. Null otherwise.</param>
/// <param name="JoinedAt">When Discord says they joined, for their current or last membership.</param>
/// <param name="LeftAt">When the bot saw them leave, or null while they are in the server.</param>
/// <param name="TimedOutUntil">When a timeout ends. Null, or in the past, when they are not timed out.</param>
/// <param name="IsPending">Still to pass membership screening.</param>
/// <param name="FirstSeenAt">When Modbot first saw them in the server.</param>
/// <param name="LinkedVRChat">
/// Their linked VRChat account. Null when they have not linked, and always null for a caller
/// without See profiles, who may not see links (Discord account linking design §11).
/// </param>
public sealed record DiscordMemberView(
    string UserId,
    string Username,
    string DisplayName,
    string? PlainName,
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
    DateTimeOffset UpdatedAt,
    LinkedVRChatView? LinkedVRChat);

/// <summary>One of the server's roles, offered as a filter, with how many current members hold it.</summary>
public sealed record DiscordRoleOption(string Id, string? Name, int Color, int Members);

/// <summary>
/// The Discord member list's filters beyond search, state and the link. Every one is optional
/// and they combine with AND.
/// </summary>
/// <param name="AnyRoles">People holding at least one of these roles.</param>
/// <param name="NoneOfRoles">People holding none of these roles.</param>
/// <param name="NoRole">True: people with no role at all; false: people with at least one.</param>
/// <param name="TimedOut">Timed out right now, or not.</param>
/// <param name="Boosting">Boosting the server, or not.</param>
/// <param name="Sort"><c>joined</c> (newest first, the default), <c>oldest</c>, or <c>name</c>.</param>
public sealed record DiscordMemberFilters(
    IReadOnlyList<string>? AnyRoles = null,
    IReadOnlyList<string>? NoneOfRoles = null,
    bool? NoRole = null,
    bool? Bot = null,
    bool? Pending = null,
    bool? TimedOut = null,
    bool? Boosting = null,
    DateTimeOffset? JoinedFrom = null,
    DateTimeOffset? JoinedTo = null,
    string? Sort = null);

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
/// <param name="Roles">
/// The server's roles to filter by, highest first, without @everyone or roles since deleted. Here
/// because the Discord role list for settings needs Change settings, and this list does not.
/// </param>
public sealed record DiscordMemberListResponse(
    IReadOnlyList<DiscordMemberView> Members,
    int Total,
    int Page,
    int PageSize,
    DiscordMemberListCoverage Coverage,
    IReadOnlyList<DiscordRoleOption> Roles);

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
/// Gated on <see cref="ModbotPermissions.ViewMembers"/>, like the group's member list. The linked
/// VRChat account and the linked filter also need <see cref="ModbotPermissions.ViewProfile"/>,
/// because seeing a link does (Discord account linking design §11). Links are read through
/// <see cref="AccountLinkLookup"/> so "linked" means an active link here as everywhere else.
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
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? search,
                [FromQuery] string? state,
                [FromQuery(Name = "role")] string[]? roles,
                [FromQuery(Name = "notRole")] string[]? notRoles,
                [FromQuery] bool? noRole,
                [FromQuery] string? linked,
                [FromQuery] bool? bot,
                [FromQuery] bool? pending,
                [FromQuery] bool? timedOut,
                [FromQuery] bool? boosting,
                [FromQuery] DateTimeOffset? joinedFrom,
                [FromQuery] DateTimeOffset? joinedTo,
                [FromQuery] string? sort,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (state is not (null or "" or "in-server" or "left" or "all"))
                    return Results.BadRequest(new { error = "`state` is in-server, left or all." });

                if (sort is not (null or "" or "joined" or "oldest" or "name"))
                    return Results.BadRequest(new { error = "`sort` is joined, oldest or name." });

                if (!LinkFilter.IsValid(linked))
                    return Results.BadRequest(new { error = LinkFilter.Error });

                var seesLinks = LinkFilter.SeesLinks(http);
                if (LinkFilter.Narrows(linked) && !seesLinks)
                    return Results.Forbid();

                return Results.Ok(await ListAsync(
                    db, clock, search, state, null, linked, seesLinks, page, pageSize, ct,
                    new DiscordMemberFilters(
                        Ids(roles), Ids(notRoles), noRole, bot, pending, timedOut, boosting, joinedFrom, joinedTo, sort)));
            })
            .RequiresFlag(ModbotPermissions.ViewMembers)
            .WithName("GetDiscordMembers")
            .WithSummary("The Discord server's members, current and past, with search")
            .WithDescription(
                "Members in the server by default; `state=left` shows people who left, `state=all` "
                + "both. `search` matches the display name, username, global name, nickname and the "
                + "id, case-insensitively. `role` is a Discord role id and may be repeated: people "
                + "holding any of them. `notRole`, also repeatable, leaves out people holding any of "
                + "those; `noRole=true` keeps only people with no role. `bot`, `pending`, `timedOut` "
                + "and `boosting` are true or false. `joinedFrom` and `joinedTo` narrow to people who "
                + "joined inside that stretch. `linked=linked` shows only people "
                + "with a linked VRChat account and `linked=not-linked` only people without; both need "
                + "See profiles, as does `linkedVRChat` on each member. Newest joiners first unless "
                + "`sort=oldest` or `sort=name`. `roles` lists the server's roles with how many "
                + "current members hold each.\n\n"
                + "`coverage.listedAt` is null until the bot has read the whole member list once; the "
                + "list is partial until then.")
            .Produces<DiscordMemberListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id}", async (
                HttpContext http,
                [FromRoute] string id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var member = await OneAsync(db, id, LinkFilter.SeesLinks(http), ct);

                return member is null
                    ? Results.NotFound(new { error = "That person has not been seen in the Discord server." })
                    : Results.Ok(member);
            })
            .RequiresFlag(ModbotPermissions.ViewMembers)
            .WithName("GetDiscordMember")
            .WithSummary("One member of the Discord server, current or past")
            .Produces<DiscordMemberView>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapDiscordMemberActivity();

        return app;
    }

    /// <summary>One member of the server, current or past, or null when never seen.</summary>
    /// <param name="seesLinks">
    /// Whether the caller may be told which VRChat account this one is linked to, which needs
    /// <see cref="ModbotPermissions.ViewProfile"/>.
    /// </param>
    internal static async Task<DiscordMemberView?> OneAsync(
        ModbotContext db, string id, bool seesLinks, CancellationToken ct)
    {
        var guildId = await GuildIdAsync(db, ct);

        var row = await db.DiscordMembers.AsNoTracking()
            .Where(m => m.UserId == id && (guildId == null || m.GuildId == guildId))
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return null;

        var roles = await RoleNamesAsync(db, row.GuildId, ct);
        var links = seesLinks
            ? await LinkedVRChatAsync(db, [row.UserId], ct)
            : new Dictionary<string, LinkedVRChatView>();

        return View(row, roles, links);
    }

    internal static async Task<DiscordMemberListResponse> ListAsync(
        ModbotContext db,
        IModbotClock clock,
        string? search,
        string? state,
        string? role,
        string? linked,
        bool seesLinks,
        int? page,
        int? pageSize,
        CancellationToken ct,
        DiscordMemberFilters? more = null)
    {
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(page ?? 1, 1);
        var now = clock.UtcNow;
        more ??= new DiscordMemberFilters();

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
            var term = search.Trim();
            var pattern = NameSearch.Pattern(term);

            // The names as stored, and the same names in their searchable form, so "alex" finds
            // 𝕬𝖑𝖊𝖝 and "adderall" finds Addеrаll.
            query = NameSearch.SearchablePattern(term) is { } plain
                ? query.Where(m =>
                    EF.Functions.ILike(m.DisplayName, pattern, "\\")
                    || EF.Functions.ILike(m.Username, pattern, "\\")
                    || (m.GlobalName != null && EF.Functions.ILike(m.GlobalName, pattern, "\\"))
                    || (m.Nickname != null && EF.Functions.ILike(m.Nickname, pattern, "\\"))
                    || (m.DisplayNameSearchable != null && EF.Functions.ILike(m.DisplayNameSearchable, plain, "\\"))
                    || (m.UsernameSearchable != null && EF.Functions.ILike(m.UsernameSearchable, plain, "\\"))
                    || (m.GlobalNameSearchable != null && EF.Functions.ILike(m.GlobalNameSearchable, plain, "\\"))
                    || (m.NicknameSearchable != null && EF.Functions.ILike(m.NicknameSearchable, plain, "\\"))
                    || m.UserId == term)
                : query.Where(m =>
                    EF.Functions.ILike(m.DisplayName, pattern, "\\")
                    || EF.Functions.ILike(m.Username, pattern, "\\")
                    || (m.GlobalName != null && EF.Functions.ILike(m.GlobalName, pattern, "\\"))
                    || (m.Nickname != null && EF.Functions.ILike(m.Nickname, pattern, "\\"))
                    || m.UserId == term);
        }

        // PostgreSQL's `?|`: whether any of these strings is an element of the roles array. One
        // operator for "any of" and its negation for "none of".
        var anyRoles = (more.AnyRoles ?? []).Concat(string.IsNullOrWhiteSpace(role) ? [] : [role.Trim()]).ToArray();
        if (anyRoles.Length > 0)
            query = query.Where(m => EF.Functions.JsonExistAny(m.Roles, anyRoles));

        if (more.NoneOfRoles is { Count: > 0 } excluded)
        {
            var unwanted = excluded.ToArray();
            query = query.Where(m => !EF.Functions.JsonExistAny(m.Roles, unwanted));
        }

        if (more.NoRole is { } noRole)
            query = noRole ? query.Where(m => m.Roles == "[]") : query.Where(m => m.Roles != "[]");

        if (more.Bot is { } bot)
            query = query.Where(m => m.IsBot == bot);

        if (more.Pending is { } isPending)
            query = query.Where(m => m.IsPending == isPending);

        if (more.TimedOut is { } timedOut)
        {
            query = timedOut
                ? query.Where(m => m.TimedOutUntil != null && m.TimedOutUntil > now)
                : query.Where(m => m.TimedOutUntil == null || m.TimedOutUntil <= now);
        }

        if (more.Boosting is { } boosting)
            query = query.Where(m => (m.BoostingSince != null) == boosting);

        if (more.JoinedFrom is { } joinedFrom)
            query = query.Where(m => m.JoinedAt >= joinedFrom);

        if (more.JoinedTo is { } joinedTo)
            query = query.Where(m => m.JoinedAt < joinedTo);

        var links = db.ActiveAccountLinks();

        query = LinkFilter.Normalised(linked) switch
        {
            LinkFilter.Linked => query.Where(m => links.Any(l => l.DiscordUserId == m.UserId)),
            LinkFilter.NotLinked => query.Where(m => !links.Any(l => l.DiscordUserId == m.UserId)),
            _ => query,
        };

        var total = await query.CountAsync(ct);

        var ordered = more.Sort switch
        {
            "name" => query.OrderBy(m => m.DisplayName).ThenBy(m => m.UserId),
            "oldest" => query.OrderBy(m => m.JoinedAt ?? m.FirstSeenAt).ThenBy(m => m.UserId),
            _ => query.OrderByDescending(m => m.JoinedAt ?? m.FirstSeenAt).ThenBy(m => m.UserId),
        };

        var rows = await ordered
            .Skip((number - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var roles = guildId is null ? new Dictionary<string, DiscordRole>() : await RoleNamesAsync(db, guildId, ct);
        var linkedTo = seesLinks
            ? await LinkedVRChatAsync(db, rows.Select(r => r.UserId).ToList(), ct)
            : new Dictionary<string, LinkedVRChatView>();

        var held = await RoleCountsAsync(inGuild, ct);

        return new DiscordMemberListResponse(
            rows.Select(r => View(r, roles, linkedTo)).ToList(),
            total,
            number,
            size,
            new DiscordMemberListCoverage(
                guildId,
                server?.MembersListedAt,
                await inGuild.CountAsync(m => m.LeftAt == null, ct),
                now),
            roles.Values
                .Where(r => !r.Everyone && r.RemovedAt == null)
                .OrderByDescending(r => r.Position)
                .Select(r => new DiscordRoleOption(r.RoleId, r.Name, r.Color, held.GetValueOrDefault(r.RoleId)))
                .ToList());
    }

    /// <summary>
    /// How many people in the server hold each role, counted from the roles column: a short JSON
    /// array per member, read once per page so a filter can say what it will show.
    /// </summary>
    private static async Task<Dictionary<string, int>> RoleCountsAsync(IQueryable<DiscordMember> inGuild, CancellationToken ct)
    {
        var rows = await inGuild
            .Where(m => m.LeftAt == null && m.Roles != "[]")
            .Select(m => m.Roles)
            .ToListAsync(ct);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var json in rows)
        {
            string[] ids;
            try
            {
                ids = JsonSerializer.Deserialize<string[]>(json) ?? [];
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var id in ids)
                counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        return counts;
    }

    /// <summary>Repeated query values, trimmed, with blanks and repeats dropped. Null when none.</summary>
    private static IReadOnlyList<string>? Ids(string[]? values)
    {
        var ids = (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return ids.Count == 0 ? null : ids;
    }

    /// <summary>The linked VRChat account of each of these Discord users that has one, in one query.</summary>
    private static async Task<Dictionary<string, LinkedVRChatView>> LinkedVRChatAsync(
        ModbotContext db, IReadOnlyList<string> discordUserIds, CancellationToken ct)
    {
        if (discordUserIds.Count == 0)
            return [];

        var rows = await (
                from l in db.ActiveAccountLinks()
                where discordUserIds.Contains(l.DiscordUserId)
                join u in db.VRChatUsers.AsNoTracking() on l.VRChatUserId equals u.UserId into users
                from u in users.DefaultIfEmpty()
                select new
                {
                    l.DiscordUserId,
                    l.VRChatUserId,
                    Name = u != null && u.DisplayName != null ? u.DisplayName : l.VRChatDisplayName,
                    Override = u != null ? u.ProfilePictureUrl : null,
                    Thumbnail = u != null ? u.CurrentAvatarThumbnailImageUrl : null,
                })
            .ToListAsync(ct);

        // At most one active link per Discord account, which a partial unique index enforces.
        return rows.ToDictionary(
            r => r.DiscordUserId,
            r => new LinkedVRChatView(
                r.VRChatUserId,
                r.Name,
                string.IsNullOrWhiteSpace(r.Override) ? (string.IsNullOrWhiteSpace(r.Thumbnail) ? null : r.Thumbnail) : r.Override),
            StringComparer.Ordinal);
    }

    internal static async Task<string?> GuildIdAsync(ModbotContext db, CancellationToken ct)
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

    private static DiscordMemberView View(
        DiscordMember row,
        IReadOnlyDictionary<string, DiscordRole> roles,
        IReadOnlyDictionary<string, LinkedVRChatView> links)
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
            PlainName.Of(row.DisplayName),
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
            row.UpdatedAt,
            links.GetValueOrDefault(row.UserId));
    }
}
