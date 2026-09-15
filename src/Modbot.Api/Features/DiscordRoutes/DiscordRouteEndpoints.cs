using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.DiscordRoutes;

/// <summary>One rule for sending events to a Discord channel, as the settings page reads and writes it.</summary>
/// <param name="SubjectIds">VRChat accounts it must be about.</param>
/// <param name="SubjectDiscordIds">Discord accounts it must be about.</param>
/// <param name="ActorAutomatic">Also match events nobody did -- Modbot and the syncs on their own.</param>
public sealed record DiscordRouteView(
    Guid Id,
    string? Name,
    string ChannelId,
    bool Enabled,
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> SubjectIds,
    IReadOnlyList<string> SubjectDiscordIds,
    IReadOnlyList<string> ActorIds,
    IReadOnlyList<string> ActorDiscordIds,
    bool ActorAutomatic,
    IReadOnlyList<string> SubjectVRChatRoleIds,
    IReadOnlyList<string> ActorVRChatRoleIds,
    IReadOnlyList<Guid> ActorModbotRoleIds)
{
    public static DiscordRouteView From(DiscordEventRoute route) => new(
        route.Id,
        route.Name,
        route.ChannelId,
        route.Enabled,
        route.EventTypes,
        route.SubjectIds,
        route.SubjectDiscordIds,
        route.ActorIds,
        route.ActorDiscordIds,
        route.ActorAutomatic,
        route.SubjectVRChatRoleIds,
        route.ActorVRChatRoleIds,
        route.ActorModbotRoleIds);
}

/// <summary>The body of a create or a change. Every list is optional and empty means "anyone".</summary>
public sealed record DiscordRouteRequest(
    string? Name,
    string? ChannelId,
    bool? Enabled,
    IReadOnlyList<string>? EventTypes,
    IReadOnlyList<string>? SubjectIds,
    IReadOnlyList<string>? ActorIds,
    bool? ActorAutomatic,
    IReadOnlyList<string>? SubjectVRChatRoleIds,
    IReadOnlyList<string>? ActorVRChatRoleIds,
    IReadOnlyList<Guid>? ActorModbotRoleIds,
    IReadOnlyList<string>? SubjectDiscordIds = null,
    IReadOnlyList<string>? ActorDiscordIds = null);

public sealed record DiscordRouteEventType(string Type, string Label);

public sealed record DiscordRouteEventGroup(string Name, IReadOnlyList<DiscordRouteEventType> Types);

public sealed record DiscordRouteOption(string Id, string Name);

public static class DiscordRoutePlatform
{
    public const string VRChat = "vrchat";
    public const string Discord = "discord";
}

/// <summary>A person a route names, with the name Modbot has for them, if any.</summary>
/// <param name="Platform"><c>vrchat</c> or <c>discord</c>: which list the id belongs in.</param>
public sealed record DiscordRoutePerson(string Id, string? Name, string? PictureUrl, string Platform = DiscordRoutePlatform.VRChat);

/// <param name="People">Names for every person the routes name, so the list reads as names rather than ids.</param>
public sealed record DiscordRoutesResponse(
    IReadOnlyList<DiscordRouteView> Routes,
    IReadOnlyList<DiscordRouteEventGroup> EventGroups,
    IReadOnlyList<DiscordRouteOption> VRChatRoles,
    IReadOnlyList<DiscordRouteOption> ModbotRoles,
    IReadOnlyList<DiscordRoutePerson> People);

public sealed record DiscordRoutePeopleResponse(IReadOnlyList<DiscordRoutePerson> People);

/// <summary>
/// The rules for which events go to which Discord channel (Discord event routes design).
/// </summary>
/// <remarks>
/// <para>
/// Gated on <see cref="ModbotPermissions.ManageSettings"/>, like every other Discord setting and
/// the channel list these are picked from. The person search is under the same gate rather than
/// <c>ViewMembers</c>, so somebody who may set up channels can fill in the filters.
/// </para>
/// <para>
/// Event types are cut down to <see cref="DiscordEventTypes.Sendable"/> on the way in; the poster
/// checks them again on the way out. Every change is recorded as a settings change, with the route
/// as it now stands.
/// </para>
/// </remarks>
public static class DiscordRouteEndpoints
{
    public const int MaxNameLength = 100;

    /// <summary>A generous cap on each filter list, so a request cannot store an unbounded row.</summary>
    public const int MaxListLength = 200;

    public const int PeopleSearchLimit = 20;

    public static IEndpointRouteBuilder MapDiscordRoutes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord/routes")
            .WithTags("Discord")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var routes = await db.DiscordEventRoutes.AsNoTracking()
                    .OrderBy(r => r.Position)
                    .ThenBy(r => r.Id)
                    .ToListAsync(ct);

                var settings = await db.Settings.AsNoTracking()
                    .Where(s => s.Id == 1)
                    .Select(s => new { s.GroupInfoSnapshot })
                    .FirstOrDefaultAsync(ct);

                var vrchatRoles = (GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot)?.Roles ?? [])
                    .OrderBy(r => r.Order)
                    .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new DiscordRouteOption(r.Id, string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name))
                    .ToList();

                var modbotRoles = await db.Roles.AsNoTracking()
                    .OrderByDescending(r => r.IsBuiltIn)
                    .ThenBy(r => r.NameNormalized)
                    .Select(r => new DiscordRouteOption(r.Id.ToString(), r.Name))
                    .ToListAsync(ct);

                var named = routes.SelectMany(r => r.SubjectIds.Concat(r.ActorIds)).Distinct(StringComparer.Ordinal).ToList();
                var namedDiscord = routes.SelectMany(r => r.SubjectDiscordIds.Concat(r.ActorDiscordIds)).Distinct(StringComparer.Ordinal).ToList();
                var people = await PeopleAsync(db, named, ct);
                people.AddRange(await DiscordPeopleAsync(db, namedDiscord, ct));

                return Results.Ok(new DiscordRoutesResponse(
                    routes.Select(DiscordRouteView.From).ToList(),
                    DiscordEventTypes.Groups
                        .Select(g => new DiscordRouteEventGroup(
                            g.Name,
                            g.Types.Select(t => new DiscordRouteEventType(t, FactLabels.For(t))).ToList()))
                        .ToList(),
                    vrchatRoles,
                    modbotRoles,
                    people));
            })
            .WithName("ListDiscordRoutes")
            .WithSummary("The channels events are sent to, and what each can be set to")
            .WithDescription(
                "Every route, in list order, with the event types that may be chosen (grouped, "
                + "labelled), the group's VRChat roles as last read, Modbot's roles, and names for "
                + "the people routes already name.")
            .Produces<DiscordRoutesResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                [FromBody] DiscordRouteRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var route = new DiscordEventRoute { Id = Guid.NewGuid() };
                if (Apply(route, body, creating: true) is { } error)
                    return Results.BadRequest(new { error });

                var last = await db.DiscordEventRoutes.MaxAsync(r => (int?)r.Position, ct);
                route.Position = (last ?? -1) + 1;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.DiscordEventRoutes.Add(route);
                await db.SaveChangesAsync(ct);
                await RecordAsync(facts, http, "create", route, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(DiscordRouteView.From(route));
            })
            .WithName("CreateDiscordRoute")
            .WithSummary("Start sending events to a channel")
            .WithDescription(
                "A channel and at least one event type are needed. Sending starts from the moment "
                + "it is saved; nothing that happened before is posted.")
            .Produces<DiscordRouteView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                [FromRoute] Guid id,
                [FromBody] DiscordRouteRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var route = await db.DiscordEventRoutes.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (route is null)
                    return Results.NotFound();

                if (Apply(route, body, creating: false) is { } error)
                    return Results.BadRequest(new { error });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                await db.SaveChangesAsync(ct);
                await RecordAsync(facts, http, "change", route, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(DiscordRouteView.From(route));
            })
            .WithName("UpdateDiscordRoute")
            .WithSummary("Change a channel's events or filters, or turn it on or off")
            .WithDescription("Fields left out are left as they are. A list sent empty clears that filter.")
            .Produces<DiscordRouteView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var route = await db.DiscordEventRoutes.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (route is null)
                    return Results.NotFound();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.DiscordEventRoutes.Remove(route);
                await db.SaveChangesAsync(ct);
                await RecordAsync(facts, http, "delete", route, ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .WithName("DeleteDiscordRoute")
            .WithSummary("Stop sending events to a channel and forget the rule")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/people", async (
                [FromQuery] string? search,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var term = search?.Trim();
                if (string.IsNullOrEmpty(term))
                    return Results.Ok(new DiscordRoutePeopleResponse([]));

                var pattern = Members.MemberEndpoints.Pattern(term);

                var found = await db.VRChatUsers.AsNoTracking()
                    .Where(u => u.UserId == term
                        || EF.Functions.ILike(u.UserId, pattern, "\\")
                        || (u.DisplayName != null && EF.Functions.ILike(u.DisplayName, pattern, "\\")))
                    .OrderBy(u => u.UserId != term)
                    .ThenBy(u => u.DisplayName)
                    .ThenBy(u => u.UserId)
                    .Take(PeopleSearchLimit)
                    .Select(u => new DiscordRoutePerson(
                        u.UserId,
                        u.DisplayName,
                        u.ProfilePictureUrl ?? u.CurrentAvatarThumbnailImageUrl,
                        DiscordRoutePlatform.VRChat))
                    .ToListAsync(ct);

                // Discord accounts: the server's member list, whether or not they linked anything,
                // then linked accounts no longer in the server. Anybody else is picked by typing
                // their Discord id, which the picker offers as it is.
                var members = await db.DiscordMembers.AsNoTracking()
                    .Where(m => !m.IsBot
                        && (m.UserId == term
                            || EF.Functions.ILike(m.Username, pattern, "\\")
                            || EF.Functions.ILike(m.DisplayName, pattern, "\\")
                            || (m.GlobalName != null && EF.Functions.ILike(m.GlobalName, pattern, "\\"))
                            || (m.Nickname != null && EF.Functions.ILike(m.Nickname, pattern, "\\"))))
                    .OrderBy(m => m.UserId != term)
                    .ThenBy(m => m.LeftAt != null)
                    .ThenBy(m => m.DisplayName)
                    .Select(m => new { m.UserId, m.DisplayName, m.AvatarUrl })
                    .Take(PeopleSearchLimit * 2)
                    .ToListAsync(ct);

                var linked = await db.DiscordAccountLinks.AsNoTracking()
                    .Where(l => l.DiscordUserId == term || EF.Functions.ILike(l.DiscordUsername, pattern, "\\"))
                    .OrderByDescending(l => l.UnlinkedAt == null)
                    .ThenByDescending(l => l.LinkedAt)
                    .Select(l => new { l.DiscordUserId, l.DiscordUsername })
                    .Take(PeopleSearchLimit * 2)
                    .ToListAsync(ct);

                found.AddRange(members
                    .Select(m => new DiscordRoutePerson(m.UserId, m.DisplayName, m.AvatarUrl, DiscordRoutePlatform.Discord))
                    .Concat(linked.Select(l => new DiscordRoutePerson(l.DiscordUserId, l.DiscordUsername, null, DiscordRoutePlatform.Discord)))
                    .DistinctBy(p => p.Id)
                    .Take(PeopleSearchLimit));

                return Results.Ok(new DiscordRoutePeopleResponse(found));
            })
            .WithName("SearchDiscordRoutePeople")
            .WithSummary("People to pick for a channel's filters, by name or id")
            .WithDescription(
                "Searches the VRChat profiles Modbot has stored, the same way the member list "
                + "searches: case-insensitively on the display name and the id. Also searches the "
                + "Discord server's members and the Discord accounts members linked, by name and id. "
                + "`platform` says which list an id goes in.")
            .Produces<DiscordRoutePeopleResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>Copies the request onto the route. Returns a sentence when something is wrong.</summary>
    internal static string? Apply(DiscordEventRoute route, DiscordRouteRequest body, bool creating)
    {
        if (body.Name is not null)
        {
            var name = body.Name.Trim();
            if (name.Length > MaxNameLength)
                return $"That name is longer than {MaxNameLength} characters.";

            route.Name = name.Length == 0 ? null : name;
        }

        if (body.ChannelId is not null || creating)
        {
            var channel = body.ChannelId?.Trim() ?? string.Empty;
            if (channel.Length == 0)
                return "Pick a channel.";

            route.ChannelId = channel;
        }

        if (body.Enabled is { } enabled)
            route.Enabled = enabled;

        if (body.EventTypes is not null || creating)
        {
            var types = DiscordEventTypes.Clean(body.EventTypes);
            if (types.Count == 0)
                return "Pick at least one event.";

            route.EventTypes = types.ToList();
        }

        if (body.SubjectIds is not null)
            route.SubjectIds = Ids(body.SubjectIds);

        if (body.ActorIds is not null)
            route.ActorIds = Ids(body.ActorIds);

        if (body.SubjectDiscordIds is not null)
            route.SubjectDiscordIds = Ids(body.SubjectDiscordIds);

        if (body.ActorDiscordIds is not null)
            route.ActorDiscordIds = Ids(body.ActorDiscordIds);

        if (body.ActorAutomatic is { } automatic)
            route.ActorAutomatic = automatic;

        if (body.SubjectVRChatRoleIds is not null)
            route.SubjectVRChatRoleIds = Ids(body.SubjectVRChatRoleIds);

        if (body.ActorVRChatRoleIds is not null)
            route.ActorVRChatRoleIds = Ids(body.ActorVRChatRoleIds);

        if (body.ActorModbotRoleIds is not null)
            route.ActorModbotRoleIds = body.ActorModbotRoleIds.Where(g => g != Guid.Empty).Distinct().Take(MaxListLength).ToList();

        return null;
    }

    /// <summary>Opaque ids, trimmed, each once, blanks dropped. Never checked for shape (foundation §3.1.1).</summary>
    private static List<string> Ids(IEnumerable<string> ids)
        => ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(MaxListLength)
            .ToList();

    private static async Task<List<DiscordRoutePerson>> PeopleAsync(ModbotContext db, List<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var known = await db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId))
            .Select(u => new DiscordRoutePerson(u.UserId, u.DisplayName, u.ProfilePictureUrl ?? u.CurrentAvatarThumbnailImageUrl, DiscordRoutePlatform.VRChat))
            .ToListAsync(ct);

        var byId = known.ToDictionary(p => p.Id, StringComparer.Ordinal);
        return ids.Select(id => byId.GetValueOrDefault(id) ?? new DiscordRoutePerson(id, null, null, DiscordRoutePlatform.VRChat)).ToList();
    }

    /// <summary>Discord accounts with their name in the server, or else the username they were linked under.</summary>
    private static async Task<List<DiscordRoutePerson>> DiscordPeopleAsync(ModbotContext db, List<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var members = await db.DiscordMembers.AsNoTracking()
            .Where(m => ids.Contains(m.UserId))
            .OrderBy(m => m.LeftAt != null)
            .Select(m => new { m.UserId, m.DisplayName, m.AvatarUrl })
            .ToListAsync(ct);

        var fromServer = members.DistinctBy(m => m.UserId).ToDictionary(m => m.UserId, StringComparer.Ordinal);

        var links = await db.DiscordAccountLinks.AsNoTracking()
            .Where(l => ids.Contains(l.DiscordUserId))
            .OrderByDescending(l => l.LinkedAt)
            .Select(l => new { l.DiscordUserId, l.DiscordUsername })
            .ToListAsync(ct);

        var names = links.DistinctBy(l => l.DiscordUserId).ToDictionary(l => l.DiscordUserId, l => l.DiscordUsername, StringComparer.Ordinal);
        return ids
            .Select(id => fromServer.TryGetValue(id, out var member)
                ? new DiscordRoutePerson(id, member.DisplayName, member.AvatarUrl, DiscordRoutePlatform.Discord)
                : new DiscordRoutePerson(id, names.GetValueOrDefault(id), null, DiscordRoutePlatform.Discord))
            .ToList();
    }

    private static Task RecordAsync(AccountFacts facts, HttpContext http, string action, DiscordEventRoute route, CancellationToken ct)
        => facts.RecordAsync(
            FactType.SettingsChanged,
            "settings",
            Actor.Of(http),
            new JsonObject
            {
                ["setting"] = "discordRoutes",
                ["action"] = action,
                ["routeId"] = route.Id.ToString(),
                ["channelId"] = route.ChannelId,
                ["name"] = route.Name,
                ["enabled"] = route.Enabled,
                ["eventTypes"] = new JsonArray(route.EventTypes.Select(t => (JsonNode?)t).ToArray()),
                ["subjectIds"] = new JsonArray(route.SubjectIds.Select(t => (JsonNode?)t).ToArray()),
                ["subjectDiscordIds"] = new JsonArray(route.SubjectDiscordIds.Select(t => (JsonNode?)t).ToArray()),
                ["actorIds"] = new JsonArray(route.ActorIds.Select(t => (JsonNode?)t).ToArray()),
                ["actorDiscordIds"] = new JsonArray(route.ActorDiscordIds.Select(t => (JsonNode?)t).ToArray()),
                ["actorAutomatic"] = route.ActorAutomatic,
                ["subjectVRChatRoleIds"] = new JsonArray(route.SubjectVRChatRoleIds.Select(t => (JsonNode?)t).ToArray()),
                ["actorVRChatRoleIds"] = new JsonArray(route.ActorVRChatRoleIds.Select(t => (JsonNode?)t).ToArray()),
                ["actorModbotRoleIds"] = new JsonArray(route.ActorModbotRoleIds.Select(t => (JsonNode?)t.ToString()).ToArray()),
            },
            ct);
}
