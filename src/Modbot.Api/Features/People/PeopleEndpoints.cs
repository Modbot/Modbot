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
                [FromQuery] string? profile,
                [FromQuery] string? sort,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                CancellationToken ct) =>
            {
                if (membership is not (null or "" or Member or NotMember or Left or "all"))
                    return Results.BadRequest(new { error = "`membership` is member, not-member, left or all." });

                if (profile is not (null or "" or "fetched" or "not-fetched"))
                    return Results.BadRequest(new { error = "`profile` is fetched or not-fetched." });

                return Results.Ok(await ListAsync(
                    db, clock, search, membership, banned, profile, sort, page, pageSize, ct));
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
                + "`not-member`, `left` or `all` (the default); `banned` is true or false; "
                + "`profile` is `fetched` or `not-fetched`. Sorted by when Modbot last saw them, "
                + "most recent first, unless `sort=name` or `sort=known` (longest known first). "
                + "Names and pictures come from the profile sync and are null for anybody it has "
                + "not fetched yet.")
            .Produces<PeopleListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    internal static async Task<PeopleListResponse> ListAsync(
        ModbotContext db,
        IModbotClock clock,
        string? search,
        string? membership,
        bool? banned,
        string? profile,
        string? sort,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var groupId = settings?.ManagedGroupId ?? string.Empty;
        var (pageNumber, size) = Paging(page, pageSize);

        var members = db.GroupMembers.AsNoTracking().Where(m => m.GroupId == groupId);
        var bans = db.GroupBans.AsNoTracking().Where(b => b.GroupId == groupId && b.LiftedAt == null);

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

        if (banned is { } onTheBanList)
        {
            query = onTheBanList
                ? query.Where(x => bans.Any(b => b.UserId == x.u.UserId))
                : query.Where(x => !bans.Any(b => b.UserId == x.u.UserId));
        }

        query = Trimmed(profile) switch
        {
            "fetched" => query.Where(x => x.u.LastRefreshedAt != null),
            "not-fetched" => query.Where(x => x.u.LastRefreshedAt == null),
            _ => query,
        };

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
