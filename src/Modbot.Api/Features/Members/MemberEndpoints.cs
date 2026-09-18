using System.Text.Json;
using Modbot.Core.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.DiscordLink;
using Modbot.Api.Lists;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Names;
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

    /// <summary>Newest joiner first. The default.</summary>
    public const string SortByJoined = "joined";

    public const string SortByName = "name";

    public const string SortBySeen = "seen";

    /// <summary>The group ban list has one ordering, and this is its name in a cursor.</summary>
    public const string BanSort = "banned";

    /// <summary>
    /// The value a member row is ordered on, as a cursor carries it.
    /// </summary>
    /// <remarks>
    /// Null means the row has no value to order on -- no join date, no fetched profile -- which is
    /// its own place at the end of the list, and is not the same as a value that happens to be
    /// empty. <see cref="ListCursor"/> keeps the two apart.
    /// </remarks>
    private static string? MarkValue(DateTimeOffset? joinedAt, VRChatUser? profile, string sort) => sort switch
    {
        SortByName => profile?.DisplayName,
        SortBySeen => profile is null ? null : ListCursor.Text(profile.LastSeenAt),
        _ => ListCursor.Text(joinedAt),
    };

    public static IEndpointRouteBuilder MapMembers(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var members = app.MapGroup("/api/members").WithTags("Members").RequireAuthorization();

        members.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? search,
                [FromQuery(Name = "role")] string[]? roles,
                [FromQuery(Name = "notRole")] string[]? notRoles,
                [FromQuery] bool? noRole,
                [FromQuery] string? status,
                [FromQuery] string? sort,
                [FromQuery] string? linked,
                [FromQuery] DateTimeOffset? joinedFrom,
                [FromQuery] DateTimeOffset? joinedTo,
                [FromQuery] bool? eighteenPlus,
                [FromQuery] bool? representing,
                [FromQuery] DateTimeOffset? seenFrom,
                [FromQuery] DateTimeOffset? seenTo,
                [FromQuery] string? profile,
                [FromQuery] string? cursor,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (!LinkFilter.IsValid(linked))
                    return Results.BadRequest(new { error = LinkFilter.Error });

                if (profile is not (null or "" or "fetched" or "not-fetched"))
                    return Results.BadRequest(new { error = "`profile` is fetched or not-fetched." });

                var seesLinks = LinkFilter.SeesLinks(http);
                if (LinkFilter.Narrows(linked) && !seesLinks)
                    return Results.Forbid();

                return Results.Ok(await ListMembersAsync(
                    db, clock, search, null, status, sort, page, pageSize, ct, linked, seesLinks, joinedFrom, joinedTo,
                    new MemberFilters(
                        Ids(roles), Ids(notRoles), noRole, eighteenPlus, representing, seenFrom, seenTo, Trimmed(profile)),
                    cursor));
            })
            .RequiresFlag(ModbotPermissions.ViewMembers)
            .WithName("GetMembers")
            .WithSummary("The group's member list, as last swept, with search")
            .WithDescription(
                "Current members by default; `status=left` shows people a full sweep no longer "
                + "listed, `status=all` both. `search` matches the display name and the id, "
                + "case-insensitively. `role` is a role id and may be repeated: people holding any "
                + "of them. `notRole`, also repeatable, leaves out people holding any of those; "
                + "`noRole=true` keeps only people with no role. Sorted by join date, newest first, "
                + "unless `sort=name` or `sort=seen`. `linked=linked` shows only people with a linked "
                + "Discord account and `linked=not-linked` only people without; both need See profiles, "
                + "as does `linkedDiscord` on each row. `joinedFrom` and `joinedTo` narrow the list "
                + "to people who joined inside that stretch, which is what an unusual-activity alert "
                + "links to; `seenFrom` and `seenTo` do the same for when Modbot last saw them. "
                + "`eighteenPlus` and `representing` are true or false; `profile` is `fetched` or "
                + "`not-fetched`.\n\n"
                + "`roles` lists the group's roles with how many current members hold each.\n\n"
                + "Paged by cursor. Read the first page with no `cursor`, then send back the "
                + "`next` or `previous` the answer carries. A cursor is the server's to write: "
                + "send it back exactly as it came, and do not build one. One that will not read, "
                + "or that was written while the list was sorted another way, is ignored and the "
                + "first page comes back instead of an error. `page` still works and still counts "
                + "rows to skip, but a list this long is swept while you read it, so a numbered "
                + "page can show you a row twice or never; `cursor` cannot, and wins when both "
                + "are sent. `total` is the whole filtered list either way.\n\n"
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
                [FromQuery] string? cursor,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
                Results.Ok(await ListBansAsync(db, clock, search, status, page, pageSize, ct, cursor)))
            .RequireAuthorization()
            .RequiresFlag(ModbotPermissions.ViewAuditLog)
            .WithTags("Members")
            .WithName("GetGroupBans")
            .WithSummary("The group's ban list, as last swept, with search")
            .WithDescription(
                "This is the group's ban list -- everyone VRChat says is banned right now, "
                + "whenever the ban was issued -- read by the ban sweep. Who banned them and why "
                + "is the audit log's to say, at /api/audit/bans. Bans that stand by default; "
                + "`status=lifted` shows bans a full sweep no longer listed, `status=all` both.\n\n"
                + "Paged by cursor: send back the `next` or `previous` the answer carries. `page` "
                + "still works but can repeat or miss a row when a sweep lands mid-read.")
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
        bool seesLinks = false,
        DateTimeOffset? joinedFrom = null,
        DateTimeOffset? joinedTo = null,
        MemberFilters? more = null,
        string? cursor = null)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var groupId = settings?.ManagedGroupId ?? string.Empty;
        var roles = RolesOf(settings);
        var (pageNumber, size) = Paging(page, pageSize);
        more ??= new MemberFilters();

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
            query = NameSearch.SearchablePattern(term) is { } plain
                ? query.Where(x =>
                    EF.Functions.ILike(x.m.UserId, pattern, "\\")
                    || (x.u != null && x.u.DisplayName != null && EF.Functions.ILike(x.u.DisplayName, pattern, "\\"))
                    || (x.u != null && x.u.DisplayNameSearchable != null && EF.Functions.ILike(x.u.DisplayNameSearchable, plain, "\\")))
                : query.Where(x =>
                    EF.Functions.ILike(x.m.UserId, pattern, "\\")
                    || (x.u != null && x.u.DisplayName != null && EF.Functions.ILike(x.u.DisplayName, pattern, "\\")));
        }

        // What an unusual-activity alert links to: the people who joined in the hour it is about.
        if (joinedFrom is { } from)
            query = query.Where(x => x.m.JoinedAt >= from);

        if (joinedTo is { } to)
            query = query.Where(x => x.m.JoinedAt < to);

        var anyRoles = (more.AnyRoles ?? []).Concat(Trimmed(role) is { } roleId ? [roleId] : []).ToList();

        // PostgreSQL's `?|`: whether any of these strings is an element of the roles array. One
        // operator for "any of" and its negation for "none of", both served by the GIN index.
        if (anyRoles.Count > 0)
        {
            var wanted = anyRoles.ToArray();
            query = query.Where(x => EF.Functions.JsonExistAny(x.m.Roles, wanted));
        }

        if (more.NoneOfRoles is { Count: > 0 } excluded)
        {
            var unwanted = excluded.ToArray();
            query = query.Where(x => !EF.Functions.JsonExistAny(x.m.Roles, unwanted));
        }

        if (more.NoRole is { } noRole)
            query = noRole ? query.Where(x => x.m.Roles == "[]") : query.Where(x => x.m.Roles != "[]");

        if (more.EighteenPlus is { } eighteenPlus)
            query = query.Where(x => (x.u != null && x.u.Is18PlusVerified) == eighteenPlus);

        if (more.Representing is { } representing)
            query = query.Where(x => x.m.IsRepresenting == representing);

        if (more.SeenFrom is { } seenFrom)
            query = query.Where(x => x.u != null && x.u.LastSeenAt >= seenFrom);

        if (more.SeenTo is { } seenTo)
            query = query.Where(x => x.u != null && x.u.LastSeenAt < seenTo);

        query = more.Profile switch
        {
            "fetched" => query.Where(x => x.u != null && x.u.LastRefreshedAt != null),
            "not-fetched" => query.Where(x => x.u == null || x.u.LastRefreshedAt == null),
            _ => query,
        };

        var links = db.ActiveAccountLinks();

        query = LinkFilter.Normalised(linked) switch
        {
            LinkFilter.Linked => query.Where(x => links.Any(l => l.VRChatUserId == x.m.UserId)),
            LinkFilter.NotLinked => query.Where(x => !links.Any(l => l.VRChatUserId == x.m.UserId)),
            _ => query,
        };

        // The list is counted as well as paged. A group is thousands of rows behind an index, and
        // the total is the figure the filter bar reads out, so it stays. The fact log, which is
        // orders of magnitude larger and partitioned, is the list that refuses to count.
        var total = await query.CountAsync(ct);

        var sortName = Trimmed(sort)?.ToLowerInvariant() switch
        {
            SortByName => SortByName,
            SortBySeen => SortBySeen,
            _ => SortByJoined,
        };

        // A cursor that will not read, or that was written under a different ordering, is no
        // cursor at all: the reader gets the first page of what they asked for rather than an
        // error page from a stale bookmark.
        var at = ListCursor.Read(cursor, sortName);
        var back = at?.Direction == ListDirection.Back;

        // Which rows come after the cursor's row -- or before it, reading back. Both halves of the
        // boundary are compared every time: a sweep stamps a whole batch of members with one
        // moment, so a cursor on the time alone would skip every one of them but the first.
        if (at is { } mark)
        {
            var id = mark.Id;
            var onwards = mark.Direction == ListDirection.Next;

            if (sortName == SortByName)
            {
                // Named people first, in name order; then the ones whose profile has not been
                // fetched, in id order. A boundary row in the second half has no name to compare.
                if (mark.Value is { } name)
                {
                    query = onwards
                        ? query.Where(x =>
                            x.u == null || x.u.DisplayName == null
                            || string.Compare(x.u.DisplayName, name) > 0
                            || (x.u.DisplayName == name && string.Compare(x.m.UserId, id) > 0))
                        : query.Where(x =>
                            x.u != null && x.u.DisplayName != null
                            && (string.Compare(x.u.DisplayName, name) < 0
                                || (x.u.DisplayName == name && string.Compare(x.m.UserId, id) < 0)));
                }
                else
                {
                    query = onwards
                        ? query.Where(x =>
                            (x.u == null || x.u.DisplayName == null)
                            && string.Compare(x.m.UserId, id) > 0)
                        : query.Where(x =>
                            (x.u != null && x.u.DisplayName != null)
                            || string.Compare(x.m.UserId, id) < 0);
                }
            }
            else if (sortName == SortBySeen)
            {
                // Newest sighting first; people with no stored profile at the end.
                if (ListCursor.Time(mark.Value) is { } seen)
                {
                    query = onwards
                        ? query.Where(x =>
                            x.u == null
                            || x.u.LastSeenAt < seen
                            || (x.u.LastSeenAt == seen && string.Compare(x.m.UserId, id) > 0))
                        : query.Where(x =>
                            x.u != null
                            && (x.u.LastSeenAt > seen
                                || (x.u.LastSeenAt == seen && string.Compare(x.m.UserId, id) < 0)));
                }
                else
                {
                    query = onwards
                        ? query.Where(x => x.u == null && string.Compare(x.m.UserId, id) > 0)
                        : query.Where(x => x.u != null || string.Compare(x.m.UserId, id) < 0);
                }
            }
            else
            {
                // Newest joiner first; people VRChat gave no join date for at the end.
                if (ListCursor.Time(mark.Value) is { } joined)
                {
                    query = onwards
                        ? query.Where(x =>
                            x.m.JoinedAt == null
                            || x.m.JoinedAt < joined
                            || (x.m.JoinedAt == joined && string.Compare(x.m.UserId, id) > 0))
                        : query.Where(x =>
                            x.m.JoinedAt != null
                            && (x.m.JoinedAt > joined
                                || (x.m.JoinedAt == joined && string.Compare(x.m.UserId, id) < 0)));
                }
                else
                {
                    query = onwards
                        ? query.Where(x => x.m.JoinedAt == null && string.Compare(x.m.UserId, id) > 0)
                        : query.Where(x => x.m.JoinedAt != null || string.Compare(x.m.UserId, id) < 0);
                }
            }
        }

        query = sortName switch
        {
            SortByName when back => query
                .OrderByDescending(x => x.u == null || x.u.DisplayName == null)
                .ThenByDescending(x => x.u!.DisplayName)
                .ThenByDescending(x => x.m.UserId),
            SortByName => query
                .OrderBy(x => x.u == null || x.u.DisplayName == null)
                .ThenBy(x => x.u!.DisplayName)
                .ThenBy(x => x.m.UserId),
            SortBySeen when back => query
                .OrderByDescending(x => x.u == null)
                .ThenBy(x => x.u!.LastSeenAt)
                .ThenByDescending(x => x.m.UserId),
            SortBySeen => query
                .OrderBy(x => x.u == null)
                .ThenByDescending(x => x.u!.LastSeenAt)
                .ThenBy(x => x.m.UserId),
            _ when back => query
                .OrderByDescending(x => x.m.JoinedAt == null)
                .ThenBy(x => x.m.JoinedAt)
                .ThenByDescending(x => x.m.UserId),
            _ => query
                .OrderBy(x => x.m.JoinedAt == null)
                .ThenByDescending(x => x.m.JoinedAt)
                .ThenBy(x => x.m.UserId),
        };

        // `page` still works for whoever was already calling this with one, and the answer now
        // carries a cursor they can move to. A cursor, when sent, wins.
        if (at is null && pageNumber > 1)
            query = query.Skip((pageNumber - 1) * size);

        var read = await ListPaging.ReadAsync(
            query,
            sortName,
            size,
            at,
            x => (MarkValue(x.m.JoinedAt, x.u, sortName), x.m.UserId),
            ct);

        var rows = read.Rows;

        var discord = seesLinks
            ? await LinkedDiscordAsync(db, settings?.DiscordGuildId, rows.Select(x => x.m.UserId).ToList(), ct)
            : new Dictionary<string, LinkedDiscordView>();

        var list = rows.Select(x =>
        {
            var ids = GroupMemberSync.RoleIds(x.m.Roles);

            return new MemberRow(
                x.m.UserId,
                x.u?.DisplayName,
                PlainName.Of(x.u?.DisplayName),
                Picture(x.u),
                ids,
                ids.Select(id => roles.TryGetValue(id, out var name) ? name ?? id : id).ToList(),
                x.m.JoinedAt,
                x.m.MembershipStatus,
                x.m.Visibility,
                x.m.IsRepresenting,
                x.u?.Is18PlusVerified ?? false,
                x.u?.TrustRank,
                x.u?.LastSeenAt,
                x.u?.LastRefreshedAt,
                x.m.LeftAt,
                discord.GetValueOrDefault(x.m.UserId));
        }).ToList();

        var held = await RoleCountsAsync(db, groupId, ct);

        return new MemberListResponse(
            list,
            total,
            at is null ? pageNumber : 1,
            size,
            read.Next,
            read.Previous,
            roles
                .Select(r => new RoleOption(r.Key, r.Value, held.GetValueOrDefault(r.Key)))
                .OrderBy(r => r.Name ?? r.Id, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MemberCoverage(settings, clock.UtcNow));
    }

    /// <summary>
    /// How many current members hold each role.
    /// </summary>
    /// <remarks>
    /// Counted here from the roles column rather than in SQL: the column is a short JSON array
    /// per member, a group is thousands of rows at most, and the count is read once per page of
    /// the list. The number is what lets a filter say what it will show before it is applied.
    /// </remarks>
    private static async Task<Dictionary<string, int>> RoleCountsAsync(ModbotContext db, string groupId, CancellationToken ct)
    {
        var rows = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && m.LeftAt == null && m.Roles != "[]")
            .Select(m => m.Roles)
            .ToListAsync(ct);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var json in rows)
        {
            foreach (var id in GroupMemberSync.RoleIds(json))
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
        CancellationToken ct,
        string? cursor = null)
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
            query = NameSearch.SearchablePattern(term) is { } plain
                ? query.Where(x =>
                    EF.Functions.ILike(x.b.UserId, pattern, "\\")
                    || (x.u != null && x.u.DisplayName != null && EF.Functions.ILike(x.u.DisplayName, pattern, "\\"))
                    || (x.u != null && x.u.DisplayNameSearchable != null && EF.Functions.ILike(x.u.DisplayNameSearchable, plain, "\\")))
                : query.Where(x =>
                    EF.Functions.ILike(x.b.UserId, pattern, "\\")
                    || (x.u != null && x.u.DisplayName != null && EF.Functions.ILike(x.u.DisplayName, pattern, "\\")));
        }

        var total = await query.CountAsync(ct);

        // One ordering, so the cursor's sort is the list's own name. Newest ban first, then the
        // bans VRChat gave no date for, and the user id breaks ties -- a sweep that finds fifty
        // bans stamps them all with the same moment, so without it a page boundary in that batch
        // would lose the other forty-nine.
        var at = ListCursor.Read(cursor, BanSort);
        var back = at?.Direction == ListDirection.Back;

        if (at is { } mark)
        {
            var id = mark.Id;
            var onwards = mark.Direction == ListDirection.Next;

            if (ListCursor.Time(mark.Value) is { } banned)
            {
                query = onwards
                    ? query.Where(x =>
                        x.b.BannedAt == null
                        || x.b.BannedAt < banned
                        || (x.b.BannedAt == banned && string.Compare(x.b.UserId, id) > 0))
                    : query.Where(x =>
                        x.b.BannedAt != null
                        && (x.b.BannedAt > banned
                            || (x.b.BannedAt == banned && string.Compare(x.b.UserId, id) < 0)));
            }
            else
            {
                query = onwards
                    ? query.Where(x => x.b.BannedAt == null && string.Compare(x.b.UserId, id) > 0)
                    : query.Where(x => x.b.BannedAt != null || string.Compare(x.b.UserId, id) < 0);
            }
        }

        query = back
            ? query
                .OrderByDescending(x => x.b.BannedAt == null)
                .ThenBy(x => x.b.BannedAt)
                .ThenByDescending(x => x.b.UserId)
            : query
                .OrderBy(x => x.b.BannedAt == null)
                .ThenByDescending(x => x.b.BannedAt)
                .ThenBy(x => x.b.UserId);

        if (at is null && pageNumber > 1)
            query = query.Skip((pageNumber - 1) * size);

        var read = await ListPaging.ReadAsync(
            query, BanSort, size, at, x => (ListCursor.Text(x.b.BannedAt), x.b.UserId), ct);

        return new GroupBanListResponse(
            read.Rows.Select(x => new BanRow(
                x.b.UserId,
                x.u?.DisplayName,
                PlainName.Of(x.u?.DisplayName),
                Picture(x.u),
                x.u?.TrustRank,
                x.b.BannedAt,
                x.b.FirstSeenAt,
                x.b.LiftedAt,
                x.u?.LastRefreshedAt)).ToList(),
            total,
            at is null ? pageNumber : 1,
            size,
            read.Next,
            read.Previous,
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

    /// <summary>The picture Modbot shows for the person: one rule, in <see cref="ProfilePictures"/>.</summary>
    private static string? Picture(VRChatUser? user) => ProfilePictures.Best(user);

    private static (int Page, int Size) Paging(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    /// <summary>
    /// An ILIKE pattern that matches the term literally. The three characters ILIKE treats as
    /// special are escaped, because a moderator typing an underscore into the search box means an
    /// underscore -- and legacy VRChat ids contain anything at all (spec 3.1.1).
    /// </summary>
    internal static string Pattern(string term) => NameSearch.Pattern(term);

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
