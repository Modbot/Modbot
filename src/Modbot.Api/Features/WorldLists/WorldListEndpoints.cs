using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Calendar;
using Modbot.Api.Features.GroupPage;
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
/// cold-stops the bucket and is never retried; the world is kept by its id for the sweep to name. An
/// answer that refuses the world is shown and nothing is stored. Saving a list only takes in worlds
/// Modbot knows and VRChat has not refused.
/// </para>
/// <para>
/// The other is <see cref="SearchVRChatAsync"/>'s, when a moderator presses Search VRChat for a world
/// Modbot does not know: one <c>SearchWorlds</c> on <c>worlds.search</c>, its own unmeasured bucket,
/// so a 429 there never pauses world naming or head counts. The public worlds it finds are kept as
/// known, unread, for the sweep to fill in.
/// </para>
/// </remarks>
public static class WorldListEndpoints
{
    /// <summary>The most worlds one search returns.</summary>
    public const int SearchLimit = 50;

    /// <summary>The most worlds one VRChat search asks for and shows.</summary>
    public const int VRChatSearchLimit = 10;

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
                "Takes a world's link or id. A world Modbot has never read, or one VRChat refused before, "
                + "is read from VRChat once (worlds.read). When VRChat cannot be asked right now, the world "
                + "comes back by its id with no name, and Modbot names it later. When VRChat refuses it, "
                + "nothing is stored and the refusal comes back in the 404.")
            .Produces<CalendarWorldView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/worlds/search-vrchat", async (
                [FromBody] WorldSearchRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IVRChatGate gate,
                [FromServices] IModbotClock clock,
                [FromServices] WorldSearchWait wait,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);
                return await SearchVRChatAsync(db, gate, clock, wait, body.Text, ct);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("SearchVRChatWorldListWorlds")
            .WithSummary("Search VRChat's public worlds")
            .WithDescription(
                "Asks VRChat for up to 10 public worlds whose name holds the text (worlds.search, one search "
                + "every 10 seconds). Each world found is kept as known, so a list can take it in. When "
                + "searching again too soon, or when VRChat cannot be asked right now, nothing is sent and "
                + "the reason comes back in the error.")
            .Produces<IReadOnlyList<WorldSearchHitView>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

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

                if (await UnknownWorldAsync(db, worlds, new HashSet<string>(), ct) is { } unknown)
                    return Results.BadRequest(new { error = unknown });

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
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.WorldListCreated, list.Id.ToString(), Actor.Of(http), Describe(list.Name, worlds), ct);

                await transaction.CommitAsync(ct);

                return Results.Ok((await ViewsAsync(db, [list], ct))[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("CreateWorldList")
            .WithSummary("Add world list")
            .WithDescription(
                "Save a world list: a name and its worlds, each with the fewest and most players it is for. "
                + "Every world must be one Modbot knows and VRChat has not refused; a world that is not is "
                + "named in the 400.")
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

                var onList = before.Select(w => w.WorldId).ToHashSet(StringComparer.Ordinal);
                if (await UnknownWorldAsync(db, worlds, onList, ct) is { } unknown)
                    return Results.BadRequest(new { error = unknown });

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
                + "picked again; a world added joins the current round of each event's shuffle. A world "
                + "added must be one Modbot knows and VRChat has not refused; a world that is not is named "
                + "in the 400. Worlds already on the list stay.")
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

        if (row is { LastRefreshedAt: not null })
            return Results.Ok(new CalendarWorldView(row.WorldId, row.Name, row.ThumbnailImageUrl));

        var now = clock.UtcNow;
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.WorldsRead, worldId, "GetWorld");

        var result = await gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Worlds.GetWorldWithHttpInfoAsync(worldId, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);

        if (result.IsRefusal)
        {
            // Nothing is stored: a row VRChat refused before stays as the sweep left it.
            var typed = !Uri.TryCreate(text?.Trim(), UriKind.Absolute, out _);

            return Results.NotFound(new
            {
                error = typed
                    ? "No world by that name or id. Paste its link."
                    : result.ErrorMessage ?? $"VRChat returned {result.StatusCode}.",
            });
        }

        if (row is null)
        {
            row = new VRChatWorld { WorldId = worldId, FirstSeenAt = now, LastSeenAt = now };
            db.VRChatWorlds.Add(row);
        }

        // Anything but an answer -- a 429, a sign-in wait, no network, VRChat down -- keeps the world
        // by its id; the world sweep names it on its next pass. Never retried here (spec 4.3.1).
        if (result.Success && result.Value is { } world)
        {
            WorldSnapshot.From(world).ApplyTo(row, now);
            row.Platforms ??= "[]";
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new CalendarWorldView(row.WorldId, row.Name, row.ThumbnailImageUrl));
    }

    /// <summary>
    /// Up to <see cref="VRChatSearchLimit"/> of VRChat's public worlds whose name holds the text, each
    /// kept as a known world. One press is one VRChat call, and never sent again on a 429.
    /// </summary>
    internal static async Task<IResult> SearchVRChatAsync(
        ModbotContext db, IVRChatGate gate, IModbotClock clock, WorldSearchWait wait, string? text, CancellationToken ct)
    {
        var term = text?.Trim();
        if (string.IsNullOrEmpty(term))
            return Results.BadRequest(new { error = "Type a name to search for." });

        var now = clock.UtcNow;

        if (wait.TryStart(now) is { } left && left > TimeSpan.Zero)
        {
            return Results.Json(
                new { error = $"Wait {Math.Ceiling(left.TotalSeconds):0} seconds, then search again." },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var endpoint = new VRChatEndpoint(VRChatEndpointClass.WorldsSearch, Operation: "SearchWorlds");

        var result = await gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Worlds.SearchWorldsWithHttpInfoAsync(
                search: term, n: VRChatSearchLimit, cancellationToken: token),
            VRChatCallPriority.Interactive,
            ct);

        if (!result.Success)
        {
            // VRChat turning the search down is shown in its words; anything else -- a cold stop, a
            // sign-in wait, no account, no network -- is that VRChat cannot be asked right now.
            return Results.Json(
                new { error = result.IsRefusal ? GroupPageAnswers.Said(result) : "VRChat can't be asked right now." },
                statusCode: GroupPageAnswers.StatusFor(result));
        }

        var found = (result.Value ?? [])
            .Where(w => !string.IsNullOrWhiteSpace(w.Id))
            .DistinctBy(w => w.Id, StringComparer.Ordinal)
            .Take(VRChatSearchLimit)
            .ToList();

        var ids = found.Select(w => w.Id).ToList();
        var rows = await db.VRChatWorlds
            .Where(w => ids.Contains(w.WorldId))
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct);

        foreach (var world in found)
        {
            // A search result is not a full read: LastRefreshedAt stays as it was, and the world
            // sweep fills in the rest.
            if (!rows.TryGetValue(world.Id, out var row))
            {
                row = new VRChatWorld { WorldId = world.Id, FirstSeenAt = now, LastSeenAt = now };
                db.VRChatWorlds.Add(row);
            }

            row.Name ??= world.Name;
            row.ImageUrl ??= world.ImageUrl;
            row.ThumbnailImageUrl ??= world.ThumbnailImageUrl;
            row.AuthorId ??= world.AuthorId;
            row.AuthorName ??= world.AuthorName;

            // VRChat just listed it publicly, so an earlier refusal no longer stands.
            row.RefreshError = null;
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(found
            .Select(w => new WorldSearchHitView(w.Id, w.Name, w.ThumbnailImageUrl, w.AuthorName))
            .ToList());
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
    /// What is wrong with the worlds a save adds, or null. A world not already on the list must be one
    /// Modbot has a row for and VRChat has not refused; worlds already on it stay, even ones that went
    /// private since.
    /// </summary>
    private static async Task<string?> UnknownWorldAsync(
        ModbotContext db, IReadOnlyList<WorldListWorldRequest> worlds, ISet<string> onList, CancellationToken ct)
    {
        var added = worlds.Select(w => w.WorldId).Where(id => !onList.Contains(id)).ToList();
        if (added.Count == 0)
            return null;

        var known = await db.VRChatWorlds.AsNoTracking()
            .Where(w => added.Contains(w.WorldId))
            .Select(w => new { w.WorldId, w.Name, w.LastRefreshedAt, w.RefreshError })
            .ToDictionaryAsync(w => w.WorldId, StringComparer.Ordinal, ct);

        foreach (var id in added)
        {
            if (!known.TryGetValue(id, out var row))
                return $"Modbot does not know the world {id}. Add it with its link.";

            // A world that was read once is real, whatever the sweep's later errors say.
            if (row.RefreshError is not null && row.LastRefreshedAt is null)
                return $"VRChat refused the world {row.Name ?? id}.";
        }

        return null;
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
