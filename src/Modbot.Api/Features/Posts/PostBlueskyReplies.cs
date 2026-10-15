using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Posts;

/// <param name="Written">Replies, mentions and quotes written to the audit log this pass.</param>
/// <param name="Asked">Bluesky was asked (a pass that only waited, or only started reading, did not).</param>
public sealed record PostBlueskyRepliesPass(int Written, bool Asked);

/// <summary>
/// Reads the replies, mentions and quotes of the group's Bluesky account and writes each one to the
/// audit log once (posts design §4.2f).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is read.</strong> <c>app.bsky.notification.listNotifications</c> with the reasons
/// <c>reply</c>, <c>mention</c> and <c>quote</c> (<see cref="BlueskyClient.MentionsAsync"/>), through
/// the account's own sign-in. Likes, reposts and follows are not asked for. What is kept is who wrote
/// it, their text and the link to the post. A post of the group's own account (a thread it continues)
/// is skipped. A post that is a reply and a mention both is written once, as the reply.
/// </para>
/// <para>
/// <strong>Each one once.</strong> <see cref="Settings.BlueskyRepliesReadAt"/> is the newest read.
/// The first pass has none: it starts from then and writes nothing from before. Later, the pages are
/// read newest first until one reaches the mark, at most <see cref="MostPages"/> of them (what is
/// older than the newest few hundred is left), then written oldest first, the mark moving on
/// after each. A pass that fails before the writing starts leaves the mark where it was. Those at the
/// mark are read again, so none that shares its second is lost; one already in the audit log is not
/// written twice. One that cannot be stored (text PostgreSQL refuses) is logged and left out, and the
/// mark moves past it.
/// </para>
/// <para>
/// <strong>Only while Posting is on</strong> and the account has passed Check, as every other Bluesky pass:
/// with Posting off nothing is read and no sign-in warning is shown.
/// </para>
/// <para>
/// <strong>A sign-in made before the reading was added</strong> lacks the permission for it
/// (<see cref="BlueskyOAuth.RepliesScope"/>): Bluesky has to be signed in with again. That is seen from
/// the scope the sign-in was given, with no call, and shown on Health. An app password needs nothing.
/// </para>
/// <para>
/// <strong>A rate limit stops the pass until the time Bluesky says it resets</strong> and is never
/// retried (CLAUDE.md, §4.3.1), on its own marker (<see cref="Settings.BlueskyRepliesStoppedUntil"/>)
/// so posting is not held up by it. Any other failure stops it for <see cref="FailurePause"/>. Either is kept for Health
/// until a read goes through. While the account's own lane is stopped
/// (<see cref="Settings.BlueskyStoppedUntil"/>) nothing is asked.
/// </para>
/// <para>
/// <strong>Rate limit: NOT MEASURED.</strong> Nobody has measured the limit on this call (CLAUDE.md,
/// §4.3.4: not in the spec's table, <c>.agent/research/</c>, <c>RateLimitOptions.cs</c> or the
/// <c>libs/</c> clones). The placeholder is cautious: a read at most every <see cref="Every"/>, of at
/// most <see cref="MostPages"/> calls, one after the other.
/// </para>
/// </remarks>
public sealed class PostBlueskyReplies
{
    /// <summary>NOT MEASURED: the least time between two reads. A placeholder.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(3);

    /// <summary>How long the pass waits after a failure that is not a rate limit.</summary>
    public static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(5);

    /// <summary>The most pages (calls) one pass reads: a hundred a page.</summary>
    public const int MostPages = 3;

    /// <summary>The words on Health when the sign-in cannot read replies.</summary>
    public const string SignInAgain = "Sign in with Bluesky again to read replies and mentions.";

    private static readonly string[] Types = [FactType.BlueskyReply, FactType.BlueskyMention, FactType.BlueskyQuote];

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly BlueskySession _session;
    private readonly BlueskyClient _client;
    private readonly ILogger _log;

    public PostBlueskyReplies(
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

    public async Task<PostBlueskyRepliesPass> RunOnceAsync(CancellationToken ct = default)
    {
        var none = new PostBlueskyRepliesPass(0, false);
        var now = _clock.UtcNow;

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        if (settings is null || !settings.BlueskyPostingOn || !PostSites.BlueskyReady(settings))
            return none;

        if (settings.BlueskyRepliesStoppedUntil is { } stopped && now < stopped)
            return none;

        if (settings.BlueskyStoppedUntil is { } laneStopped && now < laneStopped)
            return none;

        // Seen from the sign-in as saved: no call is needed to know it cannot read.
        if (_session.Read(settings.BlueskySessionEncrypted)?.OAuth is { } grant && !BlueskyOAuth.CanReadReplies(grant.Scope))
        {
            if (settings.BlueskyRepliesError != SignInAgain)
            {
                _log.Warning("Replies and mentions on Bluesky cannot be read until Bluesky is signed in with again");
                await SetErrorAsync(SignInAgain, now, ct).ConfigureAwait(false);
            }

            return none;
        }

        // The first pass starts from now: nothing from before is written.
        if (settings.BlueskyRepliesReadAt is not { } mark)
        {
            await _db.Settings.Where(s => s.Id == 1)
                .ExecuteUpdateAsync(
                    u => u.SetProperty(s => s.BlueskyRepliesReadAt, now).SetProperty(s => s.BlueskyRepliesCheckedAt, now),
                    ct).ConfigureAwait(false);
            await ClearErrorAsync(settings.BlueskyRepliesError, ct).ConfigureAwait(false);
            return none;
        }

        if (settings.BlueskyRepliesCheckedAt is { } checkedAt && now - checkedAt < Every)
            return none;

        var signIn = await _session.AccessAsync(_db, prove: false, ct).ConfigureAwait(false);
        if (signIn.Access is not { } access || !string.Equals(access.Did, settings.BlueskyDid?.Trim(), StringComparison.Ordinal))
            return none;

        await _db.Settings.Where(s => s.Id == 1)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyRepliesCheckedAt, now), ct).ConfigureAwait(false);

        var fresh = new List<BlueskyMention>();
        string? cursor = null;

        for (var page = 0; page < MostPages; page++)
        {
            var answer = await _client.MentionsAsync(access, cursor, ct).ConfigureAwait(false);

            if (answer.Failure is { } failure)
            {
                await FailedAsync(access, failure, now, ct).ConfigureAwait(false);
                return new PostBlueskyRepliesPass(0, true);
            }

            var read = answer.Value!;

            // At the mark too: two notifications can share a second, and one of them may have come after the
            // pass that moved the mark there. What is already in the audit log is skipped below.
            fresh.AddRange(read.Mentions.Where(m => m.IndexedAt >= mark));

            // Newest first: a page with one at the mark has nothing newer on the pages after it.
            if (read.Cursor is null || read.Mentions.Count == 0 || read.Mentions.Any(m => m.IndexedAt <= mark))
                break;

            cursor = read.Cursor;
        }

        var written = 0;
        var own = settings.BlueskyDid!.Trim();

        // One a post, as the reply when it is also a mention; oldest first, so the mark only moves forward.
        foreach (var mention in fresh
            .GroupBy(m => m.Uri, StringComparer.Ordinal)
            .Select(g => g.OrderBy(m => Strength(m.Reason)).First())
            .OrderBy(m => m.IndexedAt))
        {
            if (!string.Equals(mention.AuthorDid, own, StringComparison.Ordinal))
            {
                try
                {
                    if (await WriteFactAsync(mention, now, ct).ConfigureAwait(false))
                        written++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One that cannot be stored is left out, so it does not stop every pass after it.
                    _log.Warning(ex, "A Bluesky reply, mention or quote could not be saved to the audit log and was left out");
                }
            }

            await MovedOnAsync(mention.IndexedAt, ct).ConfigureAwait(false);
        }

        await ClearErrorAsync(settings.BlueskyRepliesError, ct).ConfigureAwait(false);

        return new PostBlueskyRepliesPass(written, true);
    }

    private static int Strength(string reason) => reason switch { "reply" => 0, "quote" => 1, _ => 2 };

    // ── The audit log ────────────────────────────────────────────────────────────────────

    /// <summary>True when it was written; false when the audit log already had it (a pass that stopped before the mark moved).</summary>
    private async Task<bool> WriteFactAsync(BlueskyMention mention, DateTimeOffset now, CancellationToken ct)
    {
        var uri = Clean(mention.Uri)!;

        if (await _db.Events.AsNoTracking()
            .AnyAsync(e => Types.Contains(e.Type) && e.SubjectId == uri, ct).ConfigureAwait(false))
            return false;

        var type = mention.Reason switch
        {
            "reply" => FactType.BlueskyReply,
            "quote" => FactType.BlueskyQuote,
            _ => FactType.BlueskyMention,
        };

        var at = mention.IndexedAt < now ? mention.IndexedAt : now;

        var data = new JsonObject
        {
            ["handle"] = Clean(mention.AuthorHandle),
            ["displayName"] = Clean(mention.AuthorName),
            ["did"] = Clean(mention.AuthorDid),
            ["text"] = Clean(mention.Text),
            ["link"] = LinkTo(mention),
        };

        // The Modbot post a reply answers, when it is one Modbot sent.
        if (Clean(mention.Answered) is { Length: > 0 } answered)
        {
            var post = await _db.PostDestinations.AsNoTracking()
                .Where(d => d.Network == PostNetworks.Bluesky && d.ExternalId == answered)
                .Join(_db.Posts.AsNoTracking(), d => d.PostId, p => p.Id, (d, p) => new { p.Id, p.Title, p.Text })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (post is not null)
            {
                data["postId"] = post.Id.ToString();
                data["title"] = PostEndpoints.Headline(post.Title, post.Text);
            }
        }

        await _partitions.EnsureForAsync(at, ct).ConfigureAwait(false);
        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = at,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = uri,
                Source = FactSource.Modbot,
                Data = data,
            },
            ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Text from outside Modbot, fit for the audit log: a NUL makes PostgreSQL refuse the whole row, so it and the
    /// other control characters (all but a new line and a tab) are dropped.
    /// </summary>
    internal static string? Clean(string? text)
    {
        if (text is null || !text.Any(IsControl))
            return text;

        return string.Concat(text.Where(c => !IsControl(c)));

        static bool IsControl(char c) => c < ' ' && c != '\n' && c != '\t';
    }

    /// <summary>The post on bsky.app, by the author's account id, so the link outlives a change of handle.</summary>
    public static string LinkTo(BlueskyMention mention)
    {
        var key = mention.Uri[(mention.Uri.LastIndexOf('/') + 1)..];
        var did = Uri.EscapeDataString(Clean(mention.AuthorDid)!).Replace("%3A", ":", StringComparison.Ordinal);
        return $"https://bsky.app/profile/{did}/post/{Uri.EscapeDataString(Clean(key)!)}";
    }

    // ── The mark, and what Health says ───────────────────────────────────────────────────

    private Task MovedOnAsync(DateTimeOffset indexedAt, CancellationToken ct) =>
        _db.Settings.Where(s => s.Id == 1 && (s.BlueskyRepliesReadAt == null || s.BlueskyRepliesReadAt < indexedAt))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyRepliesReadAt, indexedAt), ct);

    private Task SetErrorAsync(string reason, DateTimeOffset now, CancellationToken ct) =>
        _db.Settings.Where(s => s.Id == 1)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.BlueskyRepliesError, reason).SetProperty(s => s.BlueskyRepliesErrorAt, now),
                ct);

    private async Task ClearErrorAsync(string? error, CancellationToken ct)
    {
        if (error is null)
            return;

        await _db.Settings.Where(s => s.Id == 1)
            .ExecuteUpdateAsync(
                u => u.SetProperty(s => s.BlueskyRepliesError, (string?)null)
                    .SetProperty(s => s.BlueskyRepliesErrorAt, (DateTimeOffset?)null),
                ct).ConfigureAwait(false);
    }

    /// <summary>Kept for Health; a rate limit stops the pass until it resets, any other failure for <see cref="FailurePause"/>.</summary>
    private async Task FailedAsync(BlueskyAccess access, BlueskyFailure failure, DateTimeOffset now, CancellationToken ct)
    {
        var reason = BlueskyErrors.Sentence(failure);
        DateTimeOffset until;

        if (failure.StopsTheLane)
        {
            until = BlueskyErrors.StopUntil(failure, now);
            _log.Warning("Bluesky is limiting the reading of replies and mentions; they are not asked for again until the limit resets");
        }
        else
        {
            until = now + FailurePause;
            _log.Warning("Replies and mentions on Bluesky could not be read: {Reason}", reason);

            // The next sign-in is a refresh, not the token that was refused.
            if (failure.Problem is BlueskyProblem.TokenExpired or BlueskyProblem.TokenRefused)
                await _session.ExpiredAsync(_db, access.AccessJwt, ct).ConfigureAwait(false);
        }

        await _db.Settings
            .Where(s => s.Id == 1 && (s.BlueskyRepliesStoppedUntil == null || s.BlueskyRepliesStoppedUntil < until))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyRepliesStoppedUntil, until), ct).ConfigureAwait(false);

        await SetErrorAsync(reason, now, ct).ConfigureAwait(false);
    }
}
