using Microsoft.AspNetCore.Builder;
using Modbot.Core.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Api.Features.Members;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Search;

/// <summary>
/// A VRChat person found by name or id, and where they stand with the group. The name is null until
/// the profile sync has fetched one.
/// </summary>
/// <param name="IsMember">On the group's member list now.</param>
/// <param name="Left">Was on the member list and has left it.</param>
/// <param name="Banned">On the group's ban list now.</param>
public sealed record SearchPerson(string UserId, string? DisplayName, string? AvatarUrl, bool IsMember, bool Left, bool Banned);

/// <summary>A Discord person found by any of their names or their id.</summary>
public sealed record SearchDiscordPerson(string UserId, string DisplayName, string Username, string? AvatarUrl, bool InServer);

/// <summary>A world found by name or id. The name is null until its page has been read.</summary>
public sealed record SearchWorld(string WorldId, string? Name, string? ThumbnailImageUrl);

/// <summary>
/// What a search found, one list per kind. A list is empty when nothing matched or when the
/// caller may not see that kind at all.
/// </summary>
public sealed record SearchResults(
    IReadOnlyList<SearchPerson> People,
    IReadOnlyList<SearchDiscordPerson> DiscordPeople,
    IReadOnlyList<SearchWorld> Worlds);

/// <summary>
/// One search over people, Discord people and worlds, for the command palette.
/// </summary>
/// <remarks>
/// <para>
/// One round trip rather than three, because the palette asks on every pause in typing and
/// three requests per keystroke is the wrong shape. Each list is narrowed by the permission that
/// gates the page it would otherwise be found on: people and Discord people by
/// <see cref="ModbotPermissions.ViewMembers"/>, worlds by <see cref="ModbotPermissions.ViewAnalytics"/>.
/// A caller without a permission gets an empty list for that kind, never a refusal for asking.
/// </para>
/// <para>
/// Ids are matched as text, never parsed (spec 3.1.1): a legacy VRChat id is found by typing
/// part of it like anything else.
/// </para>
/// </remarks>
public static class SearchEndpoints
{
    public const int DefaultLimit = 8;
    public const int MaxLimit = 25;

    public static IEndpointRouteBuilder MapSearch(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/search", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromQuery] string? q,
                [FromQuery] int? limit,
                CancellationToken ct) =>
            {
                var held = ModbotAuth.PermissionsOf(http.User);

                return Results.Ok(await SearchAsync(
                    db,
                    q,
                    limit,
                    people: ModbotAuth.Allows(held, ModbotPermissions.ViewMembers),
                    worlds: ModbotAuth.Allows(held, ModbotPermissions.ViewAnalytics),
                    ct));
            })
            .RequireAuthorization()
            .WithTags("Search")
            .WithName("Search")
            .WithSummary("Search people and worlds")
            .WithDescription(
                "People, Discord people and worlds by name or id, for the command palette. "
                + "`q` is matched against display names and ids, case-insensitively. People and Discord "
                + "people need See members; worlds need See analytics. Each person says whether they are "
                + "on the group's member list, have left it, or are on its ban list. A kind the caller may not see "
                + "comes back as an empty list. At most `limit` of each kind (default 8, max 25).")
            .Produces<SearchResults>();

        return app;
    }

    internal static async Task<SearchResults> SearchAsync(
        ModbotContext db, string? q, int? limit, bool people, bool worlds, CancellationToken ct)
    {
        var term = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        if (term is null)
            return new SearchResults([], [], []);

        var pattern = MemberEndpoints.Pattern(term);

        var found = people ? await PeopleAsync(db, pattern, take, ct) : [];

        List<SearchDiscordPerson> discord = [];
        if (people)
        {
            var guildId = await DiscordMemberEndpoints.GuildIdAsync(db, ct);

            if (guildId is not null)
            {
                discord = await db.DiscordMembers.AsNoTracking()
                    .Where(m => m.GuildId == guildId
                        && (EF.Functions.ILike(m.DisplayName, pattern, "\\")
                            || EF.Functions.ILike(m.Username, pattern, "\\")
                            || (m.GlobalName != null && EF.Functions.ILike(m.GlobalName, pattern, "\\"))
                            || (m.Nickname != null && EF.Functions.ILike(m.Nickname, pattern, "\\"))
                            || m.UserId == term))
                    .OrderBy(m => m.LeftAt != null)
                    .ThenByDescending(m => m.JoinedAt ?? m.FirstSeenAt)
                    .Take(take)
                    .Select(m => new SearchDiscordPerson(m.UserId, m.DisplayName, m.Username, m.AvatarUrl, m.LeftAt == null))
                    .ToListAsync(ct);
            }
        }

        var places = worlds
            ? await db.VRChatWorlds.AsNoTracking()
                .Where(w => EF.Functions.ILike(w.WorldId, pattern, "\\")
                    || (w.Name != null && EF.Functions.ILike(w.Name, pattern, "\\")))
                .OrderBy(w => w.Name == null)
                .ThenByDescending(w => w.LastSeenAt)
                .Take(take)
                .Select(w => new SearchWorld(w.WorldId, w.Name, w.ThumbnailImageUrl))
                .ToListAsync(ct)
            : [];

        return new SearchResults(found, discord, places);
    }

    /// <summary>
    /// The people who match, each with where they stand: the People page's Member, Left and Banned,
    /// so a moderator can tell the member from the person already banned without opening either.
    /// </summary>
    /// <remarks>
    /// Read from the member and ban lists Modbot already keeps, never from VRChat: the palette asks
    /// on every pause in typing. Two lookups for the page of ids rather than a join per row, as the
    /// People page does it.
    /// </remarks>
    private static async Task<List<SearchPerson>> PeopleAsync(ModbotContext db, string pattern, int take, CancellationToken ct)
    {
        var rows = await db.VRChatUsers.AsNoTracking()
            .Where(u => EF.Functions.ILike(u.UserId, pattern, "\\")
                || (u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern, "\\")))
            .OrderBy(u => u.DisplayName == null)
            .ThenByDescending(u => u.LastSeenAt)
            .Take(take)
            .Select(u => new { u.UserId, u.DisplayName, u.ProfilePictureUrl, u.IconUrl, u.CurrentAvatarThumbnailImageUrl })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return [];

        var groupId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var ids = rows.Select(r => r.UserId).ToList();

        var members = await db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && ids.Contains(m.UserId))
            .Select(m => new { m.UserId, m.LeftAt })
            .ToDictionaryAsync(m => m.UserId, m => m.LeftAt, StringComparer.Ordinal, ct);

        var banned = (await db.GroupBans.AsNoTracking()
                .Where(b => b.GroupId == groupId && b.LiftedAt == null && ids.Contains(b.UserId))
                .Select(b => b.UserId)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        return rows
            .Select(u =>
            {
                var wasMember = members.TryGetValue(u.UserId, out var leftAt);

                return new SearchPerson(
                    u.UserId,
                    u.DisplayName,
                    ProfilePictures.Best(u.ProfilePictureUrl, u.IconUrl, u.CurrentAvatarThumbnailImageUrl),
                    IsMember: wasMember && leftAt == null,
                    Left: wasMember && leftAt != null,
                    Banned: banned.Contains(u.UserId));
            })
            .ToList();
    }
}
