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
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Places;

/// <summary>
/// One world and one instance, for the popup that opens when somebody clicks either.
/// </summary>
/// <remarks>
/// <para>
/// Every field comes from Modbot's own tables — <c>vrchat_world</c>, <c>vrchat_instance</c> and
/// the fact log. <strong>Nothing here waits on VRChat.</strong> A world page is read once by the
/// world sweep when the id is first seen; these endpoints show what that read stored, so opening
/// a popup costs no API budget however many times a moderator does it (foundation section 4.3.4).
/// </para>
/// <para>
/// The one exception is the World tab (<c>/api/instances/{id}/world</c>): the first time it lists
/// another group's instance, or its group, that Modbot has never asked about, it hands the name to
/// <see cref="OtherNameQueue"/>, and a background service asks VRChat once and keeps the answer.
/// The endpoint answers straight away with what is kept; the read costs budget once per instance
/// and once per group, however many popups follow.
/// </para>
/// <para>
/// <strong>The world id travels as a query parameter, the instance id as a path segment.</strong> A
/// VRChat world id is opaque text and a legacy one is arbitrary (spec 3.1.1) — a slash inside one
/// would break a route, and a route constraint would be exactly the format check that rule
/// forbids. An instance's id is Modbot's own <c>Guid</c>, made here, so it is safe in a path and
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
    /// <summary>How many instances a world popup lists. Enough to read, not a log.</summary>
    public const int InstancesListed = 25;

    /// <summary>How many facts an instance's log carries before it says there were more.</summary>
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
            .WithSummary("Get world")
            .WithDescription(
                "One world: what its page said, the instances that have run in it, and how busy it "
                + "was. "
                + "Read from Modbot's own tables; nothing here calls VRChat. `known` is false when "
                + "Modbot has only ever seen the id. `name` is null when the world page has not "
                + "been read yet — ordinary for a few minutes after a new world turns up, and "
                + "permanent for a private or deleted one. Time and visitors come from the desktop "
                + "client's presence reports and exist only while a moderator's client was in the "
                + "instance; instances opened come from the group's own instance list and are complete.")
            .Produces<WorldView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        var instances = app.MapGroup("/api/instances").WithTags("Places").RequireAuthorization();

        instances.MapGet("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var view = await InstanceAsync(id, ModbotAuth.PermissionsOf(http.User), db, clock.UtcNow, ct);

                return view is null ? Results.NotFound() : Results.Ok(view);
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetInstance")
            .WithSummary("Get instance")
            .WithDescription(
                "One instance: where it was, when, how busy and who was in it. "
                + "The id is Modbot's own, not VRChat's number — VRChat hands the same number out "
                + "again after an instance closes, so two evenings under one number are two instances. "
                + "What happened there is the audit log, read narrowed to the instance's world and number.\n\n"
                + "Who was in the instance needs ViewAuditLog as well: "
                + "that is moderation history (spec 5.9.4), while the instance's own shape is not. "
                + "Without it `canSeeWhoWasThere` is false, `people` is empty, and so is "
                + "`peoplePresent`: how many members and how many of each trust rank a companion saw, "
                + "moment by moment, which is drawn as lines a moderator can turn on over the head "
                + "count. Each head count reading says whether it went `up`, or down at a `kick` "
                + "recorded about then, or down because somebody `left`.")
            .Produces<InstanceView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        instances.MapGet("/{id:guid}/world", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] OtherNameQueue? names,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                var read = await InstanceWorldQuery.ReadWithUnaskedAsync(db, id, settings.ManagedGroupId, clock.UtcNow, ct);

                if (read is null)
                    return Results.NotFound();

                // Handed to the one service that asks, so this answer never waits on VRChat. A host
                // without the VRChat side registers no queue, and the numbers stand.
                var coming = names is not null && AskForNames(names, read);

                return Results.Ok(coming ? read.View with { NamesComing = true } : read.View);
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetInstanceWorld")
            .WithSummary("Get how an instance compared with its world")
            .WithDescription(
                "How one instance compared with the other instances in its world while it was open: "
                + "where it ranked by head count at each read of the world's page, the busiest of the "
                + "others, and how many people were in the world. "
                + "Read from Modbot's own tables; nothing here waits on VRChat. The world's page is read "
                + "every two minutes while the group has an instance open in that world, so an instance "
                + "that closed before those reads began has no readings. The other instances' numbers "
                + "are the world page's list; this instance's is the list's when the list carries it "
                + "(`listed`), and its own head count otherwise.\n\n"
                + "The first time a shown instance or its group has no name Modbot has asked for, and its "
                + "location does not say outsiders cannot join (invite, friends, friends+, a group's "
                + "members or members and their friends), Modbot asks VRChat for it once, in the background, "
                + "and keeps the answer, a refusal included. `namesComing` is true while such a read is "
                + "waiting; asking again a few seconds later shows what it found.")
            .Produces<InstanceWorldView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Offers the World tab's unasked names to the queue, and says whether any of the shown names is
    /// still on its way -- offered now, or by an earlier popup and not yet read.
    /// </summary>
    internal static bool AskForNames(OtherNameQueue names, InstanceWorldRead read)
    {
        var coming = false;

        foreach (var location in read.UnaskedInstances)
            coming |= names.Offer(new OtherNameRequest(OtherNameKind.Instance, location));

        foreach (var groupId in read.UnaskedGroups)
            coming |= names.Offer(new OtherNameRequest(OtherNameKind.Group, groupId));

        return coming;
    }

    internal static async Task<WorldView> WorldAsync(
        string worldId,
        ModbotContext db,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var row = await db.VRChatWorlds.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorldId == worldId, ct);

        var counts = await new PresenceCounts(db).ForWorldAsync(worldId, ct);

        var all = db.VRChatInstances.AsNoTracking().Where(i => i.WorldId == worldId);

        var instancesTotal = await all.CountAsync(ct);
        var instancesOpen = await all.CountAsync(i => i.ClosedAt == null, ct);

        // Newest first: "what ran in here lately" is the question a moderator opens this with.
        var listed = await InstanceRows.ReadAsync(
            db, all.OrderByDescending(i => i.OpenedAt).Take(InstancesListed), now, ct);

        var (visitors, opened) = await SeriesAsync(db, worldId, now, ct);

        if (row is null)
        {
            return new WorldView(
                worldId, Known: false,
                null, null, null, null, null, null, null, null, [], null, null, null,
                null, null, null, null,
                counts, listed, instancesTotal, instancesOpen, visitors, opened, now);
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
            instancesTotal,
            instancesOpen,
            visitors,
            opened,
            now);
    }

    /// <summary>
    /// Visitors and instances opened per day for one world, from the daily totals.
    /// </summary>
    /// <remarks>
    /// The same two metrics the Worlds page charts, read the same way, so the popup and the page
    /// cannot disagree. Over all of recorded history rather than a window: a popup has no date
    /// picker, and the daily totals are one small row per world per day.
    /// </remarks>
    private static async Task<(IReadOnlyList<DayValue> Visitors, IReadOnlyList<DayValue> Instances)> SeriesAsync(
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

    internal static async Task<InstanceView?> InstanceAsync(
        Guid id,
        ModbotPermissions held,
        ModbotContext db,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var row = await db.VRChatInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (row is null)
            return null;

        var listed = await InstanceRows.ReadAsync(db, db.VRChatInstances.AsNoTracking().Where(i => i.Id == id), now, ct);
        var world = await db.VRChatWorlds.AsNoTracking()
            .Where(w => w.WorldId == row.WorldId)
            .Select(w => new { w.AuthorName, w.ImageUrl, w.Capacity })
            .FirstOrDefaultAsync(ct);

        // The stretch this instance was open. Presence facts are keyed on the world and VRChat's
        // number, which is reused, so without these bounds another evening's people would be
        // counted as this one's.
        var endsAt = row.ClosedAt ?? row.LastSeenAt;

        var canSee = ModbotAuth.Allows(held, ModbotPermissions.ViewAuditLog);

        var counts = PlaceCounts.Nothing;
        var returningMembers = 0;
        IReadOnlyList<PersonSeen> people = [];
        IReadOnlyList<PeoplePresentPoint> present = [];
        IReadOnlyList<DateTimeOffset> kicks = [];

        if (row.VRChatInstanceId is { Length: > 0 } number)
        {
            var instance = new InstanceLife(row.WorldId, number, row.OpenedAt, endsAt);

            // Kicks colour the head count line, which everybody who may open the instance sees: a
            // kick's time says nothing about who was kicked.
            kicks = await KicksAsync(db, instance, ct);

            if (canSee)
            {
                var presence = new PresenceCounts(db);

                counts = await presence.ForInstanceAsync(instance, ct);
                people = await WithNamesAsync(db, await presence.PeopleInInstanceAsync(instance, ct), ct);
                returningMembers = await ReturningMembersAsync(db, people, ct);
                present = await PeoplePresentAsync(db, presence, instance, ct);
            }
        }

        var headCounts = await db.InstanceHeadCounts.AsNoTracking()
            .Where(h => h.InstanceId == id)
            .OrderByDescending(h => h.CountedAt)
            .Take(HeadCountPoint.Most)
            .Select(h => new HeadCountPoint(
                h.CountedAt,
                h.HeadCount,
                h.UserCount,
                h.MemberCount,
                h.Source,
                h.NUsers,
                h.Source == HeadCounts.FromPage && h.UserCount == null))
            .ToListAsync(ct);

        headCounts.Reverse();

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
            returningMembers,
            canSee,
            people,
            now,
            HeadCountChanges.Classify(headCounts, kicks),
            present);
    }

    /// <summary>
    /// When each kick from this instance happened, from the audit log, bounded to the instance's own
    /// stretch with the matching slack on both ends (<see cref="HeadCountChanges"/>).
    /// </summary>
    private static async Task<IReadOnlyList<DateTimeOffset>> KicksAsync(
        ModbotContext db,
        InstanceLife instance,
        CancellationToken ct)
    {
        var from = instance.OpenedAt - HeadCountChanges.Slack;
        var to = instance.EndsAt + HeadCountChanges.Slack;

        return await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.GroupInstanceKick
                && e.WorldId == instance.WorldId
                && e.InstanceId == instance.Number
                && e.OccurredAt >= from
                && e.OccurredAt <= to)
            .Select(e => e.OccurredAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// How many members and how many of each rank a companion saw present, moment by moment. Membership
    /// is in the managed group, and both it and the rank are as Modbot holds them today.
    /// </summary>
    private static async Task<IReadOnlyList<PeoplePresentPoint>> PeoplePresentAsync(
        ModbotContext db,
        PresenceCounts presence,
        InstanceLife instance,
        CancellationToken ct)
    {
        var sessions = await presence.SessionsInInstanceAsync(instance, ct);
        if (sessions.Count == 0)
            return [];

        var ids = sessions.Select(s => s.UserId).Distinct(StringComparer.Ordinal).ToList();

        var ranks = await db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId))
            .Select(u => new { u.UserId, u.TrustRank })
            .ToDictionaryAsync(u => u.UserId, u => u.TrustRank, StringComparer.Ordinal, ct);

        var groupId = (await db.GetSettingsAsync(ct)).ManagedGroupId;
        List<string> members = string.IsNullOrEmpty(groupId)
            ? []
            : await db.GroupMembers.AsNoTracking()
                .Where(m => m.GroupId == groupId && m.LeftAt == null && ids.Contains(m.UserId))
                .Select(m => m.UserId)
                .ToListAsync(ct);

        return PeoplePresentSeries.Build(sessions, members.ToHashSet(StringComparer.Ordinal), ranks);
    }

    /// <summary>
    /// How many of the people seen in an instance are members of the managed group now.
    /// </summary>
    /// <remarks>
    /// Read from the member table as the last sweep left it (<see cref="GroupMember"/>): a row with
    /// no <see cref="GroupMember.LeftAt"/> is a member. The people are the presence sessions' distinct
    /// subjects, so someone who joined twice is one person here as they are in <see cref="PlaceCounts.Visitors"/>.
    /// </remarks>
    private static async Task<int> ReturningMembersAsync(ModbotContext db, IReadOnlyList<PersonSeen> people, CancellationToken ct)
    {
        if (people.Count == 0)
            return 0;

        var settings = await db.GetSettingsAsync(ct);
        if (settings.ManagedGroupId is not { Length: > 0 } groupId)
            return 0;

        var ids = people.Select(p => p.UserId).Distinct(StringComparer.Ordinal).ToList();

        return await db.GroupMembers.AsNoTracking()
            .CountAsync(m => m.GroupId == groupId && m.LeftAt == null && ids.Contains(m.UserId), ct);
    }

    /// <summary>
    /// Every fact recorded in this instance while it was open, newest first, at most
    /// <see cref="FactsListed"/>; empty without <c>ViewAuditLog</c>, like the people.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the chat's <c>get_instance</c> tool only. The instance popup used to carry this list in
    /// <see cref="InstanceView"/>, but its Activity tab now reads the audit log itself, so the view
    /// stopped paying for a query nobody read.
    /// </para>
    /// <para>
    /// Narrowed by <see cref="AuditVisibility"/> like any other read of the log, and bounded to
    /// the instance's own open and close times so a reissued number cannot drag another evening's
    /// facts in.
    /// </para>
    /// </remarks>
    internal static async Task<(IReadOnlyList<AuditEntry> Log, bool Truncated)> InstanceLogAsync(
        ModbotContext db,
        ModbotPermissions held,
        InstanceView view,
        CancellationToken ct)
    {
        var row = view.Instance;
        if (!view.CanSeeWhoWasThere || row.VRChatInstanceId is not { Length: > 0 } number)
            return ([], false);

        var instance = new InstanceLife(row.WorldId, number, row.OpenedAt, row.ClosedAt ?? view.LastSeenAt);
        var visible = AuditVisibility.VisibleTypes(held);
        if (visible.Count == 0)
            return ([], false);

        var rows = await db.Events.AsNoTracking()
            .Where(e => visible.Contains(e.Type)
                && e.WorldId == instance.WorldId
                && e.InstanceId == instance.Number
                && e.OccurredAt >= instance.OpenedAt
                && e.OccurredAt <= instance.EndsAt)
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
