using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Bluesky;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Calendar;

/// <param name="Pinned">Posts pinned on the profile this pass.</param>
/// <param name="Unpinned">Pins taken off the profile this pass with nothing put in their place.</param>
public sealed record CalendarBlueskyPinPass(int Pinned, int Unpinned);

/// <summary>
/// Keeps the next calendar date's Bluesky post pinned on the group's profile (Settings → Bluesky,
/// Pin the next event).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The post.</strong> Of the event dates with a post that is on Bluesky, the one that starts
/// first and has not ended. When that date ends the next one's post takes the pin; with none left the
/// pin is taken off. Dates and events that are gone count as ended.
/// </para>
/// <para>
/// <strong>A pin a person set by hand is left alone</strong> (user, 2026-10-14): Modbot writes the
/// profile only while nothing is pinned or while the pinned post is one Modbot recorded. The profile
/// is read first and written back whole with only its pinned post changed, and only if it has not
/// changed since it was read (<c>swapRecord</c>), so nothing a person saved meanwhile is lost.
/// </para>
/// <para>
/// <strong>Few calls.</strong> A pass with nothing to change asks Bluesky nothing: the address last
/// pinned is kept in Settings. After a look that changed nothing or could not, the next look waits
/// <see cref="LookEvery"/>. No rate limit is measured for the profile; this wait is the placeholder.
/// A 429 stops the lane and is not tried again (CLAUDE.md). Nothing happens while the Bluesky Posting
/// switch is off, Pause all posting is on or the account has not passed Check, the same as the posts
/// themselves.
/// </para>
/// </remarks>
public sealed class CalendarBlueskyPin
{
    /// <summary>How long after a look that changed nothing, or could not, the next look waits.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromMinutes(15);

    /// <summary>The longest one date may last; a post for a date that started longer ago cannot be for one still running.</summary>
    private static readonly TimeSpan LongestDate = TimeSpan.FromDays(7);

    private const string StrongRef = "com.atproto.repo.strongRef";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly BlueskySession _session;
    private readonly BlueskyClient _client;
    private readonly ILogger _log;

    public CalendarBlueskyPin(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        BlueskySession session,
        BlueskyClient client,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(client);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _session = session;
        _client = client;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>A post that can be pinned: its address and content id on Bluesky.</summary>
    private sealed record Target(Post Post, PostDestination Destination, string Uri, string Cid);

    public async Task<CalendarBlueskyPinPass> RunOnceAsync(CancellationToken ct = default)
    {
        var none = new CalendarBlueskyPinPass(0, 0);

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        if (settings is null || !settings.BlueskyPinNextEvent || string.IsNullOrWhiteSpace(settings.BlueskyDid))
            return none;

        var sites = new PostSites(
            settings.PostsPaused,
            DiscordOn: false,
            DiscordSetUp: false,
            VRChatOn: false,
            VRChatSetUp: false,
            settings.BlueskyPostingOn,
            PostSites.BlueskyReady(settings));

        var now = _clock.UtcNow;

        if (sites.HoldFor(PostNetworks.Bluesky) is not null || settings.BlueskyStoppedUntil is { } until && now < until)
            return none;

        var did = settings.BlueskyDid.Trim();
        var target = await TargetAsync(did, now, ct).ConfigureAwait(false);

        // Nothing pinned and nothing to pin, or the right post pinned already: no call.
        if (string.Equals(target?.Uri, settings.BlueskyPinnedUri, StringComparison.Ordinal))
            return none;

        if (settings.BlueskyPinCheckedAt is { } checkedAt && now - checkedAt < LookEvery)
            return none;

        var signIn = await _session.AccessAsync(_db, prove: false, ct).ConfigureAwait(false);
        if (signIn.Access is not { } access)
        {
            if (signIn.Hold != BlueskyHold.Stopped)
                await LookedAsync(now, ct).ConfigureAwait(false);

            return none;
        }

        // Only the account in Settings.
        if (!string.Equals(access.Did, did, StringComparison.Ordinal))
            return none;

        var read = await _client.GetRecordAsync(access.Server, did, BlueskyClient.ProfileCollection, "self", ct).ConfigureAwait(false);

        // An account with no profile record has nothing pinned; the write makes one.
        var missing = read.Failure is { Problem: BlueskyProblem.RecordNotFound };

        if (read.Failure is { } readFailure && !missing)
        {
            await RefusedAsync(access, readFailure, now, ct).ConfigureAwait(false);
            return none;
        }

        var profile = missing ? new JsonObject { ["$type"] = BlueskyClient.ProfileCollection } : read.Value.Value ?? new JsonObject();
        var cid = missing || read.Value.Ref.Cid.Length == 0 ? null : read.Value.Ref.Cid;
        var pinned = profile["pinnedPost"]?["uri"] is JsonValue u && u.TryGetValue<string>(out var uriText) && uriText.Length > 0 ? uriText : null;

        // Already as wanted: only the record of it in Settings was out of date.
        if (string.Equals(pinned, target?.Uri, StringComparison.Ordinal))
        {
            await SetPinnedAsync(target?.Uri, ct).ConfigureAwait(false);
            return none;
        }

        // A post a person pinned by hand stays: only a post Modbot made is ever replaced or taken off.
        var old = pinned is null ? null : await OwnPostAsync(pinned, ct).ConfigureAwait(false);
        if (pinned is not null && old is null && !string.Equals(pinned, settings.BlueskyPinnedUri, StringComparison.Ordinal))
        {
            _log.Information("The profile has a post pinned by hand; Modbot leaves it");
            await LookedAsync(now, ct).ConfigureAwait(false);
            return none;
        }

        var wanted = (JsonObject)profile.DeepClone();
        if (target is null)
        {
            wanted.Remove("pinnedPost");
        }
        else
        {
            wanted["pinnedPost"] = new JsonObject { ["$type"] = StrongRef, ["uri"] = target.Uri, ["cid"] = target.Cid };
        }

        var put = await _client.ReplaceRecordAsync(access, did, BlueskyClient.ProfileCollection, "self", wanted, cid, ct).ConfigureAwait(false);
        if (put.Failure is { } failure)
        {
            // A profile saved since it was read, or a sign-in that lacks the profile right (Sign in with
            // Bluesky again): looked at again later, with a fresh read.
            await RefusedAsync(access, failure, now, ct).ConfigureAwait(false);
            return none;
        }

        await SetPinnedAsync(target?.Uri, ct).ConfigureAwait(false);

        if (target is not null)
        {
            await WriteFactAsync(FactType.PostPinned, target.Post, target.Destination, now, null, ct).ConfigureAwait(false);
            return new CalendarBlueskyPinPass(1, 0);
        }

        if (old is { } was)
            await WriteFactAsync(FactType.PostUnpinned, was.Post, was.Destination, now, "No later date has a post.", ct).ConfigureAwait(false);

        return new CalendarBlueskyPinPass(0, 1);
    }

    // ── Which post ───────────────────────────────────────────────────────────────────────

    /// <summary>The post of the first date that has not ended, or null when no date has one on Bluesky.</summary>
    private async Task<Target?> TargetAsync(string did, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - LongestDate;

        var rows = await _db.PostDestinations.AsNoTracking()
            .Join(_db.Posts.AsNoTracking(), d => d.PostId, p => p.Id, (d, p) => new { Destination = d, Post = p })
            .Where(x => x.Post.Kind == PostKinds.EventDate
                && x.Post.EventId != null
                && x.Post.DateStartsAt > since
                && x.Destination.Network == PostNetworks.Bluesky
                && x.Destination.State == PostDestinationStates.Posted
                && x.Destination.Target == did
                && x.Destination.ExternalId != null)
            .OrderBy(x => x.Post.DateStartsAt)
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0)
            return null;

        var ids = rows.Select(x => x.Post.EventId!.Value).Distinct().ToList();
        var events = await _db.CalendarEvents.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            // Without a content id the post cannot be pinned; it is read back or posted again first.
            if (PostTexts.BlueskyOptionsOf(row.Destination).Cid is not { Length: > 0 } cid)
                continue;

            if (!events.TryGetValue(row.Post.EventId!.Value, out var calendarEvent)
                || calendarEvent.DeletedAt is not null
                || !CalendarEventStates.IsLive(calendarEvent.State))
            {
                continue;
            }

            // The date as it happens now, which a move of the date changes.
            var date = CalendarRepeat.StartingAt(calendarEvent, row.Post.DateStartsAt!.Value);
            if (date is not { } found || found.EndsAt <= now)
                continue;

            return new Target(row.Post, row.Destination, row.Destination.ExternalId!, cid);
        }

        return null;
    }

    /// <summary>
    /// The calendar date post and destination Modbot recorded at <paramref name="uri"/>, or null when it is
    /// none of those: a Marketing post a person pinned by hand is not Modbot's to move.
    /// </summary>
    private async Task<(Post Post, PostDestination Destination)?> OwnPostAsync(string uri, CancellationToken ct)
    {
        var row = await _db.PostDestinations.AsNoTracking()
            .Where(d => d.Network == PostNetworks.Bluesky && d.ExternalId == uri)
            .Join(_db.Posts.AsNoTracking(), d => d.PostId, p => p.Id, (d, p) => new { Destination = d, Post = p })
            .Where(x => x.Post.Kind == PostKinds.EventDate)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return row is null ? null : (row.Post, row.Destination);
    }

    // ── What Bluesky answered ────────────────────────────────────────────────────────────

    /// <summary>
    /// A call that changed nothing. A rate limit stops the lane for good measure, a token that ended is
    /// refreshed next pass, and anything else is looked at again after <see cref="LookEvery"/>.
    /// </summary>
    private async Task RefusedAsync(BlueskyAccess access, BlueskyFailure failure, DateTimeOffset now, CancellationToken ct)
    {
        if (failure.StopsTheLane)
        {
            _log.Warning("Bluesky is limiting Modbot; nothing is sent to it until the limit resets");
            await _session.StopAsync(_db, failure, ct).ConfigureAwait(false);
            return;
        }

        if (failure.Problem is BlueskyProblem.TokenExpired or BlueskyProblem.TokenRefused)
        {
            // A sign-in without the profile right can be answered this way too: the look is held back as
            // well, so a pass does not refresh and try again every time.
            await _session.ExpiredAsync(_db, access.AccessJwt, ct).ConfigureAwait(false);
            await LookedAsync(now, ct).ConfigureAwait(false);
            return;
        }

        var reason = BlueskyErrors.Sentence(failure);
        _log.Warning("The post could not be pinned on the Bluesky profile: {Reason}", reason);
        await LookedAsync(now, ct).ConfigureAwait(false);
        await _db.Settings.Where(s => s.Id == 1)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.BlueskyPinError, reason).SetProperty(s => s.BlueskyPinErrorAt, now),
                ct).ConfigureAwait(false);
    }

    private Task LookedAsync(DateTimeOffset now, CancellationToken ct) =>
        _db.Settings.Where(s => s.Id == 1)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyPinCheckedAt, now), ct);

    /// <summary>The profile is as wanted: the address is kept, the next look is not held back and an earlier error is cleared.</summary>
    private Task SetPinnedAsync(string? uri, CancellationToken ct) =>
        _db.Settings.Where(s => s.Id == 1)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.BlueskyPinnedUri, uri)
                    .SetProperty(s => s.BlueskyPinCheckedAt, (DateTimeOffset?)null)
                    .SetProperty(s => s.BlueskyPinError, (string?)null)
                    .SetProperty(s => s.BlueskyPinErrorAt, (DateTimeOffset?)null),
                ct);

    private async Task WriteFactAsync(string type, Post post, PostDestination destination, DateTimeOffset now, string? reason, CancellationToken ct)
    {
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = post.Id.ToString(),
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["title"] = post.Title,
                    ["network"] = PostNetworks.Bluesky,
                    ["link"] = destination.Link,
                    ["externalId"] = destination.ExternalId,
                    ["reason"] = reason,
                },
            },
            ct).ConfigureAwait(false);
    }
}
