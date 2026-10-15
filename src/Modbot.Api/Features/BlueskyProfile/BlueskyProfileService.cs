using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Posts;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.BlueskyProfile;

/// <summary>
/// What the profile page last read, and when Modbot last saved: kept for one process, so a page opened
/// again within <see cref="BlueskyProfileService.CallGap"/> is answered without asking Bluesky.
/// </summary>
public sealed class BlueskyProfileMemory
{
    private readonly object _gate = new();
    private Answer? _read;
    private DateTimeOffset? _savedAt;

    /// <summary>What one read of the profile answered.</summary>
    /// <param name="Actor">Who was asked about: the DID, or the handle before there is one.</param>
    /// <param name="At">When it was asked.</param>
    /// <param name="Profile">The profile, or null when the read failed.</param>
    /// <param name="Problem">Why it failed, in a sentence, when it did.</param>
    public sealed record Answer(string Actor, DateTimeOffset At, BlueskyAccountProfile? Profile, string? Problem);

    /// <summary>The last answer, when it is about <paramref name="actor"/> and younger than <paramref name="within"/>.</summary>
    public Answer? Fresh(string actor, DateTimeOffset now, TimeSpan within)
    {
        lock (_gate)
        {
            return _read is { } last && last.Actor == actor && now >= last.At && now - last.At < within ? last : null;
        }
    }

    /// <summary>The last answer about <paramref name="actor"/>, however old; null for none.</summary>
    public Answer? Last(string actor)
    {
        lock (_gate)
        {
            return _read is { } last && last.Actor == actor ? last : null;
        }
    }

    /// <summary>Keeps an answer.</summary>
    public void Keep(Answer answer)
    {
        lock (_gate)
        {
            _read = answer;
        }
    }

    /// <summary>
    /// Whether the last save was less than <paramref name="gap"/> ago. When it was not, <paramref name="now"/>
    /// is kept as the time of this one, in the same step, so two saves at once do not both go through.
    /// </summary>
    public bool SaveTooSoon(DateTimeOffset now, TimeSpan gap)
    {
        lock (_gate)
        {
            if (_savedAt is { } last && now >= last && now - last < gap)
                return true;

            _savedAt = now;
            return false;
        }
    }
}

/// <summary>What a save did.</summary>
/// <param name="Status">The HTTP status to answer with: 200 when it went through.</param>
/// <param name="Error">What went wrong, in a sentence. Null when it went through.</param>
/// <param name="Before">The profile record as it was, when it was read.</param>
/// <param name="After">The profile record as written, when it went through.</param>
public sealed record BlueskyProfileSave(int Status, string? Error, JsonObject? Before = null, JsonObject? After = null)
{
    public bool Done => Error is null;
}

/// <summary>
/// The group's Bluesky account as a profile page: reading it, and saving its name, bio, picture and
/// banner (Bluesky profile page design, posts design §4.2g).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reading</strong> asks Bluesky's public API (<c>getProfile</c>, no sign-in), which is the only
/// place the follower, follow and post numbers are. The answer is kept for <see cref="CallGap"/>, so a
/// page opened again at once asks nothing. A rate limit stops the reading until the time Bluesky says
/// (<c>settings.bluesky_profile_stopped_until</c>) and is never tried again (CLAUDE.md, §4.3.1).
/// </para>
/// <para>
/// <strong>Saving</strong> is a read-modify-write of the profile record on the account's own server: the
/// record is read, each picture sent with <c>uploadBlob</c>, the four fields replaced and the rest kept
/// as it was (a pinned post, labels, anything added later), and the record written with <c>putRecord</c>
/// and <c>swapRecord</c> set to the content id read, so a profile saved by somebody meanwhile is not
/// written over. A 429 stops the account's lane (<see cref="BlueskySession.StopAsync"/>) and is not
/// tried again. A failed save is kept in <c>settings.bluesky_profile_error</c> for Health and the page
/// until a save goes through.
/// </para>
/// <para>
/// <strong>Rate limit: NOT MEASURED</strong> (CLAUDE.md §4.3.4: not in the foundation spec's table,
/// <c>.agent/research/</c> or <c>RateLimitOptions.cs</c>, which holds VRChat's lanes only; no <c>libs/</c>
/// clone has it). Placeholder, the one used for similar write lanes: one call every
/// <see cref="CallGap"/> to the public API, and one save every <see cref="CallGap"/>.
/// </para>
/// </remarks>
public sealed class BlueskyProfileService
{
    /// <summary>The least time between two reads of the profile, and between two saves.</summary>
    public static readonly TimeSpan CallGap = TimeSpan.FromSeconds(10);

    /// <summary>The refusal for a save made less than <see cref="CallGap"/> after the last.</summary>
    public const string SaveTooSoon = "Wait a few seconds, then save again.";

    /// <summary>The words for an account that is not set up.</summary>
    public const string NotSetUp = "Bluesky is not set up.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly BlueskyClient _client;
    private readonly BlueskySession _session;
    private readonly BlueskyProfileMemory _memory;
    private readonly ILogger _log;

    public BlueskyProfileService(
        ModbotContext db, IModbotClock clock, BlueskyClient client, BlueskySession session, BlueskyProfileMemory memory, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(memory);

        _db = db;
        _clock = clock;
        _client = client;
        _session = session;
        _memory = memory;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The profile page: read from Bluesky unless it was read within <see cref="CallGap"/>.</summary>
    public async Task<BlueskyProfileView> ReadAsync(bool canEdit, CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        var actor = ActorOf(settings);

        if (settings is null || actor is null)
            return NotSetUpView(canEdit);

        var now = _clock.UtcNow;

        if (settings.BlueskyProfileStoppedUntil is { } stopped && now < stopped)
            return ViewOf(settings, canEdit, null, BlueskyErrors.Limited, now);

        var answer = _memory.Fresh(actor, now, CallGap);

        if (answer is null)
        {
            var read = await _client.AccountProfileAsync(actor, ct).ConfigureAwait(false);

            if (read.Failure is { StopsTheLane: true } limited)
            {
                var until = BlueskyErrors.StopUntil(limited, now);

                await _db.Settings
                    .Where(s => s.Id == 1 && (s.BlueskyProfileStoppedUntil == null || s.BlueskyProfileStoppedUntil < until))
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyProfileStoppedUntil, until), ct).ConfigureAwait(false);

                _log.Warning("Bluesky is limiting the reading of the profile; it is not asked for again until the limit resets");
                var stoppedSettings = await _db.Settings.AsNoTracking().FirstAsync(s => s.Id == 1, ct).ConfigureAwait(false);
                return ViewOf(stoppedSettings, canEdit, null, BlueskyErrors.Limited, now);
            }

            // The sentence for the account's server would be wrong here: this call goes to Bluesky's public API.
            var problem = read.Failure is { } failure
                ? failure.Problem == BlueskyProblem.Unavailable ? PostBlueskyCounts.CouldNotReach : BlueskyErrors.Sentence(failure)
                : null;

            answer = new BlueskyProfileMemory.Answer(actor, now, read.Value, problem);
            _memory.Keep(answer);
        }

        return ViewOf(settings, canEdit, answer.Profile, answer.Problem, now);
    }

    /// <summary>
    /// Saves the parts of the profile that are set: reads the record, uploads the pictures, writes the
    /// record back with only those four fields changed. Stops at the first failure and keeps it for Health.
    /// </summary>
    /// <param name="name">The new name; empty clears it; null keeps it.</param>
    /// <param name="bio">The new bio; empty clears it; null keeps it.</param>
    /// <param name="avatar">The new round picture and its type, or null to keep it.</param>
    /// <param name="banner">The new wide picture and its type, or null to keep it.</param>
    public async Task<BlueskyProfileSave> SaveAsync(
        string? name, string? bio, (byte[] Bytes, string ContentType)? avatar, (byte[] Bytes, string ContentType)? banner, CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);

        if (settings is not { BlueskyDid: { Length: > 0 } did }
            || (string.IsNullOrEmpty(settings.BlueskyAppPasswordEncrypted) && !settings.BlueskyOAuthSignedIn))
        {
            return new BlueskyProfileSave(StatusCodes.Status400BadRequest, NotSetUp);
        }

        var now = _clock.UtcNow;

        if (_memory.SaveTooSoon(now, CallGap))
            return new BlueskyProfileSave(StatusCodes.Status429TooManyRequests, SaveTooSoon);

        var signIn = await _session.AccessAsync(_db, prove: false, ct).ConfigureAwait(false);

        if (signIn.Access is not { } access)
        {
            var status = signIn.Hold == BlueskyHold.Stopped ? StatusCodes.Status429TooManyRequests : StatusCodes.Status502BadGateway;
            return await FailedAsync(status, signIn.Problem ?? BlueskyErrors.Unreachable, now, ct).ConfigureAwait(false);
        }

        if (!string.Equals(access.Did, did, StringComparison.Ordinal))
            return new BlueskyProfileSave(StatusCodes.Status400BadRequest, NotSetUp);

        var read = await _client.GetRecordAsync(access.Server, did, BlueskyClient.ProfileCollection, "self", ct).ConfigureAwait(false);

        // An account with no profile record yet: the write makes one.
        var missing = read.Failure is { Problem: BlueskyProblem.RecordNotFound };

        if (read.Failure is { } readFailure && !missing)
            return await RefusedAsync(access, readFailure, now, ct).ConfigureAwait(false);

        var current = missing ? null : read.Value.Value;
        var cid = missing || read.Value.Ref.Cid.Length == 0 ? null : read.Value.Ref.Cid;

        JsonObject? avatarBlob = null;
        JsonObject? bannerBlob = null;

        if (avatar is { } newAvatar)
        {
            var uploaded = await _client.UploadBlobAsync(access, newAvatar.Bytes, newAvatar.ContentType, ct).ConfigureAwait(false);

            if (uploaded.Failure is { } failure)
                return await RefusedAsync(access, failure, now, ct).ConfigureAwait(false);

            avatarBlob = uploaded.Value;
        }

        if (banner is { } newBanner)
        {
            var uploaded = await _client.UploadBlobAsync(access, newBanner.Bytes, newBanner.ContentType, ct).ConfigureAwait(false);

            if (uploaded.Failure is { } failure)
                return await RefusedAsync(access, failure, now, ct).ConfigureAwait(false);

            bannerBlob = uploaded.Value;
        }

        var wanted = BlueskyProfileRecord.Apply(current, new BlueskyProfileChange(name, bio, avatarBlob, bannerBlob));
        var put = await _client.ReplaceRecordAsync(access, did, BlueskyClient.ProfileCollection, "self", wanted, cid, ct).ConfigureAwait(false);

        if (put.Failure is { } putFailure)
            return await RefusedAsync(access, putFailure, now, ct).ConfigureAwait(false);

        await _db.Settings.Where(s => s.Id == 1)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.BlueskyProfileError, (string?)null).SetProperty(s => s.BlueskyProfileErrorAt, (DateTimeOffset?)null),
                ct).ConfigureAwait(false);

        // What was written is shown at once: Bluesky's own page of the profile is a moment behind. The
        // numbers are the last ones read.
        var before = _memory.Last(did)?.Profile;
        _memory.Keep(new BlueskyProfileMemory.Answer(
            did,
            now,
            new BlueskyAccountProfile(
                did,
                before?.Handle ?? settings.BlueskyHandle ?? string.Empty,
                TextOf(wanted["displayName"]),
                TextOf(wanted["description"]),
                BlueskyProfileRecord.PictureAddress(wanted, "avatar", did),
                BlueskyProfileRecord.PictureAddress(wanted, "banner", did),
                before?.Followers,
                before?.Following,
                before?.Posts),
            null));

        return new BlueskyProfileSave(StatusCodes.Status200OK, null, current, wanted);
    }

    /// <summary>A call that changed nothing: kept for Health, with the status the page gets.</summary>
    private async Task<BlueskyProfileSave> RefusedAsync(BlueskyAccess access, BlueskyFailure failure, DateTimeOffset now, CancellationToken ct)
    {
        if (failure.StopsTheLane)
        {
            _log.Warning("Bluesky is limiting Modbot; nothing is sent to it until the limit resets");
            await _session.StopAsync(_db, failure, ct).ConfigureAwait(false);
            return await FailedAsync(StatusCodes.Status429TooManyRequests, BlueskyErrors.Limited, now, ct).ConfigureAwait(false);
        }

        if (failure.Problem is BlueskyProblem.TokenExpired or BlueskyProblem.TokenRefused)
            await _session.ExpiredAsync(_db, access.AccessJwt, ct).ConfigureAwait(false);

        return failure.Problem == BlueskyProblem.InvalidSwap
            ? await FailedAsync(StatusCodes.Status409Conflict, BlueskyProfileRecord.ChangedMeanwhile, now, ct).ConfigureAwait(false)
            : await FailedAsync(StatusCodes.Status502BadGateway, BlueskyErrors.Sentence(failure), now, ct).ConfigureAwait(false);
    }

    private async Task<BlueskyProfileSave> FailedAsync(int status, string error, DateTimeOffset now, CancellationToken ct)
    {
        _log.Warning("The Bluesky profile could not be saved: {Reason}", error);

        await _db.Settings.Where(s => s.Id == 1)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.BlueskyProfileError, error).SetProperty(s => s.BlueskyProfileErrorAt, now),
                ct).ConfigureAwait(false);

        return new BlueskyProfileSave(status, error);
    }

    /// <summary>The account to ask about: its DID, else its handle, else none.</summary>
    private static string? ActorOf(Core.Data.Entities.Settings? settings) =>
        settings is null ? null
        : !string.IsNullOrWhiteSpace(settings.BlueskyDid) ? settings.BlueskyDid.Trim()
        : !string.IsNullOrWhiteSpace(settings.BlueskyHandle) ? settings.BlueskyHandle.Trim()
        : null;

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static BlueskyProfileView NotSetUpView(bool canEdit) =>
        new(BlueskyProfileStates.NotSetUp, null, null, null, null, null, null, null, null, null, canEdit, false, null, null, null);

    private static BlueskyProfileView ViewOf(
        Core.Data.Entities.Settings settings, bool canEdit, BlueskyAccountProfile? profile, string? problem, DateTimeOffset now)
    {
        // Both lanes: the account's own server (saving) and the public API (reading).
        var limited = new[] { settings.BlueskyStoppedUntil, settings.BlueskyProfileStoppedUntil }
            .Where(until => until is { } at && now < at)
            .Max();

        var handle = profile?.Handle is { Length: > 0 } read ? read : settings.BlueskyHandle;

        return new BlueskyProfileView(
            profile is null ? BlueskyProfileStates.Unavailable : BlueskyProfileStates.Ready,
            profile is null ? problem ?? PostBlueskyCounts.CouldNotReach : null,
            handle,
            profile?.DisplayName,
            profile?.Bio,
            profile?.AvatarUrl,
            profile?.BannerUrl,
            profile?.Followers,
            profile?.Following,
            profile?.Posts,
            canEdit,
            PostSites.BlueskyReady(settings),
            limited,
            settings.BlueskyProfileError,
            settings.BlueskyProfileErrorAt);
    }
}
