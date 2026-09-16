using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Sync;
using Serilog;

namespace Modbot.VRChat.Users;

/// <summary>What recording a fetched profile did.</summary>
/// <param name="FirstSeen">This was the first profile ever recorded for the user.</param>
/// <param name="Changed">The watched fields that differed from the last recorded profile.</param>
/// <param name="AgeVerifiedObserved">The sticky 18+ flag was set by this observation.</param>
public sealed record ProfileRecorded(bool FirstSeen, IReadOnlyList<string> Changed, bool AgeVerifiedObserved)
{
    public bool WroteAnything => FirstSeen || Changed.Count > 0 || AgeVerifiedObserved;
}

/// <summary>What a moderator's change to the 18+ flag did.</summary>
public enum AgeFlagChange
{
    /// <summary>The flag already had that value. Nothing written.</summary>
    Unchanged,

    /// <summary>Set to true by hand.</summary>
    Set,

    /// <summary>Cleared by hand -- the only way it is ever cleared.</summary>
    Cleared,
}

/// <summary>What a request to refresh one person came back with.</summary>
/// <param name="Outcome">Whether it was queued, promoted, already there, or answered from the stored profile.</param>
/// <param name="LastRefreshedAt">When the stored profile was fetched, for a "fresh enough" answer to explain itself.</param>
public sealed record RefreshRequested(RefreshRequestOutcome Outcome, DateTimeOffset? LastRefreshedAt);

/// <summary>
/// The one place a VRChat user's record is written: sightings, fetched profiles, and the 18+ flag.
/// </summary>
/// <remarks>
/// <para>
/// A small service with a plain name, because more than one caller writes here. The profile sync
/// records the profiles it fetches and the people it discovers; the API records a moderator's
/// decision about the 18+ flag and a moderator's request to refresh; and account linking, when
/// it lands, will record a sighting of the user object it fetched for the linked account. Every
/// one of them has to apply the same sticky-flag rule and write the same facts, so the rule lives
/// once, here.
/// </para>
/// <para>
/// <strong>The sticky rule</strong> (user profile sync design §4): <see cref="RecordProfileAsync"/>
/// may set <see cref="VRChatUser.Is18PlusVerified"/> and may never clear it. Only
/// <see cref="SetAgeFlagAsync"/> clears it, on a moderator's say-so, and it records who.
/// </para>
/// <para>
/// Scoped, like the producers: it holds a <see cref="ModbotContext"/> for the call and the fact
/// writer shares it, so the row and the facts about it land in one <c>SaveChanges</c>.
/// </para>
/// </remarks>
public sealed class VRChatUserProfiles
{
    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly UserRefreshQueue _queue;
    private readonly UserProfileSyncOptions _options;
    private readonly ILogger _log;

    public VRChatUserProfiles(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        UserRefreshQueue queue,
        UserProfileSyncOptions? options = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(queue);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _queue = queue;
        _options = (options ?? new UserProfileSyncOptions()).Clamped();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>
    /// Records that these people were seen: a row for anyone new, and a later
    /// <see cref="VRChatUser.LastSeenAt"/> for anyone known. Nothing is fetched.
    /// </summary>
    /// <remarks>
    /// One statement, upserting, rather than a read-then-write per person. The API can be
    /// creating a row for the same id at the same moment -- a moderator opening a profile the
    /// sync is discovering -- and two inserts racing on a primary key is a failed pass. An
    /// <c>ON CONFLICT</c> is the database settling it.
    /// </remarks>
    public async Task RecordSeenAsync(IReadOnlyCollection<UserSighting> sightings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sightings);

        if (sightings.Count == 0)
            return;

        var byUser = sightings
            .GroupBy(s => s.UserId, StringComparer.Ordinal)
            .Select(g => (UserId: g.Key, First: g.Min(s => s.SeenAt), Last: g.Max(s => s.SeenAt)))
            .ToList();

        var ids = byUser.Select(u => u.UserId).ToArray();
        var firsts = byUser.Select(u => u.First.UtcDateTime).ToArray();
        var lasts = byUser.Select(u => u.Last.UtcDateTime).ToArray();

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO vrchat_user (user_id, first_seen_at, last_seen_at, is_18_plus_verified)
             SELECT id, first_seen, last_seen, false
             FROM unnest({ids}::text[], {firsts}::timestamptz[], {lasts}::timestamptz[]) AS s(id, first_seen, last_seen)
             ON CONFLICT (user_id) DO UPDATE SET
                 first_seen_at = LEAST(vrchat_user.first_seen_at, EXCLUDED.first_seen_at),
                 last_seen_at = GREATEST(vrchat_user.last_seen_at, EXCLUDED.last_seen_at)
             """,
            ct).ConfigureAwait(false);
    }

    /// <summary>One person, seen now. For callers with a single id -- a moderator opening a profile.</summary>
    public Task RecordSeenAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return RecordSeenAsync(
            [new UserSighting(userId, _clock.UtcNow, RefreshReason.SeenInFactLog)], ct);
    }

    /// <summary>
    /// Asks for a refresh of one person, for the reason given, and says what became of the ask.
    /// </summary>
    /// <remarks>
    /// Creates the row if the id is new, so a moderator can look up somebody Modbot has never
    /// recorded and have them refreshed like anyone else. The fresh-enough check is the queue's
    /// own rule, applied here with the row's real timestamp.
    /// </remarks>
    public async Task<RefreshRequested> RequestRefreshAsync(
        string userId,
        RefreshReason reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = _clock.UtcNow;

        var lastRefreshed = await _db.VRChatUsers.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => new { u.LastRefreshedAt, Known = true })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (lastRefreshed is null)
            await RecordSeenAsync(userId, ct).ConfigureAwait(false);

        var outcome = _queue.Offer(
            new RefreshRequest(userId, reason, Key: now, RequestedAt: now),
            lastRefreshed?.LastRefreshedAt,
            now,
            _options);

        return new RefreshRequested(outcome, lastRefreshed?.LastRefreshedAt);
    }

    /// <summary>
    /// Records a profile as fetched: the row's columns, the facts for what changed, and -- if
    /// the profile shows it -- the sticky 18+ flag.
    /// </summary>
    /// <param name="snapshot">The profile as it stands, and which call it came from.</param>
    /// <param name="raw">The response body, when the caller has it. Stored minus the fields Modbot never keeps.</param>
    /// <remarks>
    /// <para>
    /// Only the fields the response carried are written (<see cref="VRChatUserSnapshot.Carried"/>),
    /// and only that call's own timestamps, error and raw copy. Recording a public profile
    /// therefore never clears the join date the user read filled in, and recording a user read
    /// never clears a bio the public profile filled in.
    /// </para>
    /// </remarks>
    public async Task<ProfileRecorded> RecordProfileAsync(
        VRChatUserSnapshot snapshot,
        JsonObject? raw,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = _clock.UtcNow;
        var row = await RowAsync(snapshot.UserId, now, ct).ConfigureAwait(false);

        var previous = VRChatUserSnapshot.FromRow(row);

        // The window a change could have happened in is "since this call last looked", not since
        // the other one did: the other call never sees these fields.
        var previouslyRefreshedAt = snapshot.Source == VRChatReadKind.User
            ? row.LastUserReadAt
            : row.LastRefreshedAt;

        snapshot.ApplyTo(row);

        if (snapshot.Source == VRChatReadKind.User)
        {
            row.RawProfile = VRChatUserSnapshot.StoredJson(raw) ?? row.RawProfile;
            row.LastUserReadAt = now;
            row.UserReadError = null;
            row.UserReadErrorAt = null;
            row.UserNotFoundAt = null;
        }
        else
        {
            row.RawPublicProfile = VRChatUserSnapshot.StoredJson(raw) ?? row.RawPublicProfile;
            row.LastRefreshedAt = now;
            row.RefreshError = null;
            row.RefreshErrorAt = null;
            row.ProfileNotFoundAt = null;
        }

        UpdateMissing(row);

        var changed = new List<string>();
        var firstSeen = previous is null;

        if (previous is null)
        {
            await WriteAsync(
                FactType.UserProfileFirstSeen,
                snapshot.UserId,
                new JsonObject { ["baseline"] = snapshot.Baseline() },
                since: null,
                now,
                ct).ConfigureAwait(false);
        }
        else if (previouslyRefreshedAt is null)
        {
            // This call's first look at a person the other call already recorded. The fields it
            // carries were never seen before, so every one of them would diff from null -- which
            // is a baseline, not a change, and writing it as a change would fill the timeline
            // with "status line changed from nothing" the first time a person is read.
        }
        else
        {
            var diff = snapshot.DifferencesFrom(previous);

            if (diff.Count > 0)
            {
                changed.AddRange(diff.Select(d => d.Key));

                // A refresh knows only that the change happened since the last one. Spec 5.3:
                // a window, never an invented instant.
                await WriteAsync(
                    FactType.UserProfileChanged,
                    snapshot.UserId,
                    new JsonObject { ["changed"] = diff },
                    since: previouslyRefreshedAt,
                    now,
                    ct).ConfigureAwait(false);
            }
        }

        var observed = false;

        // The sticky rule. True may be set here; false is never written here, whatever the
        // profile says today, because "hidden" is not "unverified" (design §4).
        if (snapshot.ShowsEighteenPlus && !row.Is18PlusVerified)
        {
            row.Is18PlusVerified = true;
            row.Is18PlusVerifiedAt = now;
            row.Is18PlusVerifiedSource = AgeVerificationSource.VRChat;
            row.Is18PlusVerifiedByUserId = null;
            observed = true;

            await WriteAsync(
                FactType.UserAgeVerified,
                snapshot.UserId,
                new JsonObject
                {
                    ["ageVerificationStatus"] = snapshot.AgeVerificationStatus,
                    ["ageVerified"] = snapshot.AgeVerified,
                    ["source"] = AgeVerificationSource.VRChat,
                },
                since: null,
                now,
                ct).ConfigureAwait(false);

            _log.Information("Observed {UserId} as 18+ verified; the flag is now set and stays set", snapshot.UserId);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ProfileRecorded(firstSeen, changed, observed);
    }

    /// <summary>The sentence both calls use when VRChat says it has never heard of an id.</summary>
    public const string AccountGone = "VRChat has no account with this id.";

    /// <summary>
    /// One of the two calls answered 404 for this id. The row stays and <em>that call</em> is
    /// marked; nothing here decides whether the person is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The public profile and the user object are different endpoints and can disagree. One of
    /// them 404ing while the other answers is not an account that is gone, so a 404 marks only
    /// the call that gave it (research: <c>vrchat-public-profile-findings.md</c> §6).
    /// </para>
    /// <para>
    /// <strong>The decision is deliberately not made here.</strong> Each call commits on its own,
    /// so a 404 recorded before the other call has had its turn would write a
    /// <c>UserProfileNotFound</c> fact that the other call's success is then unable to take back
    /// -- facts are never rewritten. <see cref="SettleMissingAsync"/> makes the decision once,
    /// after both calls have had their turn at that person.
    /// </para>
    /// </remarks>
    public async Task RecordNotFoundAsync(
        string userId,
        VRChatReadKind kind,
        string detail,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = _clock.UtcNow;
        var row = await RowAsync(userId, now, ct).ConfigureAwait(false);

        if (kind == VRChatReadKind.User)
        {
            row.UserNotFoundAt = now;
            row.UserReadError = detail;
            row.UserReadErrorAt = now;
        }
        else
        {
            row.ProfileNotFoundAt = now;
            row.RefreshError = detail;
            row.RefreshErrorAt = now;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides, once, whether these people are missing, and records the ones that newly are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called at the end of a pass, for everyone either call answered 404 for during it, once both
    /// calls have had their turn. Marking somebody missing is the one thing here that cannot be
    /// undone -- the row's flag holds them out of the queue for a week, and the fact stays in the
    /// timeline forever -- so it is worth being the last thing decided rather than the first.
    /// </para>
    /// <para>
    /// The rule is the one the research sets out: missing when both calls answered 404, or when
    /// one did and the other has never succeeded for that person.
    /// </para>
    /// </remarks>
    public async Task SettleMissingAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        if (userIds.Count == 0)
            return;

        var wroteAnything = false;

        foreach (var userId in userIds.Distinct(StringComparer.Ordinal))
        {
            var row = await _db.VRChatUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct).ConfigureAwait(false);
            if (row is null)
                continue;

            var alreadyMissing = row.NotFoundAt is not null;

            UpdateMissing(row);

            if (alreadyMissing || row.NotFoundAt is not { } missingSince)
                continue;

            // Once per disappearance, not once per week when the retry finds them still gone.
            var detail = (row.ProfileNotFoundAt is not null ? row.RefreshError : row.UserReadError) ?? AccountGone;

            await WriteAsync(
                FactType.UserProfileNotFound,
                userId,
                new JsonObject { ["detail"] = detail },
                since: null,
                missingSince,
                ct).ConfigureAwait(false);

            _log.Information(
                "Neither of VRChat's answers knows {UserId}; the row is marked and will not be asked about for a while",
                userId);

            wroteAnything = true;
        }

        if (wroteAnything || _db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A read failed for a reason that is neither a 404 nor a cold stop. Recorded on the row, no fact.</summary>
    public async Task RecordRefreshFailedAsync(
        string userId,
        VRChatReadKind kind,
        string detail,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = _clock.UtcNow;
        var row = await RowAsync(userId, now, ct).ConfigureAwait(false);

        if (kind == VRChatReadKind.User)
        {
            row.UserReadError = detail;
            row.UserReadErrorAt = now;
        }
        else
        {
            row.RefreshError = detail;
            row.RefreshErrorAt = now;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decides whether the person is missing, from what each call last said.
    /// </summary>
    /// <remarks>
    /// Missing means neither call can find them: both answered 404, or one did and the other has
    /// never succeeded for this person. A success on either call clears that call's own mark, so
    /// two marks standing at once really does mean neither call has answered since.
    /// </remarks>
    private static void UpdateMissing(VRChatUser row)
    {
        row.NotFoundAt = (row.ProfileNotFoundAt, row.UserNotFoundAt) switch
        {
            ({ } profile, { } user) => profile <= user ? profile : user,
            ({ } profile, null) when row.LastUserReadAt is null => profile,
            (null, { } user) when row.LastRefreshedAt is null => user,
            _ => null,
        };
    }

    /// <summary>
    /// A moderator sets or clears the 18+ flag by hand. Recorded as a fact naming them.
    /// </summary>
    /// <param name="byUserId">The Modbot account making the change. Required: an override with no author is not an override.</param>
    /// <param name="reason">Their own words, kept with the fact. Optional, but the screen should ask.</param>
    public async Task<AgeFlagChange> SetAgeFlagAsync(
        string userId,
        bool verified,
        Guid byUserId,
        string? reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = _clock.UtcNow;
        var row = await RowAsync(userId, now, ct).ConfigureAwait(false);

        if (row.Is18PlusVerified == verified)
            return AgeFlagChange.Unchanged;

        var payload = new JsonObject
        {
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            ["previousSource"] = row.Is18PlusVerifiedSource,
            ["previouslySetAt"] = row.Is18PlusVerifiedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["ageVerificationStatusLastSeen"] = row.AgeVerificationStatus,
        };

        row.Is18PlusVerified = verified;
        row.Is18PlusVerifiedAt = now;
        row.Is18PlusVerifiedSource = AgeVerificationSource.Manual;
        row.Is18PlusVerifiedByUserId = byUserId;

        var fact = new FactRecord
        {
            Type = verified ? FactType.UserAgeFlagSet : FactType.UserAgeFlagCleared,
            OccurredAt = now,
            OccurredBefore = null,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = userId,
            ActorPlatform = FactPlatform.Modbot,
            ActorId = byUserId.ToString(),
            Source = FactSource.Manual,
            Data = payload,
        };

        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
        await _facts.WriteAsync(fact, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.Information(
            "{Actor} {Action} the 18+ verified flag on {UserId}",
            byUserId, verified ? "set" : "cleared", userId);

        return verified ? AgeFlagChange.Set : AgeFlagChange.Cleared;
    }

    /// <summary>The tracked row, created with only the id when the person is new.</summary>
    private async Task<VRChatUser> RowAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        var row = await _db.VRChatUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct).ConfigureAwait(false);
        if (row is not null)
            return row;

        row = new VRChatUser { UserId = userId, FirstSeenAt = now, LastSeenAt = now };
        _db.VRChatUsers.Add(row);

        return row;
    }

    /// <summary>
    /// A profile fact: the user is the subject, there is never an actor, and a change carries the
    /// window it could have happened in.
    /// </summary>
    private async Task WriteAsync(
        string type,
        string userId,
        JsonObject data,
        DateTimeOffset? since,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var fact = new FactRecord
        {
            Type = type,
            OccurredAt = since is { } from && from < now ? from : now,
            OccurredBefore = since is { } lower && lower < now ? now : null,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = userId,

            // No actor. VRChat's user object says what the person looks like and never who
            // changed it -- the person themselves, usually, but the object does not say so.
            ActorPlatform = null,
            ActorId = null,

            Source = FactSource.SyncDiff,
            Data = data,
        };

        await _partitions.EnsureForAsync(fact.OccurredAt, ct).ConfigureAwait(false);
        await _facts.WriteAsync(fact, ct).ConfigureAwait(false);
    }
}
