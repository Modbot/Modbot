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
using Modbot.Core.Time;
using Modbot.VRChat.Sync;
using SettingsRow = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Features.Members;

/// <summary>
/// The group's member list and ban list, as last swept, with search.
/// </summary>
/// <remarks>
/// <para>
/// Read straight from <c>group_member</c> and <c>group_ban</c> joined to <c>vrchat_user</c>, so
/// the name and picture beside each row are whatever the profile sync has fetched so far -- null
/// until it has, and the screen shows the id. Search is a plain <c>ILIKE</c> on the display name
/// and on the id, server-side, because a 5,000-row list is not something to ship to a browser to
/// filter.
/// </para>
/// <para>
/// Every response carries how fresh the list is. Spec 4.2.3: real last-sync times, never an
/// implied guarantee. Before the first full sweep has finished the list is partial and says so.
/// </para>
/// <para>
/// The id for one person's membership travels as a query parameter rather than a path segment,
/// for the reason the profile endpoint gives: VRChat ids are opaque and a legacy one can contain
/// anything (spec 3.1.1), and a route constraint is exactly the format check the rule forbids.
/// Every parameter is explicitly attributed, because an unattributed concrete type on a GET is
/// bound as the body and throws while the route is mapped.
/// </para>
/// </remarks>
public static class MemberEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapMembers(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var members = app.MapGroup("/api/members").WithTags("Members").RequireAuthorization();

        members.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? search,
                [FromQuery] string? role,
                [FromQuery] string? status,
                [FromQuery] string? sort,
                [FromQuery] string? linked,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (!LinkFilter.IsValid(linked))
                    return Results.BadRequest(new { error = LinkFilter.Error });

                var seesLinks = LinkFilter.SeesLinks(http);
                if (LinkFilter.Narrows(linked) && !seesLinks)
                    return Results.Forbid();

                return Results.Ok(await ListMembersAsync(db, clock, search, role, status, sort, page, pageSize, ct, linked, seesLinks));
            })
            .RequiresFlag(ModbotPermissions.ViewMembers)
            .WithName("GetMembers")
            .WithSummary("The group's member list, as last swept, with search")
            .WithDescription(
                "Current members by default; `status=left` shows people a full sweep no longer "
                + "listed, `status=all` both. `search` matches the display name and the id, "
                + "case-insensitively. `role` is a role id. Sorted by join date, newest first, "
                + "unless `sort=name` or `sort=seen`. `linked=linked` shows only people with a linked "
                + "Discord account and `linked=not-linked` only people without; both need See profiles, "
                + "as does `linkedDiscord` on each row.\n\n"
                + "`coverage.firstSweepComplete` is false until the first full sweep has finished; "
                + "the list is partial until then. Names and pictures come from the profile sync "
                + "and are null for people it has not fetched yet.")
            .Produces<MemberListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        members.MapGet("/membership", async (
                [FromQuery] string id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "id is required." });

                return Results.Ok(await MembershipAsync(id, db, clock, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewMembers)
            .WithName("GetMembership")
            .WithSummary("One person's membership and ban standing")
            .WithDescription(
                "Whether they are a member now, with which roles and since when; whether the ban "
                + "list holds them; and how fresh both answers are. `known` is false when no "
                + "sweep has ever listed them as a member.")
            .Produces<MembershipView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/api/bans", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? search,
                [FromQuery] string? status,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
                Results.Ok(await ListBansAsync(db, clock, search, status, page, pageSize, ct)))
            .RequireAuthorization()
            .RequiresFlag(ModbotPermissions.ViewAuditLog)
            .WithTags("Members")
            .WithName("GetGroupBans")
            .WithSummary("The group's ban list, as last swept, with search")
            .WithDescription(
                "This is the group's ban list -- everyone VRChat says is banned right now, "
                + "whenever the ban was issued -- read by the ban sweep. Who banned them and why "
                + "is the audit log's to say, at /api/audit/bans. Bans that stand by default; "
                + "`status=lifted` shows bans a full sweep no longer listed, `status=all` both.")
            .Produces<GroupBanListResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    internal static async Task<MemberListResponse> ListMembersAsync(
        ModbotContext db,
        IModbotClock clock,
        string? search,
        string? role,
        string? status,
        string? sort,
        int? page,
        int? pageSize,
        CancellationToken ct,
        string? linked = null,
        bool seesLinks = false)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var groupId = settings?.ManagedGroupId ?? string.Empty;
        var roles = RolesOf(settings);
        var (pageNumber, size) = Paging(page, pageSize);

        var query =
            from m in db.GroupMembers.AsNoTracking()
            where m.GroupId == groupId
            join u in db.VRChatUsers.AsNoTracking() on m.UserId equals u.UserId into users
            from u in users.DefaultIfEmpty()
            select new { m, u };

        query = Trimmed(status)?.ToLowerInvariant() switch
        {
            "left" => query.Where(x => x.m.LeftAt != null),
            "all" => query,
            _ => query.Where(x => x.m.LeftAt == null),
        };

        if (Trimmed(search) is { } term)
        {
            var pattern = Pattern(term);
            query = query.Where(x =>
                EF.Functions.ILike(x.m.UserId, pattern, "\\")
                || (x.u != null && x.u.DisplayName != null && EF.Functions.ILike(x.u.DisplayName, pattern, "\\")));
        }

        if (Trimmed(role) is { } roleId)
        {
            var needle = JsonSerializer.Serialize(new[] { roleId });
            query = query.Where(x => EF.Functions.JsonContains(x.m.Roles, needle));
        }

        var links = db.ActiveAccountLinks();

        query = LinkFilter.Normalised(linked) switch
        {
            LinkFilter.Linked => query.Where(x => links.Any(l => l.VRChatUserId == x.m.UserId)),
            LinkFilter.NotLinked => query.Where(x => !links.Any(l => l.VRChatUserId == x.m.UserId)),
            _ => query,
        };

        query = Trimmed(sort)?.ToLowerInvariant() switch
        {
            "name" => query
                .OrderBy(x => x.u == null || x.u.DisplayName == null)
                .ThenBy(x => x.u!.DisplayName)
                .ThenBy(x => x.m.UserId),
            "seen" => query
                .OrderBy(x => x.u == null)
                .ThenByDescending(x => x.u!.LastSeenAt)
                .ThenBy(x => x.m.UserId),
            _ => query
                .OrderBy(x => x.m.JoinedAt == null)
                .ThenByDescending(x => x.m.JoinedAt)
                .ThenBy(x => x.m.UserId),
        };

        var total = await query.CountAsync(ct);

        var rows = await query
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var discord = seesLinks
            ? await LinkedDiscordAsync(db, settings?.DiscordGuildId, rows.Select(x => x.m.UserId).ToList(), ct)
            : new Dictionary<string, LinkedDiscordView>();

        var list = rows.Select(x =>
        {
            var ids = GroupMemberSync.RoleIds(x.m.Roles);

            return new MemberRow(
                x.m.UserId,
                x.u?.DisplayName,
                Picture(x.u),
                ids,
                ids.Select(id => roles.TryGetValue(id, out var name) ? name ?? id : id).ToList(),
                x.m.JoinedAt,
                x.m.MembershipStatus,
                x.m.Visibility,
                x.m.IsRepresenting,
                x.u?.Is18PlusVerified ?? false,
                x.u?.LastSeenAt,
                x.u?.LastRefreshedAt,
                x.m.LeftAt,
                discord.GetValueOrDefault(x.m.UserId));
        }).ToList();

        return new MemberListResponse(
            list,
            total,
            pageNumber,
            size,
            roles.Select(r => new RoleOption(r.Key, r.Value)).OrderBy(r => r.Name ?? r.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            MemberCoverage(settings, clock.UtcNow));
    }

    /// <summary>
    /// The linked Discord account of each of these VRChat users that has one, with how the stored
    /// Discord member list has them, in two queries.
    /// </summary>
    private static async Task<Dictionary<string, LinkedDiscordView>> LinkedDiscordAsync(
        ModbotContext db, string? guildId, IReadOnlyList<string> vrchatUserIds, CancellationToken ct)
    {
        if (vrchatUserIds.Count == 0)
            return [];

        var links = await db.ActiveAccountLinks()
            .Where(l => vrchatUserIds.Contains(l.VRChatUserId))
            .Select(l => new { l.VRChatUserId, l.DiscordUserId, l.DiscordUsername })
            .ToListAsync(ct);

        if (links.Count == 0)
            return [];

        guildId = string.IsNullOrWhiteSpace(guildId) ? null : guildId.Trim();
        var discordIds = links.Select(l => l.DiscordUserId).ToList();

        var listed = guildId is null
            ? []
            : await db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == guildId && discordIds.Contains(m.UserId))
                .ToDictionaryAsync(m => m.UserId, StringComparer.Ordinal, ct);

        return links.ToDictionary(
            l => l.VRChatUserId,
            l =>
            {
                var member = listed.GetValueOrDefault(l.DiscordUserId);
                return new LinkedDiscordView(
                    l.DiscordUserId,
                    member?.DisplayName ?? l.DiscordUsername,
                    member?.AvatarUrl,
                    member is { LeftAt: null },
                    member?.LeftAt);
            },
            StringComparer.Ordinal);
    }

    internal static async Task<MembershipView> MembershipAsync(
        string id,
        ModbotContext db,
        IModbotClock clock,
        CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var groupId = settings?.ManagedGroupId ?? string.Empty;
        var roles = RolesOf(settings);
        var now = clock.UtcNow;

        var member = await db.GroupMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == id, ct);

        var ban = await db.GroupBans.AsNoTracking()
            .FirstOrDefaultAsync(b => b.GroupId == groupId && b.UserId == id, ct);

        var ids = GroupMemberSync.RoleIds(member?.Roles);

        return new MembershipView(
            id,
            Known: member is not null,
            IsMember: member is { LeftAt: null },
            ids,
            ids.Select(r => roles.TryGetValue(r, out var name) ? name ?? r : r).ToList(),
            member?.JoinedAt,
            member?.MembershipStatus,
            member?.Visibility,
            member?.IsRepresenting ?? false,
            member?.ManagerNotes,
            member?.FirstSeenAt,
            member?.LastSeenAt,
            member?.LeftAt,
            Banned: ban is { LiftedAt: null },
            ban?.BannedAt,
            ban?.LiftedAt,
            MemberCoverage(settings, now),
            BanCoverage(settings, now));
    }

    internal static async Task<GroupBanListResponse> ListBansAsync(
        ModbotContext db,
        IModbotClock clock,
        string? search,
        string? status,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var groupId = settings?.ManagedGroupId ?? string.Empty;
        var (pageNumber, size) = Paging(page, pageSize);

        var query =
            from b in db.GroupBans.AsNoTracking()
            where b.GroupId == groupId
            join u in db.VRChatUsers.AsNoTracking() on b.UserId equals u.UserId into users
            from u in users.DefaultIfEmpty()
            select new { b, u };

        query = Trimmed(status)?.ToLowerInvariant() switch
        {
            "lifted" => query.Where(x => x.b.LiftedAt != null),
            "all" => query,
            _ => query.Where(x => x.b.LiftedAt == null),
        };

        if (Trimmed(search) is { } term)
        {
            var pattern = Pattern(term);
            query = query.Where(x =>
                EF.Functions.ILike(x.b.UserId, pattern, "\\")
                || (x.u != null && x.u.DisplayName != null && EF.Functions.ILike(x.u.DisplayName, pattern, "\\")));
        }

        query = query
            .OrderBy(x => x.b.BannedAt == null)
            .ThenByDescending(x => x.b.BannedAt)
            .ThenBy(x => x.b.UserId);

        var total = await query.CountAsync(ct);

        var rows = await query
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return new GroupBanListResponse(
            rows.Select(x => new BanRow(
                x.b.UserId,
                x.u?.DisplayName,
                Picture(x.u),
                x.b.BannedAt,
                x.b.FirstSeenAt,
                x.b.LiftedAt,
                x.u?.LastRefreshedAt)).ToList(),
            total,
            pageNumber,
            size,
            BanCoverage(settings, clock.UtcNow));
    }

    private static MemberListCoverage MemberCoverage(SettingsRow? settings, DateTimeOffset now) => new(
        settings?.MemberSweepCompletedAt is not null,
        settings?.MemberSweepCompletedAt,
        settings?.MemberSweepStartedAt is not null,
        settings?.MemberSweepCount ?? 0,
        now);

    private static BanListCoverage BanCoverage(SettingsRow? settings, DateTimeOffset now) => new(
        settings?.BanSweepCompletedAt is not null,
        settings?.BanSweepCompletedAt,
        settings?.BanSweepStartedAt is not null,
        settings?.BanSweepCount ?? 0,
        now);

    /// <summary>
    /// The group's roles by id, from the group-info producer's last snapshot. Empty until it has
    /// polled once, in which case roles are shown by id -- honest, and it fixes itself within five
    /// minutes of the group being configured.
    /// </summary>
    private static Dictionary<string, string?> RolesOf(SettingsRow? settings)
    {
        var snapshot = GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot);

        return (snapshot?.Roles ?? [])
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
    }

    /// <summary>The picture VRChat shows for the person: the override when set, else the avatar thumbnail.</summary>
    private static string? Picture(VRChatUser? user) =>
        string.IsNullOrWhiteSpace(user?.ProfilePictureUrl)
            ? Blank(user?.CurrentAvatarThumbnailImageUrl)
            : user.ProfilePictureUrl;

    private static (int Page, int Size) Paging(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    /// <summary>
    /// An ILIKE pattern that matches the term literally. The three characters ILIKE treats as
    /// special are escaped, because a moderator typing an underscore into the search box means an
    /// underscore -- and legacy VRChat ids contain anything at all (spec 3.1.1).
    /// </summary>
    internal static string Pattern(string term) =>
        "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal)
                  .Replace("%", "\\%", StringComparison.Ordinal)
                  .Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
