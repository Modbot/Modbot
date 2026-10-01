using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Audit;

/// <summary>
/// One visit: when they were seen arriving (or already there), when they left, and what was seen
/// in between.
/// </summary>
/// <param name="Arrived">
/// The fact that made them present: an arrival, or "already here" when a moderator's client arrived
/// after they did. Named and placed like any audit log entry, instance included.
/// </param>
/// <param name="Left">
/// The leave, or null when nobody saw them go -- or when the leave was seen but its entry has since
/// gone (pruned between the two reads); <paramref name="SeenLeaving"/> tells the two apart.
/// </param>
/// <param name="SeenLeaving">Whether a leave ended the visit, rather than the instance going quiet or closing.</param>
/// <param name="Until">
/// When they left, or, when nobody saw them go, the last thing any client reported from that
/// instance in that instance's own life: the last moment anything is known.
/// </param>
/// <param name="Name">The display name they had when they arrived, as the client read it.</param>
/// <param name="Avatars">The avatars they were seen changing into, in order, each once.</param>
/// <param name="SeenBy">
/// Whose clients reported the arrival or the leave: the moderators who were in the instance with
/// them. Empty for facts no client reported.
/// </param>
public sealed record PersonVisit(
    AuditEntry Arrived,
    AuditEntry? Left,
    bool SeenLeaving,
    DateTimeOffset Until,
    string? Name,
    IReadOnlyList<string> Avatars,
    IReadOnlyList<AuditReporter> SeenBy);

/// <param name="Next">Send back as <c>beforeStartedAt</c> and <c>beforeId</c> for the next page.</param>
public sealed record PersonVisitsPage(IReadOnlyList<PersonVisit> Visits, AuditCursor? Next, DateTimeOffset Now);

/// <summary>
/// Where one person has been seen, as visits rather than as separate arrivals and leaves.
/// </summary>
/// <remarks>
/// <para>
/// The person popup's Activity tab already lists every arrival and leave, but a moderator asking
/// "how long were they there, and who was with them" had to pair them by eye, often across a page
/// of other people's entries. The pairing follows the time-in-world figures' rule
/// (<see cref="PresenceCounts"/>), kept inside the life of each instance, because VRChat hands an
/// instance's number out again once it closes.
/// </para>
/// <para>
/// <strong>Read as the audit log is read.</strong> Every visit is made of presence facts, which are
/// moderation entries, so the gate is the one those entries already have: somebody who may not read
/// an arrival in the log may not read it here either. The moderators named as having seen them are
/// the reporters the log already names on the same entries (who is watching design §4).
/// </para>
/// </remarks>
public static class PersonVisits
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;

    /// <summary>How many avatar names a visit lists. A person flicking through a menu for a minute is not twenty facts worth reading.</summary>
    public const int MaxAvatars = 10;

    public static IEndpointRouteBuilder MapPersonVisits(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/audit/visits", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] string? person,
                [FromQuery] DateTimeOffset? beforeStartedAt,
                [FromQuery] long? beforeId,
                [FromQuery] int? limit,
                CancellationToken ct) =>
            {
                var held = ModbotAuth.PermissionsOf(http.User);

                if (!AuditVisibility.CanSeeType(held, FactType.InstanceJoined))
                    return Results.Forbid();

                if (string.IsNullOrWhiteSpace(person))
                    return Results.BadRequest(new { error = "person is required." });

                // Both halves or neither, as the audit log's own cursor: half of one would skip every
                // visit that began in the same second as the page edge.
                (DateTimeOffset, long)? before = beforeStartedAt is { } at && beforeId is { } id ? (at, id) : null;

                return Results.Ok(await ReadAsync(
                    db,
                    person.Trim(),
                    before,
                    Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit),
                    clock.UtcNow,
                    ct));
            })
            .RequireAuthorization()
            .WithTags("Audit")
            .WithName("GetPersonVisits")
            .WithSummary("List a person's visits")
            .WithDescription(
                "Where one VRChat user has been seen, a visit per row, newest first: the arrival "
                + "(or \"already here\" when a moderator's companion arrived after them), the leave "
                + "when one was seen, `until` (when they left, or the last report from that instance "
                + "when nobody saw them go), the display name they had, the avatars they were seen "
                + "changing into, and `seenBy`, the moderators whose companions reported them. "
                + "Visits are made from the companion's presence reports exactly as the time-in-world "
                + "figures are, so they only cover time a moderator was in the same instance.\n\n"
                + "Needs ViewAuditLog, the permission that reads the same arrivals and leaves in the "
                + "audit log. Paged by keyset: send the returned `next` back as beforeStartedAt and "
                + "beforeId; both are required.")
            .Produces<PersonVisitsPage>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    public static async Task<PersonVisitsPage> ReadAsync(
        ModbotContext db,
        string userId,
        (DateTimeOffset At, long Id)? before,
        int limit,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // One more than asked for, so whether there is another page comes from the same read.
        var found = (await new PresenceCounts(db).VisitsOfPersonAsync(userId, before, limit + 1, ct)).ToList();

        var hasMore = found.Count > limit;
        if (hasMore)
            found.RemoveAt(found.Count - 1);

        if (found.Count == 0)
            return new PersonVisitsPage([], null, now);

        var earliest = found.Min(v => v.Started);
        var latest = found.Max(v => v.Ended);

        var ids = found.Select(v => v.StartedBy)
            .Concat(found.Where(v => v.EndedBy is not null).Select(v => v.EndedBy!.Value))
            .ToList();

        // Bounded by time as well as by id, so the read touches the partitions the visits fell in
        // rather than every partition the table has.
        var facts = await db.Events.AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.OccurredAt >= earliest && e.OccurredAt <= latest)
            .ToListAsync(ct);

        var named = (await AuditNaming.ResolveAsync(db, facts.Select(AuditQuery.Project).ToList(), ct))
            .ToDictionary(e => e.Id);

        var changes = await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.AvatarChanged
                && e.SubjectPlatform == FactPlatform.VRChat
                && e.SubjectId == userId
                && e.OccurredAt >= earliest
                && e.OccurredAt <= latest
                && e.WorldId != null
                && e.InstanceId != null)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Select(e => new { e.WorldId, e.InstanceId, e.OccurredAt, e.Data })
            .ToListAsync(ct);

        var avatars = changes
            .Select(c => (c.WorldId, c.InstanceId, c.OccurredAt, Name: AuditJson.Text(AuditJson.Parse(c.Data), "avatarName")))
            .Where(c => c.Name is not null)
            .ToList();

        var visits = new List<PersonVisit>();

        foreach (var visit in found)
        {
            // A visit whose first fact has gone -- pruned between the two reads -- is left out
            // rather than shown with nothing to say where it was.
            if (!named.TryGetValue(visit.StartedBy, out var arrived))
                continue;

            var left = visit.EndedBy is { } endedBy ? named.GetValueOrDefault(endedBy) : null;

            var worn = avatars
                .Where(a => a.WorldId == visit.WorldId
                    && a.InstanceId == visit.InstanceId
                    && a.OccurredAt >= visit.Started
                    && a.OccurredAt <= visit.Ended)
                .Select(a => a.Name!)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxAvatars)
                .ToList();

            var seenBy = (arrived.ReportedBy ?? [])
                .Concat(left?.ReportedBy ?? [])
                .DistinctBy(r => r.AccountId)
                .ToList();

            visits.Add(new PersonVisit(
                arrived,
                left,
                visit.EndedBy is not null,
                visit.Ended,
                AuditJson.Text(arrived.Data, "displayName"),
                worn,
                seenBy));
        }

        var last = found[^1];
        var next = hasMore ? new AuditCursor(last.Started, last.StartedBy) : null;

        return new PersonVisitsPage(visits, next, now);
    }
}
