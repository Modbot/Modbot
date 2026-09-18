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
/// One pass of the profile sync: find anyone new in the fact log, read the public profile of
/// whoever the queue says is next, and read the full user object of anybody due one.
/// </summary>
/// <remarks>
/// <para>
/// User profile sync design §3. <c>GroupMember</c> carries no profile -- no bio, no status, no
/// avatar, no pronouns, no age verification -- and VRChat hands those out one person at a time,
/// so a per-person fetch is unavoidable and the only design question is who goes first. That
/// question is answered by <see cref="UserRefreshQueue"/> and its one comparison; this class
/// feeds the queue and spends the requests.
/// </para>
/// <para>
/// <strong>Two reads, not one</strong> (spec 4.2.5, revised 2026-09-15). The public profile
/// (<c>users.profile</c>) is the main read and carries the bio, the pronouns, the name and the age
/// verification. The full user object (<c>users.read</c>) carries the status line, the join date,
/// the tag list and the pictures, none of which changes fast enough to be worth asking about more
/// than once a week. They have their own budgets and their own lanes, so neither starves the other
/// and a cold stop on one leaves the other running.
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
            await TopUpAsync(settings.ManagedGroupId, now, ct).ConfigureAwait(false);
            await CountAsync(now, ct).ConfigureAwait(false);
        }

        var result = await RefreshNextAsync(ct).ConfigureAwait(false);

        // The two reads are separate budgets on separate lanes (spec 4.3.4, answered 2026-09-15),
        // so the rare one runs in the same pass rather than waiting for the frequent one to be
        // idle. Almost every pass finds nobody due and spends nothing.
        //
        // When the profile read has just been told there is no such account, that person goes to
        // the front of the rare read: they are the one person whose answer decides something, and
        // asking now means the pass can settle it rather than leaving them marked missing until
        // some later pass gets round to them.
        var askAbout = result.NotFound ? result.UserId : null;
        var userRead = await ReadUserIfDueAsync(askAbout, ct).ConfigureAwait(false);

        // Only now, with both calls done, is it decided whether anybody is really gone. Doing it
        // inside either call would write a fact the other call's success could not take back.
        await SettleMissingAsync(result, userRead, ct).ConfigureAwait(false);

        settings.UserProfilePolledAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return result with { Discovered = discovered, UserRead = userRead };
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
    /// <para>
    /// The database supplies candidates and nothing more. Each tier's query is ordered the way
    /// that tier is ordered so that its batch holds the right people, but the decision of who
    /// goes next -- across tiers, and against the presence sightings and moderator requests that
    /// never touched the database -- is made once, by <see cref="RefreshOrder"/>.
    /// </para>
    /// <para>
    /// <strong>The two periodic tiers only cover the people they are for</strong> (spec §3.1,
    /// narrowed 2026-09-18). <c>vrchat_user</c> holds a row for everyone Modbot has ever seen
    /// anywhere, so an unnarrowed "old profile" query queued a stranger who passed through one
    /// instance a year ago for a refresh forever. They are now current group members plus anyone
    /// seen inside <see cref="UserProfileSyncOptions.RefreshNonMembersFor"/>. The on-demand tiers
    /// are untouched: a sighting in an instance or a moderator opening somebody still fetches
    /// them at once, whoever they are.
    /// </para>
    /// </remarks>
    private async Task TopUpAsync(string? groupId, DateTimeOffset now, CancellationToken ct)
    {
        var recentCutoff = now - _options.RecentWindow;
        var staleCutoff = now - _options.StaleAfter;
        var notFoundCutoff = now - _options.RetryNotFoundAfter;
        var errorCutoff = now - _options.RetryFailedUserAfter;
        var keepRefreshingCutoff = now - _options.RefreshNonMembersFor;
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

        // Who the periodic tiers are for: the group's current members, and anyone else Modbot has
        // seen recently enough that their profile is still worth keeping up to date.
        var members = _db.GroupMembers.AsNoTracking()
            .Where(m => m.GroupId == groupId && m.LeftAt == null)
            .Select(m => m.UserId);

        var worthRefreshing = eligible
            .Where(u => u.LastSeenAt >= keepRefreshingCutoff || members.Contains(u.UserId));

        // "Old" is either older than the stale window, or refreshed before the person was last
        // seen doing something -- a ban two hours ago is worth a look before six hours are up,
        // even if the sighting itself has aged out of the recent window.
        var old = await worthRefreshing
            .Where(u => u.LastRefreshedAt != null
                     && (u.LastRefreshedAt <= staleCutoff || u.LastRefreshedAt < u.LastSeenAt))
            .OrderBy(u => u.LastRefreshedAt).ThenBy(u => u.UserId)
            .Take(take)
            .Select(u => new { u.UserId, u.LastSeenAt, u.LastRefreshedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var never = await worthRefreshing
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
            now,
            await users.CountAsync(u => u.LastUserReadAt == null, ct).ConfigureAwait(false),
            await users.MinAsync(u => u.LastUserReadAt, ct).ConfigureAwait(false)));
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

        // users.profile: its own lane, its own budget at the same rate as users.read, exempt from
        // the global ceiling (spec 4.2.5; the rate is the maintainer's, 2026-09-15). Not
        // resource-scoped -- the limit is on the endpoint, not on the person asked about -- so no
        // id is attached; a per-user bucket would be a row in rate_limit_bucket for every member
        // of the group.
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.UsersProfile, Operation: "GetPublicProfile");

        var result = await _gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Users.GetPublicProfileWithHttpInfoAsync(userId, cancellationToken: token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (!result.Success)
            return await FailedAsync(request, result, ct).ConfigureAwait(false);

        if (result.Value is not { } profile)
        {
            await _profiles.RecordRefreshFailedAsync(
                userId, VRChatReadKind.PublicProfile, "VRChat returned no profile body", ct).ConfigureAwait(false);
            _queue.Finish(request);

            return new UserProfileRunResult(
                SyncOutcome.Quiet, UserId: userId, Reason: request.Reason, Refreshed: true,
                Message: "VRChat returned no profile body");
        }

        // The body as it arrived is the record; the typed object is a reading of it. The SDK's
        // own serialisation is the fallback for a gate that had no body to hand over.
        var raw = VRChatUserSnapshot.ParseRaw(result.RawResponse);
        if (raw is null || raw.Count == 0)
            raw = VRChatUserSnapshot.ParseRaw(profile.ToJson());

        var snapshot = VRChatUserSnapshot.FromPublicProfile(userId, profile, raw);
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

    private async Task<UserProfileRunResult> FailedAsync<T>(
        RefreshRequest request,
        VRChatResult<T> result,
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
                result.ErrorMessage ?? "the users.profile lane is cold-stopped");

            return new UserProfileRunResult(
                SyncOutcome.RateLimited, UserId: userId, Reason: request.Reason, Message: result.ErrorMessage);
        }

        if (result.StatusCode == (int)HttpStatusCode.NotFound)
        {
            // A fact about the person, not a failure of the pass: the account is gone, or the id
            // never named one. Only this call is marked; whether the person is really missing is
            // settled at the end of the pass, once the user read has had its turn at them too
            // (research: vrchat-public-profile-findings.md §6).
            const string detail = VRChatUserProfiles.AccountGone;

            await _profiles.RecordNotFoundAsync(userId, VRChatReadKind.PublicProfile, detail, ct).ConfigureAwait(false);
            _queue.Finish(request);

            _log.Information("VRChat has no public profile for {UserId}", userId);

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

        await _profiles.RecordRefreshFailedAsync(
            userId, VRChatReadKind.PublicProfile, detailText, ct).ConfigureAwait(false);

        _log.Warning("Profile sync could not read {UserId}: {Status} {Reason}", userId, result.StatusCode, detailText);

        return new UserProfileRunResult(
            SyncOutcome.Quiet, UserId: userId, Reason: request.Reason, Refreshed: true, Message: detailText);
    }

    /// <summary>
    /// Reads the full user object for one person, when somebody is due one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rare half of the profile sync. A person is due when their user object has never been
    /// read, or was read longer ago than <see cref="UserProfileSyncOptions.ReadUserEvery"/> --
    /// seven days. What this call carries that the public profile does not barely changes, and
    /// what does change fast (the status line, the avatar pictures) is not something Modbot
    /// decides anything from, so a week is enough (research:
    /// <c>vrchat-public-profile-findings.md</c> §5).
    /// </para>
    /// <para>
    /// No queue and no tiers: there is nothing to order, because "due" is a week-wide window and
    /// nobody is waiting on the answer. The oldest goes first, which on a new deployment means
    /// everybody, once, as fast as the lane allows.
    /// </para>
    /// </remarks>
    /// <param name="askAbout">
    /// Somebody the public profile has just been told does not exist. They go first when they are
    /// due a read at all, because their answer is the one that decides whether the person is
    /// really gone -- and that decision is made at the end of this pass.
    /// </param>
    private async Task<UserReadRunResult?> ReadUserIfDueAsync(string? askAbout, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var dueBefore = now - _options.ReadUserEvery;
        var errorCutoff = now - _options.RetryFailedUserAfter;
        var missingCutoff = now - _options.RetryNotFoundAfter;

        // Gated on this call's own marks, not on the row's "missing" flag. A person the public
        // profile cannot find is exactly the person worth asking the user endpoint about: if it
        // answers, they are not missing at all.
        var due = _db.VRChatUsers.AsNoTracking()
            .Where(u => (u.LastUserReadAt == null || u.LastUserReadAt <= dueBefore)
                     && (u.UserReadErrorAt == null || u.UserReadErrorAt <= errorCutoff)
                     && (u.UserNotFoundAt == null || u.UserNotFoundAt <= missingCutoff));

        var next = askAbout is null
            ? null
            : await due.Where(u => u.UserId == askAbout).Select(u => u.UserId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        next ??= await due
            .OrderBy(u => u.LastUserReadAt).ThenBy(u => u.UserId)
            .Select(u => u.UserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (next is null)
            return null;

        var started = _clock.UtcNow;
        var result = await ReadUserAsync(next, ct).ConfigureAwait(false);

        _diagnostics.RecordUserRead(
            new SyncRunReport(result.Outcome, started, _clock.UtcNow - started, DescribeUserRead(result)),
            result.Read);

        return result;
    }

    private async Task<UserReadRunResult> ReadUserAsync(string userId, CancellationToken ct)
    {
        // users.read: its own lane, its own budget, exempt from the global ceiling (spec 4.2.5).
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.UsersRead, Operation: "GetUser");

        var result = await _gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Users.GetUserWithHttpInfoAsync(userId, token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
        {
            // Never retried (spec 4.3.1). Nothing is written and nothing is lost: the person is
            // still due, and the next pass that finds the lane open reads them.
            return new UserReadRunResult(
                SyncOutcome.RateLimited, UserId: userId,
                Message: result.ErrorMessage ?? "the users.read lane is cold-stopped");
        }

        if (result.StatusCode == (int)HttpStatusCode.NotFound)
        {
            // Marks the user read and nothing else. The public profile may still be answering for
            // this person, and if it is they are not missing.
            const string detail = VRChatUserProfiles.AccountGone;

            await _profiles.RecordNotFoundAsync(userId, VRChatReadKind.User, detail, ct).ConfigureAwait(false);

            return new UserReadRunResult(
                SyncOutcome.Produced, UserId: userId, Read: true, NotFound: true, Message: detail);
        }

        if (!result.Success || result.Value is not { } user)
        {
            var detail = result.ErrorMessage ?? $"VRChat returned {result.StatusCode}";

            // A failure about the deployment rather than the person -- no session, a WAF block,
            // the network -- is not written on the row: the frequent read reports those, and
            // stamping every person Modbot happened to try would hide the real ones.
            var aboutThePerson = result.Kind == VRChatFailureKind.Other && result.StatusCode >= 400;

            if (aboutThePerson || result.Success)
            {
                await _profiles.RecordRefreshFailedAsync(
                    userId, VRChatReadKind.User, result.Success ? "VRChat returned no user body" : detail, ct)
                    .ConfigureAwait(false);

                return new UserReadRunResult(
                    SyncOutcome.Quiet, UserId: userId, Read: true, Message: detail);
            }

            return new UserReadRunResult(SyncOutcome.Failed, UserId: userId, Message: detail);
        }

        var raw = VRChatUserSnapshot.ParseRaw(result.RawResponse);
        if (raw is null || raw.Count == 0)
            raw = VRChatUserSnapshot.ParseRaw(user.ToJson());

        var snapshot = VRChatUserSnapshot.From(user, raw);
        var recorded = await _profiles.RecordProfileAsync(snapshot, raw, ct).ConfigureAwait(false);

        if (recorded.Changed.Count > 0)
            _log.Debug("{UserId}'s user object changed: {Fields}", userId, string.Join(", ", recorded.Changed));

        return new UserReadRunResult(
            recorded.WroteAnything ? SyncOutcome.Produced : SyncOutcome.Quiet,
            UserId: userId,
            Read: true,
            Changed: recorded.Changed,
            AgeVerifiedObserved: recorded.AgeVerifiedObserved);
    }

    /// <summary>
    /// Decides, once per pass and only after both calls have had their turn, whether anybody this
    /// pass was told "no such account" about is really gone.
    /// </summary>
    /// <remarks>
    /// The reason this is a step of its own rather than part of either call: each call commits its
    /// own row write, so a 404 that recorded the decision on the spot would write a fact that the
    /// other call's success a moment later could not take back. Facts are never rewritten, so a
    /// person whose public profile 404s once would stay recorded as missing forever.
    /// </remarks>
    private async Task SettleMissingAsync(
        UserProfileRunResult profile,
        UserReadRunResult? userRead,
        CancellationToken ct)
    {
        var gone = new List<string>(2);

        if (profile is { NotFound: true, UserId: { } fromProfile })
            gone.Add(fromProfile);

        if (userRead is { NotFound: true, UserId: { } fromUserRead })
            gone.Add(fromUserRead);

        if (gone.Count > 0)
            await _profiles.SettleMissingAsync(gone, ct).ConfigureAwait(false);
    }

    private static string DescribeUserRead(UserReadRunResult result)
    {
        var who = result.UserId is null ? string.Empty : $" {result.UserId}";

        var what = result switch
        {
            { NotFound: true } => "not found on VRChat",
            { Changed.Count: > 0 } => $"changed: {string.Join(", ", result.Changed)}",
            { Read: true, Message: { } message } => message,
            { Read: true } => "unchanged",
            _ => result.Message ?? result.Outcome.ToString(),
        };

        return $"{what}{who}";
    }
}
