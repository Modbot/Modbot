using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.DiscordLink;
using Modbot.Api.Features.Members;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Names;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.People;

/// <summary>
/// Everyone Modbot has a record of, member or not.
/// </summary>
/// <remarks>
/// <para>
/// The Members page lists the group's roster. This lists <c>vrchat_user</c>, which is a row for
/// every id Modbot has ever seen anywhere -- somebody who walked through one instance, an account
/// named once in the audit log, a moderator from another group. Those people have a profile, a
/// first sighting, a last sighting and a history, and until this page there was no way to reach
/// any of it except by already knowing the id.
/// </para>
/// <para>
/// Gated on See profiles, and on nothing new. Every field on a row is a field the person popup
/// already shows to the same permission; what the page adds is the list of who there is, which is
/// the one thing a moderator could not get at before.
/// </para>
/// <para>
/// Since 2026-09-27 this is the member list too: the Members page became People narrowed to
/// members, with the member list's columns and filters. So See members opens it as well, for the
/// two views the member list used to show -- members and people who left -- and nothing wider.
/// Somebody who could read the member list before can still read it, and See members has not
/// quietly become a way to list everybody Modbot has ever seen. The member list's own endpoint,
/// <c>/api/members</c>, stays as it was for the API and the chat tools.
/// </para>
/// <para>
/// Search, filters and paging run on the server, for the reason the member list gives: the table
/// outgrows the member list by an order of magnitude, and a browser is not where a hundred
/// thousand rows get filtered. Search is a literal <c>ILIKE</c> on the display name and the id,
/// never a format check on the id (spec 3.1.1).
/// </para>
/// </remarks>
public static class PeopleEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>What <c>membership</c> accepts.</summary>
    private const string Member = "member";
    private const string NotMember = "not-member";
    private const string Left = "left";

    public static IEndpointRouteBuilder MapPeople(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/people", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? search,
                [FromQuery] string? membership,
                [FromQuery] bool? banned,
                [FromQuery] bool? everBanned,
                [FromQuery] string? profile,
                [FromQuery] bool? eighteenPlus,
                [FromQuery(Name = "trustRank")] string[]? trustRanks,
                [FromQuery(Name = "platform")] string[]? platforms,
                [FromQuery] string? linked,
                [FromQuery] bool? flagged,
                [FromQuery] DateTimeOffset? seenFrom,
                [FromQuery] DateTimeOffset? seenTo,
                [FromQuery(Name = "role")] string[]? roles,
                [FromQuery(Name = "notRole")] string[]? notRoles,
                [FromQuery] bool? noRole,
                [FromQuery] bool? representing,
                [FromQuery] DateTimeOffset? joinedFrom,
                [FromQuery] DateTimeOffset? joinedTo,
                [FromQuery] string? sort,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (membership is not (null or "" or Member or NotMember or Left or "all"))
                    return Results.BadRequest(new { error = "`membership` is member, not-member, left or all." });

                var held = ModbotAuth.PermissionsOf(http.User);
                var seesProfiles = ModbotAuth.Allows(held, ModbotPermissions.ViewProfile);

                // See members alone reads what the member list showed and no more: members and
                // people who left, with no links and no flags.
                if (!seesProfiles)
                {
                    if (!ModbotAuth.Allows(held, ModbotPermissions.ViewMembers))
                        return Results.Forbid();

                    if (Trimmed(membership)?.ToLowerInvariant() is not (Member or Left)
                        || flagged is not null
                        || LinkFilter.Narrows(linked))
                        return Results.Forbid();
                }

                if (profile is not (null or "" or "fetched" or "not-fetched"))
                    return Results.BadRequest(new { error = "`profile` is fetched or not-fetched." });

                if (!LinkFilter.IsValid(linked))
                    return Results.BadRequest(new { error = LinkFilter.Error });

                if (!Ranks(trustRanks, out var ranks))
                    return Results.BadRequest(new { error = "`trustRank` is a trust rank name, such as KnownUser." });

                return Results.Ok(await ListAsync(
                    db,
                    clock,
                    search,
                    membership,
                    profile,
                    sort,
                    page,
                    pageSize,
                    new PeopleFilters(
                        banned, everBanned, eighteenPlus, ranks, Platforms(platforms), linked, flagged, seenFrom, seenTo,
                        MemberEndpoints.Ids(roles), MemberEndpoints.Ids(notRoles), noRole, representing, joinedFrom, joinedTo),
                    seesProfiles,
                    ct));
            })
            .RequireAuthorization()
            .WithTags("People")
            .WithName("GetPeople")
            .WithSummary("List people")
            .WithDescription(
                "Every VRChat account Modbot has ever seen: members, people who left, people it "
                + "only ever saw in an instance or in the audit log. `search` matches the display "
                + "name and the id, case-insensitively and literally. `membership` is `member`, "
                + "`not-member`, `left` or `all` (the default); `banned` is true or false for the "
                + "group's ban list as it stands, `everBanned` true or false for a ban at any time, "
                + "lifted or not; `profile` is `fetched` or `not-fetched`. `eighteenPlus` is true or "
                + "false for Modbot's 18+ mark. `trustRank` is a trust rank name and may be "
                + "repeated: people holding any of them, and never anybody whose tags have not been "
                + "read. `platform` is the platform they last used, matched case-insensitively "
                + "against whatever VRChat sent, and may be repeated. `linked=linked` shows only "
                + "people with a linked Discord account and `linked=not-linked` only people without. "
                + "`flagged` is true or false for having ever been flagged by a moderation rule, "
                + "dismissed or not. `seenFrom` and `seenTo` narrow the list to people Modbot last "
                + "saw inside that stretch. `role` is a group role id and may be repeated: people "
                + "holding any of them; `notRole`, also repeatable, leaves out people holding any of "
                + "those; `noRole=true` keeps only people with no role. `representing` is true or "
                + "false. `joinedFrom` and `joinedTo` narrow the list to people who joined the group "
                + "inside that stretch, which is what an unusual-activity alert links to. Sorted by "
                + "when Modbot last saw them, most recent first, unless `sort=name`, `sort=known` "
                + "(longest known first) or `sort=joined` (newest joiner first). "
                + "Names and pictures come from the profile sync and are null for anybody it has "
                + "not fetched yet. Each row carries the member list's fields too: roles, when they "
                + "joined, whether they are representing the group, and their linked Discord "
                + "account. `roles` lists the group's roles with how many current members hold "
                + "each.\n\n"
                + "Needs See profiles. See members alone is enough for `membership=member` or "
                + "`membership=left`, without `flagged` or `linked`; the rows then carry no linked "
                + "Discord account.")
            .Produces<PeopleListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>
    /// Everything the list is narrowed by beyond the search box, the membership word and the sort.
    /// </summary>
    /// <remarks>
    /// A record rather than nine more parameters, for the reason the member list gives: the
    /// endpoint's own signature is already the whole query string, and a filter added later should
    /// not have to be threaded through every call.
    /// </remarks>
    /// <param name="Banned">On the group's ban list as it stands.</param>
    /// <param name="EverBanned">Banned from the group at any time, lifted or not.</param>
    /// <param name="EighteenPlus">Modbot's sticky 18+ mark.</param>
    /// <param name="TrustRanks">Any of these trust ranks. Empty means the rank is not filtered.</param>
    /// <param name="Platforms">Any of these <c>last_platform</c> values, lower-cased. Empty means the platform is not filtered.</param>
    /// <param name="Linked">A linked Discord account, in <see cref="LinkFilter"/>'s words.</param>
    /// <param name="Flagged">Ever flagged by a moderation rule, dismissed or not.</param>
    /// <param name="SeenFrom">Modbot last saw them on or after this.</param>
    /// <param name="SeenTo">Modbot last saw them before this.</param>
    /// <param name="AnyRoles">Holding at least one of these group roles.</param>
    /// <param name="NoneOfRoles">Holding none of these group roles; somebody who was never a member holds none.</param>
    /// <param name="NoRole">True: no group role at all, which includes everybody who was never a member; false: at least one.</param>
    /// <param name="Representing">Representing the group, or not.</param>
    /// <param name="JoinedFrom">Joined the group on or after this.</param>
    /// <param name="JoinedTo">Joined the group before this.</param>
    internal sealed record PeopleFilters(
        bool? Banned = null,
        bool? EverBanned = null,
        bool? EighteenPlus = null,
        IReadOnlyList<TrustRank>? TrustRanks = null,
        IReadOnlyList<string>? Platforms = null,
        string? Linked = null,
        bool? Flagged = null,
        DateTimeOffset? SeenFrom = null,
        DateTimeOffset? SeenTo = null,
        IReadOnlyList<string>? AnyRoles = null,
        IReadOnlyList<string>? NoneOfRoles = null,
        bool? NoRole = null,
        bool? Representing = null,
        DateTimeOffset? JoinedFrom = null,
        DateTimeOffset? JoinedTo = null);

    /// <summary>
    /// The trust ranks a repeated <c>trustRank</c> names, or false when one of them is not a rank.
    /// </summary>
    /// <remarks>
    /// Refused rather than ignored: a moderator who mistypes a rank should be told, not handed the
    /// whole list back as though they had asked for it.
    /// </remarks>
    private static bool Ranks(string[]? values, out IReadOnlyList<TrustRank> ranks)
    {
        var read = new List<TrustRank>();
        ranks = read;

        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!Enum.TryParse<TrustRank>(value.Trim(), ignoreCase: true, out var rank)) return false;
            if (!read.Contains(rank)) read.Add(rank);
        }

        return true;
    }

    /// <summary>
    /// The <c>last_platform</c> values a repeated <c>platform</c> names, lower-cased.
    /// </summary>
    /// <remarks>
    /// Never checked against a list of known platforms: VRChat documents the field as free text
    /// and it is stored exactly as sent, so a value this build has no word for is a value a
    /// moderator may still want to pick out. An unknown one simply matches nobody.
    /// </remarks>
    private static IReadOnlyList<string> Platforms(string[]? values)
        => [.. (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)];

    internal static async Task<PeopleListResponse> ListAsync(
        ModbotContext db,
        IModbotClock clock,
        string? search,
        string? membership,
        string? profile,
        string? sort,
        int? page,
        int? pageSize,
        PeopleFilters? filters,
        bool seesLinks,
        CancellationToken ct)
    {
        filters ??= new PeopleFilters();

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var groupId = settings?.ManagedGroupId ?? string.Empty;
        var roleNames = MemberEndpoints.RolesOf(settings);
        var (pageNumber, size) = Paging(page, pageSize);

        var members = db.GroupMembers.AsNoTracking().Where(m => m.GroupId == groupId);
        var bans = db.GroupBans.AsNoTracking().Where(b => b.GroupId == groupId && b.LiftedAt == null);
        var everBanned = db.GroupBans.AsNoTracking().Where(b => b.GroupId == groupId);

        // From the people rather than from the roster, so a member is one row among everybody. The
        // member sweep records every member it lists as seen (GroupMemberSync), so each member has
        // a row here to join to.
        var query =
            from u in db.VRChatUsers.AsNoTracking()
            join m in members on u.UserId equals m.UserId into joined
            from m in joined.DefaultIfEmpty()
            select new { u, m };

        if (Trimmed(search) is { } term)
        {
            var pattern = NameSearch.Pattern(term);

            query = NameSearch.SearchablePattern(term) is { } plain
                ? query.Where(x =>
                    EF.Functions.ILike(x.u.UserId, pattern, "\\")
                    || (x.u.DisplayName != null && EF.Functions.ILike(x.u.DisplayName, pattern, "\\"))
                    || (x.u.DisplayNameSearchable != null
                        && EF.Functions.ILike(x.u.DisplayNameSearchable, plain, "\\")))
                : query.Where(x =>
                    EF.Functions.ILike(x.u.UserId, pattern, "\\")
                    || (x.u.DisplayName != null && EF.Functions.ILike(x.u.DisplayName, pattern, "\\")));
        }

        query = Trimmed(membership)?.ToLowerInvariant() switch
        {
            Member => query.Where(x => x.m != null && x.m.LeftAt == null),
            NotMember => query.Where(x => x.m == null || x.m.LeftAt != null),
            Left => query.Where(x => x.m != null && x.m.LeftAt != null),
            _ => query,
        };

        if (filters.Banned is { } onTheBanList)
        {
            query = onTheBanList
                ? query.Where(x => bans.Any(b => b.UserId == x.u.UserId))
                : query.Where(x => !bans.Any(b => b.UserId == x.u.UserId));
        }

        // Ever banned is a different question from on the ban list: an unbanned person is off the
        // list and still somebody the group has banned once.
        if (filters.EverBanned is { } wasBanned)
        {
            query = wasBanned
                ? query.Where(x => everBanned.Any(b => b.UserId == x.u.UserId))
                : query.Where(x => !everBanned.Any(b => b.UserId == x.u.UserId));
        }

        query = Trimmed(profile) switch
        {
            "fetched" => query.Where(x => x.u.LastRefreshedAt != null),
            "not-fetched" => query.Where(x => x.u.LastRefreshedAt == null),
            _ => query,
        };

        if (filters.EighteenPlus is { } eighteenPlus)
            query = query.Where(x => x.u.Is18PlusVerified == eighteenPlus);

        // Null is not a rank: somebody whose tags have never been read is unknown, not a Visitor
        // (the entity says so), so picking any rank leaves them out rather than lumping them in.
        if (filters.TrustRanks is { Count: > 0 } ranks)
            query = query.Where(x => x.u.TrustRank != null && ranks.Contains(x.u.TrustRank.Value));

        if (filters.Platforms is { Count: > 0 } platforms)
            query = query.Where(x => x.u.LastPlatform != null && platforms.Contains(x.u.LastPlatform.ToLower()));

        var links = db.ActiveAccountLinks();

        query = LinkFilter.Normalised(filters.Linked) switch
        {
            LinkFilter.Linked => query.Where(x => links.Any(l => l.VRChatUserId == x.u.UserId)),
            LinkFilter.NotLinked => query.Where(x => !links.Any(l => l.VRChatUserId == x.u.UserId)),
            _ => query,
        };

        if (filters.Flagged is { } flagged)
        {
            var flags = db.ModerationFlags.AsNoTracking().Where(f => f.SubjectPlatform == FactPlatform.VRChat);

            query = flagged
                ? query.Where(x => flags.Any(f => f.SubjectId == x.u.UserId))
                : query.Where(x => !flags.Any(f => f.SubjectId == x.u.UserId));
        }

        if (filters.SeenFrom is { } seenFrom)
            query = query.Where(x => x.u.LastSeenAt >= seenFrom);

        if (filters.SeenTo is { } seenTo)
            query = query.Where(x => x.u.LastSeenAt < seenTo);

        // The member list's filters, as it runs them: PostgreSQL's `?|` on the roles array for
        // "any of" and its negation for "none of", both served by the GIN index. Somebody who was
        // never a member has no roles, so they hold none of any list and have no role at all.
        if (filters.AnyRoles is { Count: > 0 } anyRoles)
        {
            var wanted = anyRoles.ToArray();
            query = query.Where(x => x.m != null && EF.Functions.JsonExistAny(x.m.Roles, wanted));
        }

        if (filters.NoneOfRoles is { Count: > 0 } excluded)
        {
            var unwanted = excluded.ToArray();
            query = query.Where(x => x.m == null || !EF.Functions.JsonExistAny(x.m.Roles, unwanted));
        }

        if (filters.NoRole is { } noRole)
        {
            query = noRole
                ? query.Where(x => x.m == null || x.m.Roles == "[]")
                : query.Where(x => x.m != null && x.m.Roles != "[]");
        }

        if (filters.Representing is { } representing)
        {
            query = representing
                ? query.Where(x => x.m != null && x.m.IsRepresenting)
                : query.Where(x => x.m == null || !x.m.IsRepresenting);
        }

        if (filters.JoinedFrom is { } joinedFrom)
            query = query.Where(x => x.m != null && x.m.JoinedAt >= joinedFrom);

        if (filters.JoinedTo is { } joinedTo)
            query = query.Where(x => x.m != null && x.m.JoinedAt < joinedTo);

        query = Trimmed(sort)?.ToLowerInvariant() switch
        {
            // Newest joiner first, as the member list opened; people with no join date go last.
            "joined" => query
                .OrderBy(x => x.m == null || x.m.JoinedAt == null)
                .ThenByDescending(x => x.m!.JoinedAt)
                .ThenBy(x => x.u.UserId),
            // A name nobody has fetched yet sorts last rather than first: a page of blank rows is
            // not what "by name" is for.
            "name" => query
                .OrderBy(x => x.u.DisplayName == null)
                .ThenBy(x => x.u.DisplayName)
                .ThenBy(x => x.u.UserId),
            "known" => query.OrderBy(x => x.u.FirstSeenAt).ThenBy(x => x.u.UserId),
            _ => query.OrderByDescending(x => x.u.LastSeenAt).ThenBy(x => x.u.UserId),
        };

        var total = await query.CountAsync(ct);

        var rows = await query
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Select(x => new
            {
                x.u,
                Left = x.m == null ? null : x.m.LeftAt,
                WasMember = x.m != null,
                Roles = x.m == null ? null : x.m.Roles,
                Joined = x.m == null ? null : x.m.JoinedAt,
                Representing = x.m != null && x.m.IsRepresenting,
            })
            .ToListAsync(ct);

        // One query for the whole page rather than a join per row: the ban list is small and the
        // page is fifty ids. The linked accounts are the member list's two queries, for the same
        // fifty ids.
        var ids = rows.Select(r => r.u.UserId).ToList();
        var onTheList = await bans
            .Where(b => ids.Contains(b.UserId))
            .Select(b => b.UserId)
            .ToListAsync(ct);

        var bannedIds = onTheList.ToHashSet(StringComparer.Ordinal);

        var discord = seesLinks
            ? await MemberEndpoints.LinkedDiscordAsync(db, settings?.DiscordGuildId, ids, ct)
            : new Dictionary<string, LinkedDiscordView>();

        var people = rows.Select(r =>
        {
            var roleIds = GroupMemberSync.RoleIds(r.Roles);

            return new PersonRow(
                r.u.UserId,
                r.u.DisplayName,
                PlainName.Of(r.u.DisplayName),
                ProfilePictures.Best(r.u),
                r.u.TrustRank,
                r.u.Is18PlusVerified,
                IsMember: r.WasMember && r.Left == null,
                r.Left,
                bannedIds.Contains(r.u.UserId),
                r.u.FirstSeenAt,
                r.u.LastSeenAt,
                r.u.LastRefreshedAt,
                r.u.NotFoundAt,
                roleIds,
                MemberEndpoints.RoleNames(roleIds, roleNames),
                r.Joined,
                r.Representing,
                discord.GetValueOrDefault(r.u.UserId));
        }).ToList();

        var held = await MemberEndpoints.RoleCountsAsync(db, groupId, ct);

        return new PeopleListResponse(
            people,
            total,
            pageNumber,
            size,
            MemberEndpoints.RoleOptions(roleNames, held),
            new PeopleCoverage(
                await db.VRChatUsers.AsNoTracking().CountAsync(ct),
                await members.CountAsync(m => m.LeftAt == null, ct),
                clock.UtcNow,
                MemberEndpoints.MemberCoverage(settings, clock.UtcNow)));
    }

    private static (int Page, int Size) Paging(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
