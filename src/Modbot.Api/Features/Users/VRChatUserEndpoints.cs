using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;

namespace Modbot.Api.Features.Users;

/// <summary>
/// One VRChat user's stored profile, a way to ask for it to be refreshed, and the moderator's
/// control over the 18+ flag.
/// </summary>
/// <remarks>
/// <para>
/// The id travels as a query parameter rather than a path segment. VRChat ids are opaque and
/// legacy ones are arbitrary text (spec 3.1.1) -- a slash inside one would break a route and a
/// route constraint is exactly the format check the rule forbids.
/// </para>
/// <para>
/// <strong>Every parameter is explicitly attributed</strong>, for the reason the audit endpoints
/// give: an unattributed concrete type on a GET is bound as the body and throws while the route
/// is mapped.
/// </para>
/// <para>
/// The sync's pieces -- the queue and the record writer -- resolve optionally, so a host that
/// mapped the API without registering the producers still answers reads, and says plainly that a
/// refresh cannot be arranged here rather than failing to resolve a service mid-request.
/// </para>
/// </remarks>
public static class VRChatUserEndpoints
{
    public static IEndpointRouteBuilder MapVRChatUsers(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/vrchat-users").WithTags("VRChat users").RequireAuthorization();

        group.MapGet("/profile", async (
                [FromQuery] string id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IVRChatGate gate,
                [FromServices] UserRefreshQueue? queue,
                [FromServices] UserProfileSyncOptions? options,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "id is required." });

                return Results.Ok(await ProfileAsync(id, db, clock, gate, queue, options, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("GetVRChatUserProfile")
            .WithSummary("What Modbot has stored about one VRChat user, and how old it is")
            .WithDescription(
                "The profile as of `lastRefreshedAt`, never fresher. `stale` says whether it is "
                + "older than the sync's own threshold. `eighteenPlus` is Modbot's sticky flag: "
                + "true once the person has ever been seen as 18+ verified, cleared only by a "
                + "moderator -- it can disagree with `ageVerificationStatusLastSeen`, and that is "
                + "the point. `refresh.pending` is true while a refresh is queued or in flight; "
                + "poll this endpoint until `lastRefreshedAt` moves or `refresh.pending` clears.")
            .Produces<VRChatUserProfile>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/metrics", async (
                [FromQuery] string id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "id is required." });

                var now = clock.UtcNow;
                var counts = await new PresenceCounts(db).ForPersonAsync(id, ct);

                // The rooms they were seen in, newest first. Matched on the room's own open and
                // close times rather than on VRChat's number alone, which is handed out again.
                var rooms = await RoomsSeenInAsync(db, id, now, ct);

                return Results.Ok(new PersonMetrics(id, counts.Arrivals > 0, counts, rooms, now));
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("GetVRChatUserMetrics")
            .WithSummary("How long this person has been seen in world, where, and how often")
            .WithDescription(
                "Computed from the desktop client's presence reports, so it only covers time a "
                + "moderator's client was in the same room. Somebody who has never shared a room "
                + "with the client reads as nothing here, which is not the same as never having "
                + "been in one — and the screen says so.\n\n"
                + "Gated on ViewProfile rather than ViewAnalytics: this is one person's own record "
                + "rather than an aggregate, and it sits beside the rest of their profile.")
            .Produces<PersonMetrics>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/refresh", async (
                [FromQuery] string id,
                [FromServices] VRChatUserProfiles? profiles,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "id is required." });

                if (profiles is null)
                {
                    return Results.Ok(new RefreshRequestResult(
                        "NotAvailable", null,
                        "The profile sync is not running in this process, so nothing can be refreshed from here."));
                }

                var asked = await profiles.RequestRefreshAsync(id, RefreshReason.OpenedInModbot, ct);

                return Results.Ok(new RefreshRequestResult(
                    asked.Outcome.ToString(),
                    asked.LastRefreshedAt,
                    asked.Outcome switch
                    {
                        RefreshRequestOutcome.FreshEnough =>
                            "Fresh enough: this profile was fetched a moment ago, so it is shown as stored.",
                        RefreshRequestOutcome.AlreadyQueued => "Already waiting for a refresh.",
                        RefreshRequestOutcome.Promoted => "Moved up the queue.",
                        _ => "Queued. The profile is refreshed as soon as the users lane gets to it.",
                    }));
            })
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("RequestVRChatUserRefresh")
            .WithSummary("Ask for this person's profile to be refreshed as soon as possible")
            .WithDescription(
                "Queues one request at the 'opened in Modbot' tier, behind only people seen in an "
                + "instance right now. Answers immediately; the refresh happens on the users lane at "
                + "its own pace. A profile fetched within the fresh-enough gap is not queued again, "
                + "so a screen may call this on every open without spending the lane.")
            .Produces<RefreshRequestResult>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/age-verified", async (
                HttpContext http,
                [FromQuery] string id,
                [FromBody] SetAgeVerifiedRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IVRChatGate gate,
                [FromServices] VRChatUserProfiles? profiles,
                [FromServices] UserRefreshQueue? queue,
                [FromServices] UserProfileSyncOptions? options,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Results.BadRequest(new { error = "id is required." });

                if (profiles is null)
                    return Results.Problem("User records are not available in this process.", statusCode: 503);

                // An override with no author is not an override. The cookie always carries the
                // account id, so this is a guard against a misconfigured host, not a user error.
                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                await profiles.SetAgeFlagAsync(id, body.Verified, actor, body.Reason, ct);

                return Results.Ok(await ProfileAsync(id, db, clock, gate, queue, options, ct));
            })
            .RequiresFlag(ModbotPermissions.EditAgeVerification)
            .WithName("SetVRChatUserAgeVerified")
            .WithSummary("Set or clear the 18+ verified flag by hand")
            .WithDescription(
                "The only way the flag is ever cleared: a sync sets it and never clears it, "
                + "because VRChat users can hide their verification again and hidden is not "
                + "unverified. Recorded as a fact naming the account that made the change and "
                + "carrying the reason given. Setting it again to the value it already has changes "
                + "nothing and records nothing.")
            .Produces<VRChatUserProfile>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>How many rooms a person's Metrics tab lists.</summary>
    public const int RoomsListed = 25;

    /// <summary>
    /// The rooms this person was seen in, newest first.
    /// </summary>
    /// <remarks>
    /// Two steps rather than a join, because the fact log and the room table do not share a key:
    /// facts record the world and VRChat's room number, which is handed out again after a room
    /// closes, so the room is the one whose own life overlaps the stretch this person was seen
    /// there. A person seen under a number outside every room's life gets no row rather than
    /// somebody else's evening.
    /// </remarks>
    private static async Task<IReadOnlyList<InstanceRow>> RoomsSeenInAsync(
        ModbotContext db,
        string userId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var seen = await db.Events.AsNoTracking()
            .Where(e => e.SubjectId == userId
                && AnalyticsSql.PresenceTypes.Contains(e.Type)
                && e.WorldId != null
                && e.InstanceId != null)
            .GroupBy(e => new { e.WorldId, e.InstanceId })
            .Select(g => new
            {
                g.Key.WorldId,
                g.Key.InstanceId,
                First = g.Min(e => e.OccurredAt),
                Last = g.Max(e => e.OccurredAt),
            })
            .ToListAsync(ct);

        if (seen.Count == 0)
            return [];

        var worldIds = seen.Select(s => s.WorldId!).Distinct(StringComparer.Ordinal).ToList();
        var numbers = seen.Select(s => s.InstanceId!).Distinct(StringComparer.Ordinal).ToList();

        var candidates = await db.VRChatInstances.AsNoTracking()
            .Where(i => worldIds.Contains(i.WorldId)
                && i.VRChatInstanceId != null
                && numbers.Contains(i.VRChatInstanceId))
            .Select(i => new { i.Id, i.WorldId, i.VRChatInstanceId, i.OpenedAt, i.ClosedAt, i.LastSeenAt })
            .ToListAsync(ct);

        var ids = candidates
            .Where(c => seen.Any(s =>
                string.Equals(s.WorldId, c.WorldId, StringComparison.Ordinal)
                && string.Equals(s.InstanceId, c.VRChatInstanceId, StringComparison.Ordinal)
                && s.First <= (c.ClosedAt ?? c.LastSeenAt)
                && s.Last >= c.OpenedAt))
            .Select(c => c.Id)
            .ToList();

        if (ids.Count == 0)
            return [];

        return await RoomRows.ReadAsync(
            db,
            db.VRChatInstances.AsNoTracking()
                .Where(i => ids.Contains(i.Id))
                .OrderByDescending(i => i.OpenedAt)
                .Take(RoomsListed),
            now,
            ct);
    }

    private static async Task<VRChatUserProfile> ProfileAsync(
        string id,
        ModbotContext db,
        IModbotClock clock,
        IVRChatGate gate,
        UserRefreshQueue? queue,
        UserProfileSyncOptions? options,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var staleAfter = (options ?? new UserProfileSyncOptions()).StaleAfter;

        var row = await db.VRChatUsers.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == id, ct);
        var refresh = await RefreshStateAsync(id, gate, queue, ct);

        if (row is null)
        {
            return new VRChatUserProfile(
                id, Known: false,
                null, null, null, null, null, null, null, null, null, [], null, null, null,
                new AgeVerifiedFlag(false, null, null, null, null),
                null, null, null,
                Stale: true, staleAfter.TotalSeconds,
                null, null, null,
                refresh, now);
        }

        string? setBy = null;
        if (row.Is18PlusVerifiedByUserId is { } by)
        {
            setBy = await db.Users.AsNoTracking()
                .Where(u => u.Id == by)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(ct);
        }

        return new VRChatUserProfile(
            row.UserId,
            Known: true,
            row.DisplayName,
            row.Bio,
            row.Status,
            row.StatusDescription,
            row.Pronouns,
            row.CurrentAvatarImageUrl,
            row.CurrentAvatarThumbnailImageUrl,
            row.ProfilePictureUrl,
            row.DateJoined,
            Tags(row.Tags),
            row.LastPlatform,
            row.AgeVerificationStatus,
            row.AgeVerified,
            new AgeVerifiedFlag(
                row.Is18PlusVerified,
                row.Is18PlusVerifiedAt,
                row.Is18PlusVerifiedSource,
                row.Is18PlusVerifiedByUserId,
                setBy),
            row.FirstSeenAt,
            row.LastSeenAt,
            row.LastRefreshedAt,
            Stale: row.LastRefreshedAt is not { } refreshed || now - refreshed > staleAfter,
            staleAfter.TotalSeconds,
            row.RefreshError,
            row.RefreshErrorAt,
            row.NotFoundAt,
            refresh,
            now);
    }

    /// <summary>
    /// Whether a refresh is coming, and if not, why not -- the two things a screen showing
    /// "refreshing…" has to be able to stop showing it for.
    /// </summary>
    private static async Task<RefreshState> RefreshStateAsync(
        string id,
        IVRChatGate gate,
        UserRefreshQueue? queue,
        CancellationToken ct)
    {
        if (queue is null)
        {
            return new RefreshState(
                false, null, null, false,
                "The profile sync is not running in this process, so nothing here is refreshed.");
        }

        var pending = queue.PendingFor(id);
        var inProgress = queue.InProgress is { } current && string.Equals(current.UserId, id, StringComparison.Ordinal);

        // The lane's own state, so a cold stop reads as "VRChat is rate limiting Modbot" rather
        // than as a refresh that silently never arrives (spec 4.3.3).
        string? blocked = null;
        var buckets = await gate.DescribeBucketsAsync(ct);
        var lane = buckets.FirstOrDefault(b => b.Name == VRChatEndpointClass.UsersRead);

        if (lane is { IsColdStopped: true })
        {
            blocked = lane.Alerting
                ? "VRChat has rate limited Modbot's profile fetches repeatedly; nothing is fetched until an operator looks at it."
                : "VRChat is rate limiting Modbot right now. Profiles are not being fetched until the cold stop lifts"
                  + (lane.StoppedUntil is { } until ? $" (next try no earlier than {until:HH:mm} UTC)." : ".");
        }

        return new RefreshState(
            pending is not null,
            pending?.Reason.ToString(),
            pending?.RequestedAt,
            inProgress,
            blocked);
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
