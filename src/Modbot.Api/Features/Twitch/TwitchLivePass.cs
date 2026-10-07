using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Posts;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Core.Twitch;
using Serilog;

namespace Modbot.Api.Features.Twitch;

/// <summary>What one pass of the Twitch poll did.</summary>
/// <param name="Polled">Twitch was asked and answered.</param>
/// <param name="Live">The channel was live at that answer.</param>
/// <param name="MadePost">A "We're live on Twitch" post was made.</param>
/// <param name="Problem">What stopped the pass, as the sentence Health shows. Null when nothing did.</param>
public sealed record TwitchLivePassResult(bool Polled, bool Live, bool MadePost, string? Problem);

/// <summary>
/// Asks Twitch once a minute whether the channel is live, keeps a row for each stream, tells the
/// Live and Now pages, links the stream to the calendar event on at the time, and makes the
/// "We're live on Twitch" post (Twitch design, steps 1 to 3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>An app token, no public address.</strong> Get Streams answers an app access token, so the
/// poll needs only the client id and secret in Settings. A token Twitch refuses (401) is dropped and
/// asked for again once.
/// </para>
/// <para>
/// <strong>A rate limit stops every call.</strong> A 429 on any call writes
/// <see cref="Core.Data.Entities.Settings.TwitchStoppedUntil"/> from Twitch's <c>Ratelimit-Reset</c>
/// (15 minutes when it names none) and nothing goes to Twitch before then. A 429 is never sent again.
/// </para>
/// <para>
/// <strong>One post per stream.</strong> A stream is one <c>twitch_stream</c> row, written when it is
/// first seen, so a restart during a stream finds the row and decides nothing twice. The post is
/// decided once (<see cref="TwitchRules.Decide"/>), under the database's own guard: a partial unique
/// index on <c>post(kind, external_key)</c>.
/// </para>
/// <para>
/// Everything a pass writes, its facts included, is one transaction, so a pass that fails halfway
/// leaves nothing half-done and runs again whole a minute later. Times come from
/// <see cref="IModbotClock"/>.
/// </para>
/// </remarks>
public sealed class TwitchLivePass
{
    /// <summary>How long a stream that ended before it was decided about may still come back as the same stream.</summary>
    public static readonly TimeSpan EndedStaysOpenFor = TimeSpan.FromHours(1);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ISecretProtector _protector;
    private readonly TwitchSignIn _signIn;
    private readonly TwitchClient _twitch;
    private readonly AccountFacts _facts;
    private readonly ILogger _log;

    public TwitchLivePass(
        ModbotContext db,
        IModbotClock clock,
        ISecretProtector protector,
        TwitchSignIn signIn,
        TwitchClient twitch,
        AccountFacts facts,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(twitch);
        ArgumentNullException.ThrowIfNull(facts);

        _db = db;
        _clock = clock;
        _protector = protector;
        _signIn = signIn;
        _twitch = twitch;
        _facts = facts;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<TwitchLivePassResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        if (settings is null || !settings.TwitchLiveOn || !TwitchRules.SetUp(settings))
            return new TwitchLivePassResult(false, false, false, null);

        // Cold stop: nothing goes to Twitch while it limits Modbot.
        if (TwitchRules.Stopped(settings, now))
            return new TwitchLivePassResult(false, false, false, TwitchErrors.Limiting);

        var asked = await AskAsync(settings, ct).ConfigureAwait(false);

        if (asked.Failure is { } failure)
        {
            if (failure.IsALimit)
                settings.TwitchStoppedUntil = TwitchErrors.StopUntil(failure, now);

            settings.TwitchPollProblem = TwitchErrors.Sentence(failure);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return new TwitchLivePassResult(false, false, false, settings.TwitchPollProblem);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        settings.TwitchPolledAt = now;
        settings.TwitchPollProblem = null;

        var live = asked.Stream;
        await UpdateStreamsAsync(live, now, ct).ConfigureAwait(false);
        var made = await DecidePostsAsync(settings, now, ct).ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new TwitchLivePassResult(true, live is not null, made, null);
    }

    // ── Asking Twitch ────────────────────────────────────────────────────────────────────

    private sealed record Asked(TwitchLiveStream? Stream, TwitchFailure? Failure);

    private async Task<Asked> AskAsync(Core.Data.Entities.Settings settings, CancellationToken ct)
    {
        var clientId = settings.TwitchClientId!;
        var secret = _protector.Unprotect(settings.TwitchClientSecretEncrypted);

        if (secret is null)
            return new Asked(null, new TwitchFailure(TwitchProblem.CredentialsRefused, 0));

        // A token Twitch refuses is asked for again once; anything else is the pass's answer.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await _signIn.TokenAsync(clientId, secret, fresh: false, ct).ConfigureAwait(false);
            if (token.Failure is { } refused)
                return new Asked(null, refused);

            var accessToken = token.Value!;
            var stream = await _twitch.StreamAsync(accessToken, clientId, settings.TwitchChannelId!, ct).ConfigureAwait(false);

            if (stream.Failure is { Problem: TwitchProblem.Unauthorized } unauthorized)
            {
                _signIn.Refused(accessToken);

                if (attempt == 1)
                    return new Asked(null, unauthorized);

                continue;
            }

            return new Asked(stream.Value, stream.Failure);
        }

        return new Asked(null, TwitchErrors.NoAnswer());
    }

    // ── The streams ──────────────────────────────────────────────────────────────────────

    private async Task UpdateStreamsAsync(TwitchLiveStream? live, DateTimeOffset now, CancellationToken ct)
    {
        var open = await _db.TwitchStreams.Where(s => s.EndedAt == null).ToListAsync(ct).ConfigureAwait(false);

        // A stream that is no longer the live one has ended.
        foreach (var row in open.Where(s => live is null || s.Id != live.Id))
        {
            row.EndedAt = now;
            await _facts.RecordAsync(
                FactType.TwitchOffline,
                row.Id,
                actor: null,
                new JsonObject
                {
                    ["title"] = row.Title,
                    ["category"] = row.Category,
                    ["startedAt"] = Iso(row.StartedAt),
                    ["peakViewers"] = row.PeakViewers,
                },
                ct).ConfigureAwait(false);
        }

        if (live is null)
            return;

        var stream = open.FirstOrDefault(s => s.Id == live.Id)
            ?? await _db.TwitchStreams.FirstOrDefaultAsync(s => s.Id == live.Id, ct).ConfigureAwait(false);

        var title = TwitchRules.Cut(live.Title ?? string.Empty, TwitchStream.MaxTitleLength);
        var category = live.Category is null ? null : TwitchRules.Cut(live.Category, TwitchStream.MaxTitleLength);

        if (stream is null)
        {
            stream = new TwitchStream
            {
                Id = live.Id,
                StartedAt = live.StartedAt,
                FirstSeenAt = now,
                LastSeenAt = now,
                Type = TwitchRules.Cut(live.Type, 32),
                Title = title,
                Category = category,
                Viewers = live.Viewers,
                PeakViewers = live.Viewers,
                UpdateSentAt = now,
            };

            stream.EventId = await EventAtAsync(live.StartedAt, ct).ConfigureAwait(false);
            _db.TwitchStreams.Add(stream);

            await _facts.RecordAsync(
                FactType.TwitchOnline,
                stream.Id,
                actor: null,
                new JsonObject
                {
                    ["title"] = stream.Title,
                    ["category"] = stream.Category,
                    ["startedAt"] = Iso(stream.StartedAt),
                    ["viewers"] = stream.Viewers,
                    ["eventId"] = stream.EventId?.ToString(),
                },
                ct).ConfigureAwait(false);

            return;
        }

        // The same stream, seen again: a stream that dropped out and came back under its Twitch id is still one row.
        var wasEnded = stream.EndedAt is not null;
        var moved = wasEnded
            || !string.Equals(stream.Title, title, StringComparison.Ordinal)
            || !string.Equals(stream.Category, category, StringComparison.Ordinal);

        stream.EndedAt = null;
        stream.LastSeenAt = now;
        stream.Title = title;
        stream.Category = category;

        // An event made or moved after the stream began may be the one on: while nobody has chosen
        // for this stream and none is linked, the event is looked for again. A person's choice,
        // clearing included, is never changed, and overlapping events still leave it empty.
        if (stream.EventId is null && !stream.EventSetByStaff)
        {
            var found = await EventAtAsync(stream.StartedAt, ct).ConfigureAwait(false);

            if (found is not null)
            {
                stream.EventId = found;
                moved = true;
            }
        }

        var viewersMoved = stream.Viewers != live.Viewers;
        stream.Viewers = live.Viewers;
        stream.PeakViewers = Math.Max(stream.PeakViewers, live.Viewers);

        // A moved title or category goes out at once; a moved viewer count at most every few
        // minutes, so a long stream does not write a fact a minute.
        var due = stream.UpdateSentAt is not { } sent || now - sent >= TwitchRules.UpdateEvery;

        if (moved || (viewersMoved && due))
        {
            stream.UpdateSentAt = now;

            await _facts.RecordAsync(
                FactType.TwitchUpdated,
                stream.Id,
                actor: null,
                new JsonObject
                {
                    ["title"] = stream.Title,
                    ["category"] = stream.Category,
                    ["viewers"] = stream.Viewers,
                    ["eventId"] = stream.EventId?.ToString(),
                },
                ct).ConfigureAwait(false);
        }

    }

    /// <summary>The one event on when a stream started, or null (none, or several overlapping).</summary>
    private async Task<Guid?> EventAtAsync(DateTimeOffset startedAt, CancellationToken ct)
    {
        var until = startedAt + TwitchRules.EventLeadTime;

        // Drafts and cancelled events never ran. A repeating event is read by its rule, so it is
        // brought in whole and its dates worked out here.
        var candidates = await _db.CalendarEvents.AsNoTracking()
            .Include(e => e.DateChanges)
            .Where(e => e.DeletedAt == null
                && e.StartsAt < until
                && (e.State == CalendarEventStates.Scheduled
                    || e.State == CalendarEventStates.Open
                    || e.State == CalendarEventStates.Finished)
                && (e.Repeat != CalendarRepeats.None || e.EndsAt > startedAt || e.DateChanges.Any()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return TwitchRules.ChooseEvent(candidates, startedAt);
    }

    // ── The post ─────────────────────────────────────────────────────────────────────────

    private async Task<bool> DecidePostsAsync(Core.Data.Entities.Settings settings, DateTimeOffset now, CancellationToken ct)
    {
        var undecided = await _db.TwitchStreams
            .Where(s => s.PostDecidedAt == null)
            .OrderBy(s => s.StartedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (undecided.Count == 0)
            return false;

        var places = TwitchPostPlaces.Parse(settings.TwitchPostPlaces);
        var lastPostAt = await _db.Posts
            .Where(p => p.Kind == PostKinds.TwitchLive)
            .MaxAsync(p => (DateTimeOffset?)p.CreatedAt, ct)
            .ConfigureAwait(false);

        var made = false;

        foreach (var stream in undecided)
        {
            // Gone before it had been live long enough: never a post. Left open for an hour first,
            // because a stream that drops out and comes back under the same Twitch id is the same stream.
            if (stream.EndedAt is { } ended)
            {
                if (now - ended >= EndedStaysOpenFor)
                    stream.PostDecidedAt = now;

                continue;
            }

            var decision = TwitchRules.Decide(
                stream.Type,
                stream.StartedAt,
                stream.FirstSeenAt,
                now,
                settings.TwitchPostAfterMinutes,
                settings.TwitchPostEveryHours,
                lastPostAt,
                places.AnyTicked);

            if (decision == TwitchPostDecision.Wait)
                continue;

            stream.PostDecidedAt = now;

            if (decision != TwitchPostDecision.Make)
                continue;

            // The database's guard, checked first so a second pass finds the post rather than
            // tripping the index: one post per Twitch stream id.
            var existing = await _db.Posts
                .Where(p => p.Kind == PostKinds.TwitchLive && p.ExternalKey == stream.Id)
                .Select(p => (Guid?)p.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                stream.PostId = existing;
                continue;
            }

            var post = await TwitchPosts.BuildAsync(_db, settings, stream, now, ct).ConfigureAwait(false);
            if (post is null)
                continue;

            stream.PostId = post.Id;
            lastPostAt = now;
            made = true;

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await _facts.RecordAsync(FactType.PostCreated, post.Id.ToString(), actor: null, PostRequests.Describe(post), ct)
                .ConfigureAwait(false);

            _log.Information("Made the \"live on Twitch\" post for stream {StreamId}", stream.Id);
        }

        return made;
    }

    private static string Iso(DateTimeOffset at) => at.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
