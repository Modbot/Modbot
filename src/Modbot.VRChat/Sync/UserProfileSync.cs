using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Users;
using Serilog;
using VRChat.API.Model;

namespace Modbot.VRChat.Sync;

/// <summary>
/// One pass of the profile sync: find anyone new in the fact log, then fetch the profile of
/// whoever the queue says is next.
/// </summary>
/// <remarks>
/// <para>
/// User profile sync design §3. <c>GroupMember</c> carries no profile -- no bio, no status, no
/// avatar, no pronouns, no age verification -- and VRChat hands those out one user at a time on
/// <c>users.read</c>, so a per-user fetch is unavoidable and the only design question is who
/// goes first. That question is answered by <see cref="UserRefreshQueue"/> and its one
/// comparison; this class feeds the queue and spends the requests.
/// </para>
/// <para>
/// <strong>Discovery is incremental.</strong> Each pass reads the facts written since the last
/// one, by id, from a cursor on the settings row. Everyone a fact mentions -- the person it
/// happened to, and the moderator who did it -- gets a row in <c>vrchat_user</c>, created with
/// only the id if they are new. Which facts name a person is decided by fact type, never by
/// looking at the id (spec 3.1.1; see <see cref="UserSightings"/>).
/// </para>
/// <para>
/// <strong>Nothing here retries a 429</strong> (spec 4.3.1). A rate-limited fetch puts the
/// request back where it was and reports the outcome; the service waits out the cold stop. The
/// limiter has already halved the global bucket as well, because a 429 on this lane is evidence
/// the whole model is optimistic (spec 4.2.5).
/// </para>
/// </remarks>
public sealed class UserProfileSync
{
    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly VRChatUserProfiles _profiles;
    private readonly UserRefreshQueue _queue;
    private readonly IModbotClock _clock;
    private readonly SyncDiagnostics _diagnostics;
    private readonly UserProfileSyncOptions _options;
    private readonly ILogger _log;

    /// <summary>
    /// How many answered entries one pass will discard before giving up for this tick. Bounds a
    /// pass that finds the front of the queue full of people refreshed a moment ago.
    /// </summary>
    private const int MaxDroppedPerPass = 20;

    public UserProfileSync(
        IVRChatGate gate,
        ModbotContext db,
        VRChatUserProfiles profiles,
        UserRefreshQueue queue,
        IModbotClock clock,
        SyncDiagnostics diagnostics,
        UserProfileSyncOptions? options = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);

        _gate = gate;
        _db = db;
        _profiles = profiles;
        _queue = queue;
        _clock = clock;
        _diagnostics = diagnostics;
        _options = (options ?? new UserProfileSyncOptions()).Clamped();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <param name="housekeeping">
    /// Whether this pass also re-reads the discovery overlap, tops the queue up from the database
    /// and re-counts the table. The service asks for it once a minute and whenever the queue is
    /// empty; every other pass does only the cheap part.
    /// </param>
    public async Task<UserProfileRunResult> RunOnceAsync(bool housekeeping = true, CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        // The profile sync does not read the group, but until a group is chosen there is no
        // fact log to discover anyone from and no VRChat session to fetch with. Same answer as
        // the other producers: idle, not an error.
        if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
            return new UserProfileRunResult(SyncOutcome.NotConfigured, Message: "no managed group configured");

        var now = _clock.UtcNow;

        var discovered = await DiscoverAsync(settings, now, housekeeping, ct).ConfigureAwait(false);

        if (housekeeping)
        {
            await TopUpAsync(now, ct).ConfigureAwait(false);
            await CountAsync(now, ct).ConfigureAwait(false);
        }

        var result = await RefreshNextAsync(ct).ConfigureAwait(false);

        settings.UserProfilePolledAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return result with { Discovered = discovered };
    }

    /// <summary>
    /// Reads the facts written since the cursor, records a sighting of everyone they mention,
    /// and queues anyone seen recently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Strictly past the cursor on an ordinary pass. The overlap -- a re-read of the last few
    /// hundred ids, for facts that committed after a higher id had already been read past -- runs
    /// with housekeeping only, because the sightings upsert is idempotent but not free, and
    /// re-writing five hundred rows a second to catch a late commit would be the wrong trade.
    /// </para>
    /// <para>
    /// Only sightings inside <see cref="UserProfileSyncOptions.RecentWindow"/> go to the queue.
    /// The one-off audit-log catch-up hands this pass a month of history at once, and a person
    /// who was banned in March is not a fresh sighting -- their row is created and their
    /// <c>last_seen_at</c> recorded, and the top-up finds them in the backlog in due course.
    /// </para>
    /// </remarks>
    private async Task<int> DiscoverAsync(Settings settings, DateTimeOffset now, bool includeOverlap, CancellationToken ct)
    {
        var cursor = settings.UserProfileEventsReadThrough;
        var from = includeOverlap ? Math.Max(0, cursor - _options.DiscoveryOverlap) : cursor;

        var rows = await _db.Events.AsNoTracking()
            .Where(e => e.Id > from)
            .OrderBy(e => e.Id)
            .Take(_options.DiscoveryBatchSize)
            .Select(e => new { e.Id, e.Type, e.SubjectPlatform, e.SubjectId, e.ActorPlatform, e.ActorId, e.OccurredAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return 0;

        var sightings = rows
            .SelectMany(r => UserSightings.From(r.Type, r.SubjectPlatform, r.SubjectId, r.ActorPlatform, r.ActorId, r.OccurredAt))
            .ToList();

        // The cursor moves whether or not anyone was mentioned: a page of group-info facts is
        // still a page read.
        settings.UserProfileEventsReadThrough = Math.Max(cursor, rows[^1].Id);

        if (sightings.Count == 0)
            return 0;

        await _profiles.RecordSeenAsync(sightings, ct).ConfigureAwait(false);

        var recentCutoff = now - _options.RecentWindow;

        // One offer per person: their best reason, keyed on their latest sighting for it.
        var recent = sightings
            .Where(s => s.SeenAt >= recentCutoff)
            .GroupBy(s => s.UserId, StringComparer.Ordinal)
            .Select(g =>
            {
                var best = g.Min(s => (int)s.Reason);
                return new RefreshRequest(
                    g.Key,
                    (RefreshReason)best,
                    Key: g.Where(s => (int)s.Reason == best).Max(s => s.SeenAt),
                    RequestedAt: now);
            })
            .ToList();

        if (recent.Count > 0)
        {
            var ids = recent.Select(r => r.UserId).ToArray();

            var known = await _db.VRChatUsers.AsNoTracking()
                .Where(u => ids.Contains(u.UserId))
                .Select(u => new { u.UserId, u.LastRefreshedAt, u.NotFoundAt })
                .ToDictionaryAsync(u => u.UserId, StringComparer.Ordinal, ct)
                .ConfigureAwait(false);

            var notFoundCutoff = now - _options.RetryNotFoundAfter;

            foreach (var request in recent)
            {
                known.TryGetValue(request.UserId, out var row);

                // Already answered by a refresh since the sighting, or a 404 too recent to ask
                // about again. Either way the request would be a wasted token.
                if (request.IsAnsweredBy(row?.LastRefreshedAt))
                    continue;

                if (row?.NotFoundAt is { } gone && gone > notFoundCutoff)
                    continue;

                _queue.Offer(request, row?.LastRefreshedAt, now, _options);
            }
        }

        return sightings.Select(s => s.UserId).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>
    /// Feeds the queue the people the database says are due: seen since their last refresh, old,
    /// or never fetched. A batch per tier; the queue's own order decides between them.
    /// </summary>
    /// <remarks>
    /// The database supplies candidates and nothing more. Each tier's query is ordered the way
    /// that tier is ordered so that its batch holds the right people, but the decision of who
    /// goes next -- across tiers, and against the presence sightings and moderator requests that
    /// never touched the database -- is made once, by <see cref="RefreshOrder"/>.
    /// </remarks>
    private async Task TopUpAsync(DateTimeOffset now, CancellationToken ct)
    {
        var recentCutoff = now - _options.RecentWindow;
        var staleCutoff = now - _options.StaleAfter;
        var notFoundCutoff = now - _options.RetryNotFoundAfter;
        var errorCutoff = now - _options.RetryFailedUserAfter;
        var take = _options.TopUpBatchSize;

        var eligible = _db.VRChatUsers.AsNoTracking()
            .Where(u => (u.NotFoundAt == null || u.NotFoundAt <= notFoundCutoff)
                     && (u.RefreshErrorAt == null || u.RefreshErrorAt <= errorCutoff));

        var seen = await eligible
            .Where(u => u.LastSeenAt >= recentCutoff
                     && (u.LastRefreshedAt == null || u.LastRefreshedAt < u.LastSeenAt))
            .OrderByDescending(u => u.LastSeenAt).ThenBy(u => u.UserId)
            .Take(take)
            .Select(u => new { u.UserId, u.LastSeenAt, u.LastRefreshedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // "Old" is either older than the stale window, or refreshed before the person was last
        // seen doing something -- a ban two hours ago is worth a look before six hours are up,
        // even if the sighting itself has aged out of the recent window.
        var old = await eligible
            .Where(u => u.LastRefreshedAt != null
                     && (u.LastRefreshedAt <= staleCutoff || u.LastRefreshedAt < u.LastSeenAt))
            .OrderBy(u => u.LastRefreshedAt).ThenBy(u => u.UserId)
            .Take(take)
            .Select(u => new { u.UserId, u.LastSeenAt, u.LastRefreshedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var never = await eligible
            .Where(u => u.LastRefreshedAt == null)
            .OrderByDescending(u => u.LastSeenAt).ThenBy(u => u.UserId)
            .Take(take)
            .Select(u => new { u.UserId, u.LastSeenAt, u.LastRefreshedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var u in seen)
            _queue.Offer(new RefreshRequest(u.UserId, RefreshReason.SeenInFactLog, u.LastSeenAt, now), null, now, _options);

        foreach (var u in old)
            _queue.Offer(new RefreshRequest(u.UserId, RefreshReason.ProfileIsOld, u.LastRefreshedAt!.Value, now), null, now, _options);

        foreach (var u in never)
            _queue.Offer(new RefreshRequest(u.UserId, RefreshReason.NeverRefreshed, u.LastSeenAt, now), null, now, _options);
    }

    private async Task CountAsync(DateTimeOffset now, CancellationToken ct)
    {
        var users = _db.VRChatUsers.AsNoTracking();

        _diagnostics.RecordUserProfileCounts(new UserProfileCounts(
            await users.CountAsync(ct).ConfigureAwait(false),
            await users.CountAsync(u => u.LastRefreshedAt == null, ct).ConfigureAwait(false),
            await users.CountAsync(u => u.NotFoundAt != null, ct).ConfigureAwait(false),
            await users.MinAsync(u => u.LastRefreshedAt, ct).ConfigureAwait(false),
            now));
    }

    /// <summary>Takes the next request the queue has, skips it if it is already answered, and spends one token on it.</summary>
    private async Task<UserProfileRunResult> RefreshNextAsync(CancellationToken ct)
    {
        var dropped = 0;

        while (dropped <= MaxDroppedPerPass)
        {
            var request = _queue.TakeNext();
            if (request is null)
                return new UserProfileRunResult(SyncOutcome.Quiet, Dropped: dropped, Message: "nobody waiting");

            var row = await _db.VRChatUsers.AsNoTracking()
                .Where(u => u.UserId == request.UserId)
                .Select(u => new { u.LastRefreshedAt })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            // The row can be gone -- a purge -- or refreshed since the request was queued. Both
            // mean the token would be spent learning nothing.
            if (row is null || request.IsAnsweredBy(row.LastRefreshedAt))
            {
                _queue.Finish(request);
                dropped++;
                continue;
            }

            var result = await FetchAsync(request, ct).ConfigureAwait(false);
            return result with { Dropped = dropped };
        }

        return new UserProfileRunResult(
            SyncOutcome.Quiet, Dropped: dropped, Message: "already refreshed");
    }

    private async Task<UserProfileRunResult> FetchAsync(RefreshRequest request, CancellationToken ct)
    {
        var userId = request.UserId;

        // users.read: its own lane, its own budget, exempt from the global ceiling (spec 4.2.5).
        // Not resource-scoped -- the limit is on the endpoint, not on the person asked about --
        // so no id is attached; a per-user bucket would be a row in rate_limit_bucket for every
        // member of the group.
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.UsersRead, Operation: "GetUser");

        var result = await _gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Users.GetUserWithHttpInfoAsync(userId, token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (!result.Success)
            return await FailedAsync(request, result, ct).ConfigureAwait(false);

        if (result.Value is not { } user)
        {
            await _profiles.RecordRefreshFailedAsync(userId, "VRChat returned no user body", ct).ConfigureAwait(false);
            _queue.Finish(request);

            return new UserProfileRunResult(
                SyncOutcome.Quiet, UserId: userId, Reason: request.Reason, Refreshed: true,
                Message: "VRChat returned no user body");
        }

        // The body as it arrived is the record; the typed object is a reading of it. The SDK's
        // own serialisation is the fallback for a gate that had no body to hand over.
        var raw = VRChatUserSnapshot.ParseRaw(result.RawResponse);
        if (raw is null || raw.Count == 0)
            raw = VRChatUserSnapshot.ParseRaw(user.ToJson());

        var snapshot = VRChatUserSnapshot.From(user, raw);
        var recorded = await _profiles.RecordProfileAsync(snapshot, raw, ct).ConfigureAwait(false);

        _queue.Finish(request);

        if (recorded.FirstSeen)
            _log.Debug("Recorded {UserId}'s profile for the first time", userId);
        else if (recorded.Changed.Count > 0)
            _log.Debug("{UserId}'s profile changed: {Fields}", userId, string.Join(", ", recorded.Changed));

        return new UserProfileRunResult(
            recorded.WroteAnything ? SyncOutcome.Produced : SyncOutcome.Quiet,
            UserId: userId,
            Reason: request.Reason,
            Refreshed: true,
            FirstSeen: recorded.FirstSeen,
            Changed: recorded.Changed,
            AgeVerifiedObserved: recorded.AgeVerifiedObserved);
    }

    private async Task<UserProfileRunResult> FailedAsync(
        RefreshRequest request,
        VRChatResult<User> result,
        CancellationToken ct)
    {
        var userId = request.UserId;

        if (result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
        {
            // Never retried, and not logged as an error: a cold stop is the design working
            // (spec 4.3.1). The request goes back to the front of its tier -- it was not spent.
            _queue.PutBack(request);

            _log.Information(
                "Profile sync is paused: {Reason}",
                result.ErrorMessage ?? "the users.read lane is cold-stopped");

            return new UserProfileRunResult(
                SyncOutcome.RateLimited, UserId: userId, Reason: request.Reason, Message: result.ErrorMessage);
        }

        if (result.StatusCode == (int)HttpStatusCode.NotFound)
        {
            // A fact about the person, not a failure of the pass: the account is gone, or the id
            // never named one. Marked, recorded once, and left alone for a long while.
            const string detail = "VRChat has no account with this id.";

            await _profiles.RecordNotFoundAsync(userId, detail, ct).ConfigureAwait(false);
            _queue.Finish(request);

            _log.Information("VRChat has no user {UserId}; the row is marked and will not be asked about for a while", userId);

            return new UserProfileRunResult(
                SyncOutcome.Produced, UserId: userId, Reason: request.Reason, Refreshed: true, NotFound: true, Message: detail);
        }

        // A failure that says something about the deployment rather than the person -- no
        // session, a WAF block, the network -- stops the pass so the service backs off. One that
        // says something about the person -- a 403, a 500 for this id -- is written on their row
        // and the lane moves on.
        var aboutThePerson = result.Kind == VRChatFailureKind.Other && result.StatusCode >= 400;
        var detailText = result.ErrorMessage ?? $"VRChat returned {result.StatusCode}";

        _queue.Finish(request);

        if (!aboutThePerson)
        {
            _log.Warning("Profile sync could not reach VRChat: {Status} {Reason}", result.StatusCode, detailText);

            return new UserProfileRunResult(
                SyncOutcome.Failed, UserId: userId, Reason: request.Reason, Message: detailText);
        }

        await _profiles.RecordRefreshFailedAsync(userId, detailText, ct).ConfigureAwait(false);

        _log.Warning("Profile sync could not read {UserId}: {Status} {Reason}", userId, result.StatusCode, detailText);

        return new UserProfileRunResult(
            SyncOutcome.Quiet, UserId: userId, Reason: request.Reason, Refreshed: true, Message: detailText);
    }
}
