using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Api.Auth;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Places;

/// <summary>
/// One world and one room, for the popup that opens when somebody clicks either.
/// </summary>
/// <remarks>
/// <para>
/// Every field comes from Modbot's own tables — <c>vrchat_world</c>, <c>vrchat_instance</c> and
/// the fact log. <strong>Nothing here calls VRChat.</strong> A world page is read once by the
/// world sweep when the id is first seen; these endpoints show what that read stored, so opening
/// a popup costs no API budget however many times a moderator does it (foundation section 4.3.4).
/// </para>
/// <para>
/// <strong>The world id travels as a query parameter, the room id as a path segment.</strong> A
/// VRChat world id is opaque text and a legacy one is arbitrary (spec 3.1.1) — a slash inside one
/// would break a route, and a route constraint would be exactly the format check that rule
/// forbids. A room's id is Modbot's own <c>Guid</c>, made here, so it is safe in a path and
/// constrained like one.
/// </para>
/// <para>
/// <strong>Every parameter is explicitly attributed.</strong> Minimal APIs bind an unattributed
/// concrete type as the request body, and on a GET that throws while the route is being mapped
/// and takes every other endpoint in the host with it.
/// </para>
/// </remarks>
public static class PlacesEndpoints
{
    /// <summary>How many rooms a world popup lists. Enough to read, not a log.</summary>
    public const int RoomsListed = 25;

    /// <summary>How many facts a room's log carries before it says there were more.</summary>
    public const int FactsListed = 100;

    public static IEndpointRouteBuilder MapPlaces(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var worlds = app.MapGroup("/api/worlds").WithTags("Places").RequireAuthorization();

        worlds.MapGet("/", async (
                [FromQuery] string id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "id is required." });

                return Results.Ok(await WorldAsync(id, db, clock.UtcNow, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetWorld")
            .WithSummary("One world: what its page said, the rooms that have run in it, and how busy it was")
            .WithDescription(
                "Read from Modbot's own tables; nothing here calls VRChat. `known` is false when "
                + "Modbot has only ever seen the id. `name` is null when the world page has not "
                + "been read yet — ordinary for a few minutes after a new world turns up, and "
                + "permanent for a private or deleted one. Time and visitors come from the desktop "
                + "client's presence reports and exist only while a moderator's client was in the "
                + "room; rooms opened come from the group's own instance list and are complete.")
            .Produces<WorldView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        var rooms = app.MapGroup("/api/instances").WithTags("Places").RequireAuthorization();

        rooms.MapGet("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var view = await RoomAsync(id, ModbotAuth.PermissionsOf(http.User), db, clock.UtcNow, ct);

                return view is null ? Results.NotFound() : Results.Ok(view);
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetInstance")
            .WithSummary("One room: where it was, when, how busy, who was in it and what happened there")
            .WithDescription(
                "The id is Modbot's own, not VRChat's number — VRChat hands the same number out "
                + "again after a room closes, so two evenings under one number are two rooms.\n\n"
                + "Who was in the room and the facts recorded there need ViewAuditLog as well: "
                + "that is moderation history (spec 5.9.4), while the room's own shape is not. "
                + "Without it `canSeeWhoWasThere` is false and both lists are empty.")
            .Produces<InstanceView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<WorldView> WorldAsync(
        string worldId,
        ModbotContext db,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var row = await db.VRChatWorlds.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorldId == worldId, ct);

        var counts = await new PresenceCounts(db).ForWorldAsync(worldId, ct);

        var all = db.VRChatInstances.AsNoTracking().Where(i => i.WorldId == worldId);

        var roomsTotal = await all.CountAsync(ct);
        var roomsOpen = await all.CountAsync(i => i.ClosedAt == null, ct);

        // Newest first: "what ran in here lately" is the question a moderator opens this with.
        var listed = await RoomRows.ReadAsync(
            db, all.OrderByDescending(i => i.OpenedAt).Take(RoomsListed), now, ct);

        var (visitors, opened) = await SeriesAsync(db, worldId, now, ct);

        if (row is null)
        {
            return new WorldView(
                worldId, Known: false,
                null, null, null, null, null, null, null, null, [], null, null, null,
                null, null, null, null,
                counts, listed, roomsTotal, roomsOpen, visitors, opened, now);
        }

        return new WorldView(
            row.WorldId,
            Known: true,
            row.Name,
            row.Description,
            row.AuthorId,
            row.AuthorName,
            row.ImageUrl,
            row.ThumbnailImageUrl,
            row.Capacity,
            row.RecommendedCapacity,
            Tags(row.Tags),
            row.ReleaseStatus,
            row.PublishedAt,
            row.UpdatedAt,
            row.FirstSeenAt,
            row.LastSeenAt,
            row.LastRefreshedAt,
            row.RefreshError,
            counts,
            listed,
            roomsTotal,
            roomsOpen,
            visitors,
            opened,
            now);
    }

    /// <summary>
    /// Visitors and rooms opened per day for one world, from the daily totals.
    /// </summary>
    /// <remarks>
    /// The same two metrics the Worlds page charts, read the same way, so the popup and the page
    /// cannot disagree. Over all of recorded history rather than a window: a popup has no date
    /// picker, and the daily totals are one small row per world per day.
    /// </remarks>
    private static async Task<(IReadOnlyList<DayValue> Visitors, IReadOnlyList<DayValue> Rooms)> SeriesAsync(
        ModbotContext db,
        string worldId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var today = AnalyticsSql.DayOf(now);
        var first = await AnalyticsCoverageQuery.FirstDayAsync(db, ct) ?? today;

        var rows = await new AnalyticsSql(db).DailyTotalsAsync(
            first, today, [DailyTotalMetrics.WorldVisitors, DailyTotalMetrics.WorldInstances], ct);

        List<DayValue> Series(string metric) => rows
            .Where(r => r.Metric == metric && string.Equals(r.Dimension, worldId, StringComparison.Ordinal))
            .OrderBy(r => r.Day)
            .Select(r => new DayValue(r.Day, r.Value))
            .ToList();

        return (Series(DailyTotalMetrics.WorldVisitors), Series(DailyTotalMetrics.WorldInstances));
    }

    private static async Task<InstanceView?> RoomAsync(
        Guid id,
        ModbotPermissions held,
        ModbotContext db,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var row = await db.VRChatInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (row is null)
            return null;

        var listed = await RoomRows.ReadAsync(db, db.VRChatInstances.AsNoTracking().Where(i => i.Id == id), now, ct);
        var world = await db.VRChatWorlds.AsNoTracking()
            .Where(w => w.WorldId == row.WorldId)
            .Select(w => new { w.AuthorName, w.ImageUrl, w.Capacity })
            .FirstOrDefaultAsync(ct);

        // The stretch this room was open. Presence facts are keyed on the world and VRChat's
        // number, which is reused, so without these bounds another evening's people would be
        // counted as this one's.
        var endsAt = row.ClosedAt ?? row.LastSeenAt;

        var canSee = ModbotAuth.Allows(held, ModbotPermissions.ViewAuditLog);

        var counts = PlaceCounts.Nothing;
        IReadOnlyList<PersonSeen> people = [];
        IReadOnlyList<AuditEntry> log = [];
        var truncated = false;

        if (canSee && row.VRChatInstanceId is { Length: > 0 } number)
        {
            var room = new Room(row.WorldId, number, row.OpenedAt, endsAt);
            var presence = new PresenceCounts(db);

            counts = await presence.ForRoomAsync(room, ct);
            people = await WithNamesAsync(db, await presence.PeopleInRoomAsync(room, ct), ct);
            (log, truncated) = await LogAsync(db, held, room, ct);
        }

        return new InstanceView(
            listed[0],
            Known: true,
            world?.AuthorName,
            world?.ImageUrl,
            world?.Capacity,
            row.Type,
            row.GroupId,
            row.LastSeenAt,
            row.SeenInGroupList,
            counts,
            canSee,
            people,
            log,
            truncated,
            now);
    }

    /// <summary>
    /// Every fact recorded in this room while it was open, newest first.
    /// </summary>
    /// <remarks>
    /// Narrowed by <see cref="AuditVisibility"/> like any other read of the log, and bounded to
    /// the room's own open and close times so a reissued number cannot drag another evening's
    /// facts in.
    /// </remarks>
    private static async Task<(IReadOnlyList<AuditEntry> Log, bool Truncated)> LogAsync(
        ModbotContext db,
        ModbotPermissions held,
        Room room,
        CancellationToken ct)
    {
        var visible = AuditVisibility.VisibleTypes(held);
        if (visible.Count == 0)
            return ([], false);

        var rows = await db.Events.AsNoTracking()
            .Where(e => visible.Contains(e.Type)
                && e.WorldId == room.WorldId
                && e.InstanceId == room.Number
                && e.OccurredAt >= room.OpenedAt
                && e.OccurredAt <= room.EndsAt)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(FactsListed + 1)
            .ToListAsync(ct);

        var truncated = rows.Count > FactsListed;
        if (truncated)
            rows.RemoveAt(rows.Count - 1);

        var entries = rows.Select(AuditQuery.Project).ToList();

        return (await AuditNaming.ResolveAsync(db, entries, ct), truncated);
    }

    /// <summary>Puts stored display names to the people seen, in one lookup rather than one each.</summary>
    private static async Task<IReadOnlyList<PersonSeen>> WithNamesAsync(
        ModbotContext db,
        IReadOnlyList<PersonSeen> people,
        CancellationToken ct)
    {
        if (people.Count == 0)
            return people;

        var ids = people.Select(p => p.UserId).Distinct(StringComparer.Ordinal).ToList();

        var names = await db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.DisplayName != null)
            .Select(u => new { u.UserId, u.DisplayName })
            .ToDictionaryAsync(u => u.UserId, u => u.DisplayName!, StringComparer.Ordinal, ct);

        return people
            .Select(p => p with { DisplayName = names.GetValueOrDefault(p.UserId) })
            .ToList();
    }

    private static IReadOnlyList<string> Tags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
