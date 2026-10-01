using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Calendar;
using Modbot.Api.Features.Users;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.WorldLists;

/// <summary>
/// World lists: lists of worlds kept in Modbot, each world with the players its game is for, for an
/// event to pick its world from (world lists design).
/// </summary>
/// <remarks>
/// <para>
/// Under the calendar's permissions (§3): See calendar to look, Manage calendar to change.
/// </para>
/// <para>
/// The one VRChat call is <see cref="FindAsync"/>'s, for a pasted world Modbot has never read: the
/// world sweep's own <c>GetWorld</c> on <c>worlds.read</c>, once, at interactive priority (§2). A 429
/// cold-stops the bucket and is never retried; the world is kept by its id for the sweep to name.
/// </para>
/// </remarks>
public static class WorldListEndpoints
{
    /// <summary>The most worlds one search returns.</summary>
    public const int SearchLimit = 50;

    public static IEndpointRouteBuilder MapWorldLists(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/world-lists").WithTags("World lists");

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var lists = await db.WorldLists.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);

                return Results.Ok(new WorldListsView(
                    await ViewsAsync(db, lists, ct),
                    ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManageCalendar)));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("ListWorldLists")
            .WithSummary("List world lists")
            .WithDescription("Every world list by name, with its worlds, their players, and the events that pick from it.")
            .Produces<WorldListsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/worlds", async (
                [FromQuery] string? search,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var worlds = db.VRChatWorlds.AsNoTracking();
                var term = search?.Trim();

                if (!string.IsNullOrEmpty(term))
                {
                    var lower = term.ToLowerInvariant();
                    worlds = worlds.Where(w => w.WorldId == term || (w.Name != null && w.Name.ToLower().Contains(lower)));
                }

                var found = await worlds
                    .OrderByDescending(w => w.LastSeenAt)
                    .Take(SearchLimit)
                    .Select(w => new CalendarWorldView(w.WorldId, w.Name, w.ThumbnailImageUrl))
                    .ToListAsync(ct);

                return Results.Ok(found);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("SearchWorldListWorlds")
            .WithSummary("Search known worlds")
            .WithDescription(
                "Worlds Modbot knows whose name holds the search, or whose id is it, most recently seen "
                + "first, at most 50. Asks VRChat nothing.")
            .Produces<IReadOnlyList<CalendarWorldView>>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/worlds/find", async (
                [FromBody] WorldFindRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IVRChatGate gate,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);
                return await FindAsync(db, gate, clock, body.Text, ct);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("FindWorldListWorld")
            .WithSummary("Find a world by link or id")
            .WithDescription(
                "Takes a world's link or id. A world Modbot has never read is read from VRChat once "
                + "(worlds.read). When VRChat cannot be asked right now, the world comes back by its id "
                + "with no name, and Modbot names it later.")
            .Produces<CalendarWorldView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] WorldListRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (Check(body, out var name, out var worlds) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var now = clock.UtcNow;
                var list = new WorldList
                {
                    Id = Guid.CreateVersion7(),
                    Name = name,
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.WorldLists.Add(list);
                db.WorldListItems.AddRange(Items(list.Id, worlds));
                await NoteWorldsAsync(db, worlds, now, ct);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.WorldListCreated, list.Id.ToString(), Actor.Of(http), Describe(list.Name, worlds), ct);

                await transaction.CommitAsync(ct);

                return Results.Ok((await ViewsAsync(db, [list], ct))[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("CreateWorldList")
            .WithSummary("Add world list")
            .WithDescription("Save a world list: a name and its worlds, each with the fewest and most players it is for.")
            .Produces<WorldListView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] WorldListRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var list = await db.WorldLists.FirstOrDefaultAsync(l => l.Id == id, ct);
                if (list is null)
                    return Results.NotFound();

                if (Check(body, out var name, out var worlds) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var items = await db.WorldListItems.Where(i => i.ListId == id).OrderBy(i => i.Position).ToListAsync(ct);
                var before = items.Select(i => new WorldListWorldRequest(i.WorldId, i.MinPlayers, i.MaxPlayers)).ToList();

                if (list.Name == name && before.SequenceEqual(worlds))
                    return Results.Ok((await ViewsAsync(db, [list], ct))[0]);

                var now = clock.UtcNow;
                var beforeData = Describe(list.Name, before);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                list.Name = name;
                list.UpdatedAt = now;

                // Replaced whole: a list is a few dozen rows at most, and the shuffle follows the
                // list's worlds by id at its next pick (world lists design §4), not these rows.
                db.WorldListItems.RemoveRange(items);
                await db.SaveChangesAsync(ct);

                db.WorldListItems.AddRange(Items(list.Id, worlds));
                await NoteWorldsAsync(db, worlds, now, ct);
                await db.SaveChangesAsync(ct);

                var ids = worlds.Select(w => w.WorldId).ToHashSet(StringComparer.Ordinal);
                var had = before.Select(w => w.WorldId).ToHashSet(StringComparer.Ordinal);

                await facts.RecordAsync(
                    FactType.WorldListChanged,
                    list.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["name"] = list.Name,
                        ["before"] = beforeData,
                        ["after"] = Describe(list.Name, worlds),
                        ["added"] = ids.Count(w => !had.Contains(w)),
                        ["removed"] = had.Count(w => !ids.Contains(w)),
                    },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok((await ViewsAsync(db, [list], ct))[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("UpdateWorldList")
            .WithSummary("Update world list")
            .WithDescription(
                "Rename a world list or change its worlds and their players. A world taken out is never "
                + "picked again; a world added joins the current round of each event's shuffle.")
            .Produces<WorldListView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                var list = await db.WorldLists.FirstOrDefaultAsync(l => l.Id == id, ct);
                if (list is null)
                    return Results.NotFound();

                var used = await db.CalendarEvents.AnyAsync(
                    e => e.WorldListId == id
                        && e.DeletedAt == null
                        && (e.State == CalendarEventStates.Draft
                            || e.State == CalendarEventStates.Scheduled
                            || e.State == CalendarEventStates.Open),
                    ct);

                if (used)
                    return Results.Conflict(new { error = "An event still picks from this list." });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.WorldLists.Remove(list);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.WorldListDeleted, list.Id.ToString(), Actor.Of(http), new JsonObject { ["name"] = list.Name }, ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("DeleteWorldList")
            .WithSummary("Delete world list")
            .WithDescription(
                "Delete a world list. Refused while a draft, scheduled or open event picks from it. "
                + "Finished and cancelled events keep their last world.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>
    /// The world in a pasted link or id, read from VRChat when Modbot has never read it.
    /// </summary>
    internal static async Task<IResult> FindAsync(
        ModbotContext db, IVRChatGate gate, IModbotClock clock, string? text, CancellationToken ct)
    {
        if (WorldLinks.WorldIdFrom(text) is not { } worldId)
            return Results.BadRequest(new { error = "That link has no world in it." });

        var row = await db.VRChatWorlds.FirstOrDefaultAsync(w => w.WorldId == worldId, ct);

        if (row is { LastRefreshedAt: not null } or { RefreshError: not null })
            return Results.Ok(new CalendarWorldView(row.WorldId, row.Name, row.ThumbnailImageUrl));

        var now = clock.UtcNow;
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.WorldsRead, worldId, "GetWorld");

        var result = await gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Worlds.GetWorldWithHttpInfoAsync(worldId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);

        if (!result.Success && result.StatusCode == StatusCodes.Status404NotFound)
            return Results.NotFound(new { error = "VRChat has no world with that id." });

        if (row is null)
        {
            row = new VRChatWorld { WorldId = worldId, FirstSeenAt = now, LastSeenAt = now };
            db.VRChatWorlds.Add(row);
        }

        // Anything but an answer -- a 429, a sign-in wait, no network -- keeps the world by its id;
        // the world sweep names it on its next pass. Never retried here (spec 4.3.1).
        if (result.Success && result.Value is { } world)
        {
            WorldSnapshot.From(world).ApplyTo(row, now);
            row.Platforms ??= "[]";
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new CalendarWorldView(row.WorldId, row.Name, row.ThumbnailImageUrl));
    }

    /// <summary>Checks a request and cleans it up. Returns what is wrong, or null.</summary>
    internal static string? Check(WorldListRequest body, out string name, out List<WorldListWorldRequest> worlds)
    {
        name = body.Name?.Trim() ?? string.Empty;
        worlds = [];

        if (name.Length == 0)
            return "A list needs a name.";

        if (name.Length > WorldList.MaxNameLength)
            return $"The name is longer than {WorldList.MaxNameLength} characters.";

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var world in body.Worlds ?? [])
        {
            var id = world.WorldId?.Trim() ?? string.Empty;
            if (id.Length == 0)
                return "A world in the list has no id.";

            if (world.MinPlayers is < 1)
                return "The fewest players must be at least 1.";

            if (world.MaxPlayers is < 1)
                return "The most players must be at least 1.";

            if (world.MinPlayers is { } min && world.MaxPlayers is { } max && max < min)
                return "The most players cannot be fewer than the fewest.";

            if (seen.Add(id))
                worlds.Add(new WorldListWorldRequest(id, world.MinPlayers, world.MaxPlayers));
        }

        if (worlds.Count > WorldList.MaxWorlds)
            return $"A list can hold at most {WorldList.MaxWorlds} worlds.";

        return null;
    }

    private static IEnumerable<WorldListItem> Items(Guid listId, IReadOnlyList<WorldListWorldRequest> worlds) =>
        worlds.Select((w, i) => new WorldListItem
        {
            ListId = listId,
            WorldId = w.WorldId,
            MinPlayers = w.MinPlayers,
            MaxPlayers = w.MaxPlayers,
            Position = i,
        });

    /// <summary>
    /// Gives each world Modbot has no row for a placeholder holding its id, which the world sweep
    /// names on its next pass.
    /// </summary>
    private static async Task NoteWorldsAsync(
        ModbotContext db, IReadOnlyList<WorldListWorldRequest> worlds, DateTimeOffset now, CancellationToken ct)
    {
        var ids = worlds.Select(w => w.WorldId).ToList();
        var known = await db.VRChatWorlds.AsNoTracking()
            .Where(w => ids.Contains(w.WorldId))
            .Select(w => w.WorldId)
            .ToListAsync(ct);

        foreach (var id in ids.Except(known, StringComparer.Ordinal))
            db.VRChatWorlds.Add(new VRChatWorld { WorldId = id, FirstSeenAt = now, LastSeenAt = now });
    }

    private static JsonObject Describe(string name, IReadOnlyList<WorldListWorldRequest> worlds) => new()
    {
        ["name"] = name,
        ["worlds"] = new JsonArray([.. worlds.Select(w => (JsonNode)new JsonObject
        {
            ["worldId"] = w.WorldId,
            ["minPlayers"] = w.MinPlayers,
            ["maxPlayers"] = w.MaxPlayers,
        })]),
    };

    private static async Task<List<WorldListView>> ViewsAsync(ModbotContext db, IReadOnlyList<WorldList> lists, CancellationToken ct)
    {
        var ids = lists.Select(l => l.Id).ToList();

        var items = await db.WorldListItems.AsNoTracking()
            .Where(i => ids.Contains(i.ListId))
            .OrderBy(i => i.Position)
            .ToListAsync(ct);

        var worldIds = items.Select(i => i.WorldId).Distinct().ToList();
        var worlds = await db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct);

        var uses = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.WorldListId != null && ids.Contains(e.WorldListId.Value) && e.DeletedAt == null)
            .OrderBy(e => e.StartsAt)
            .Select(e => new { ListId = e.WorldListId!.Value, e.Id, e.Title, e.State })
            .ToListAsync(ct);

        return [.. lists.Select(l => new WorldListView(
            l.Id,
            l.Name,
            [.. items
                .Where(i => i.ListId == l.Id)
                .Select(i =>
                {
                    var world = worlds.GetValueOrDefault(i.WorldId);
                    return new WorldListWorldView(i.WorldId, world?.Name, world?.ThumbnailImageUrl, i.MinPlayers, i.MaxPlayers);
                })],
            [.. uses.Where(u => u.ListId == l.Id).Select(u => new WorldListUseView(u.Id, u.Title, u.State))],
            l.CreatedAt,
            l.UpdatedAt))];
    }
}
