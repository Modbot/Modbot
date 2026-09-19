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
using Modbot.Core.Users;

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
/// Gated on See profiles rather than See members, and on nothing new. Every field on a row is a
/// field the person popup already shows to the same permission; what the page adds is the list of
/// who there is, which is the one thing a moderator could not get at before.
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
                [FromQuery] string? sort,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (membership is not (null or "" or Member or NotMember or Left or "all"))
                    return Results.BadRequest(new { error = "`membership` is member, not-member, left or all." });

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
                        banned, everBanned, eighteenPlus, ranks, Platforms(platforms), linked, flagged, seenFrom, seenTo),
                    ct));
            })
            .RequireAuthorization()
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithTags("People")
            .WithName("GetPeople")
            .WithSummary("Everyone Modbot has a record of, member or not")
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
                + "saw inside that stretch. Sorted by when Modbot last saw them, "
                + "most recent first, unless `sort=name` or `sort=known` (longest known first). "
                + "Names and pictures come from the profile sync and are null for anybody it has "
                + "not fetched yet.")
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
    internal sealed record PeopleFilters(
        bool? Banned = null,
        bool? EverBanned = null,
        bool? EighteenPlus = null,
        IReadOnlyList<TrustRank>? TrustRanks = null,
        IReadOnlyList<string>? Platforms = null,
        string? Linked = null,
        bool? Flagged = null,
        DateTimeOffset? SeenFrom = null,
        DateTimeOffset? SeenTo = null);

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
        CancellationToken ct)
    {
        filters ??= new PeopleFilters();

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var groupId = settings?.ManagedGroupId ?? string.Empty;
        var (pageNumber, size) = Paging(page, pageSize);

        var members = db.GroupMembers.AsNoTracking().Where(m => m.GroupId == groupId);
        var bans = db.GroupBans.AsNoTracking().Where(b => b.GroupId == groupId && b.LiftedAt == null);
        var everBanned = db.GroupBans.AsNoTracking().Where(b => b.GroupId == groupId);

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

        query = Trimmed(sort)?.ToLowerInvariant() switch
        {
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
            .Select(x => new { x.u, Left = x.m == null ? null : x.m.LeftAt, WasMember = x.m != null })
            .ToListAsync(ct);

        // One query for the whole page rather than a join per row: the ban list is small and the
        // page is fifty ids.
        var ids = rows.Select(r => r.u.UserId).ToList();
        var onTheList = await bans
            .Where(b => ids.Contains(b.UserId))
            .Select(b => b.UserId)
            .ToListAsync(ct);

        var bannedIds = onTheList.ToHashSet(StringComparer.Ordinal);

        var people = rows.Select(r => new PersonRow(
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
            r.u.NotFoundAt)).ToList();

        return new PeopleListResponse(
            people,
            total,
            pageNumber,
            size,
            new PeopleCoverage(
                await db.VRChatUsers.AsNoTracking().CountAsync(ct),
                await members.CountAsync(m => m.LeftAt == null, ct),
                clock.UtcNow));
    }

    private static (int Page, int Size) Paging(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
