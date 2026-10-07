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

/// <param name="Sent">Posts that went out this pass, read-backs that found one included.</param>
/// <param name="Looked">Read-backs made for a post that got no clear answer.</param>
/// <param name="Failed">Destinations that turned Failed this pass.</param>
public sealed record PostBlueskyPass(int Sent, int Looked, int Failed);

/// <summary>
/// Sends posts to Bluesky at their time, and reads back the ones Bluesky gave no clear answer to
/// (Bluesky design §3.4, posts design §4.2c).
/// </summary>
/// <remarks>
/// <para>
/// <strong>At most once, by a key Modbot picks.</strong> Before the first try the destination is given
/// a record key (a <see cref="Tid"/>) and claimed (<see cref="PostClaim"/>) with the text about to go
/// out. Every try is a <c>putRecord</c> at that key with <c>swapRecord: null</c>, which makes the post
/// only if nothing is at the key. So a late first write and a second try cannot both land: the second
/// gets <c>InvalidSwap</c>, and the key is read back and the post taken as Posted.
/// </para>
/// <para>
/// <strong>No "Unknown".</strong> A timeout or a 5xx leaves the destination Checking (shown as
/// Sending…). A minute later the key is read back with <c>getRecord</c>: found, Posted; not found,
/// it waits to be sent again under the same key, with a fresh <c>createdAt</c> (feeds sort by it);
/// the read failed, it is read again later, and an hour after the looking began it is Failed, "Could
/// not reach Bluesky.", and Try again reads back first.
/// </para>
/// <para>
/// <strong>A rate limit stops the lane.</strong> A 429 on any call stops every call to Bluesky until
/// the time Bluesky says it resets, and nothing is sent again before then (CLAUDE.md). A 429 made
/// nothing, so the post waits.
/// </para>
/// <para>
/// One post a pass, at most <see cref="PostRules.PerSitePerHour"/> an hour, the same late rule as
/// every site, and held by Pause all posting, the Bluesky Posting switch and an account that has not
/// passed Check (<see cref="PostSites.BlueskyReady"/>). Mentions are never made: an <c>@handle</c>
/// stays plain text (<see cref="BlueskyText"/>).
/// </para>
/// </remarks>
public sealed class PostBlueskySender
{
    /// <summary>How many read-backs one pass makes, at most.</summary>
    public const int LooksPerPass = 2;

    public const string Unreachable = "Could not reach Bluesky.";
    public const string NoText = "The post has no text.";
    public const string AccountChanged = "That Bluesky account is not the one in Settings.";
    public const string KeyTaken = "Bluesky says this post is already there, but could not show it.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly PostClaim _claim;
    private readonly BlueskySession _session;
    private readonly BlueskyClient _client;
    private readonly BlueskyIdentity _identity;
    private readonly ILogger _log;

    public PostBlueskySender(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        PostClaim claim,
        BlueskySession session,
        BlueskyClient client,
        BlueskyIdentity identity,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(identity);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _claim = claim;
        _session = session;
        _client = client;
        _identity = identity;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<PostBlueskyPass> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        if (settings is null)
            return new PostBlueskyPass(0, 0, 0);

        var sites = new PostSites(
            settings.PostsPaused,
            settings.DiscordPostsOn,
            DiscordSetUp: false,
            settings.VRChatPostsOn,
            VRChatSetUp: false,
            settings.BlueskyPostingOn,
            PostSites.BlueskyReady(settings));

        var pass = new Pass(settings, _clock.UtcNow, ct);

        await SettleStuckAsync(pass).ConfigureAwait(false);

        // While Bluesky limits Modbot nothing is asked of it, a read-back included.
        if (!pass.Stopped)
            await LookAsync(pass).ConfigureAwait(false);

        await FailLateAsync(pass).ConfigureAwait(false);

        if (!pass.Stopped && sites.HoldFor(PostNetworks.Bluesky) is null)
            await SendOneAsync(pass).ConfigureAwait(false);

        return new PostBlueskyPass(pass.Sent, pass.Looked, pass.Failed);
    }

    // ── Cut off mid-send ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A destination left Sending -- the process stopped between the claim and the answer -- may be
    /// on Bluesky. It is read back, and sent again under the same key only if it is not there.
    /// </summary>
    private async Task SettleStuckAsync(Pass pass)
    {
        var before = pass.Now - PostRules.StuckSendingAfter;

        var stuck = await _db.PostDestinations
            .Where(d => d.Network == PostNetworks.Bluesky
                && d.State == PostDestinationStates.Sending
                && (d.SentAt == null || d.SentAt < before))
            .ToListAsync(pass.Ct).ConfigureAwait(false);

        if (stuck.Count == 0)
            return;

        foreach (var destination in stuck)
        {
            destination.State = PostDestinationStates.Checking;
            destination.MayBeSent = true;
            destination.CheckAt = pass.Now;
            destination.SentAt ??= pass.Now;
            destination.UpdatedAt = pass.Now;
        }

        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
    }

    // ── The read-back ────────────────────────────────────────────────────────────────────

    private async Task LookAsync(Pass pass)
    {
        var due = await _db.PostDestinations
            .Where(d => d.Network == PostNetworks.Bluesky
                && d.State == PostDestinationStates.Checking
                && (d.CheckAt == null || d.CheckAt <= pass.Now))
            .OrderBy(d => d.CheckAt)
            .Take(LooksPerPass)
            .ToListAsync(pass.Ct).ConfigureAwait(false);

        foreach (var destination in due)
        {
            if (pass.Stopped)
                return;

            var post = await _db.Posts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == destination.PostId, pass.Ct).ConfigureAwait(false);

            if (post is null)
                continue;

            await LookOnceAsync(pass, post, destination).ConfigureAwait(false);
        }
    }

    private async Task LookOnceAsync(Pass pass, Post post, PostDestination destination)
    {
        if (destination.ClientKey is not { Length: > 0 } key || destination.Target.Length == 0)
        {
            // Nothing was ever sent without a key, so there is nothing to find.
            await FailAsync(pass, post, destination, Unreachable, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        pass.Looked++;

        var read = await ReadBackAsync(pass, destination.Target, key).ConfigureAwait(false);

        if (read.Found is { } found)
        {
            await PostedAsync(pass, post, destination, found, adopted: true).ConfigureAwait(false);
            _log.Information("Bluesky post {PostId} had gone through after all", post.Id);
            return;
        }

        if (read.Failure is { StopsTheLane: true } limited)
        {
            var until = await StopAsync(pass, limited).ConfigureAwait(false);
            destination.CheckAt = until;
            await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
            return;
        }

        if (read.NotThere && PostTexts.BlueskyOptionsOf(destination).KeyTaken)
        {
            // Bluesky once said something is at this key. Not finding it now proves nothing, so it is
            // never sent again, whoever asks: it stays one Bluesky may have.
            await FailAsync(pass, post, destination, KeyTaken, mayBeSent: true).ConfigureAwait(false);
            return;
        }

        if (read.NotThere)
        {
            var attempt = destination.SentAt ?? pass.Now;

            // Not on Bluesky: sent again under the same key, which can never make a second post.
            // A person's Try again starts the hour again; otherwise the hour counts from the attempt,
            // and one past it is not sent late.
            if (destination.SendIfMissing || pass.Now - attempt <= PostRules.LateLimitFor(post))
            {
                destination.State = PostDestinationStates.Waiting;
                destination.MayBeSent = false;
                destination.CheckAt = null;
                destination.Error = null;
                destination.ErrorAt = null;
                destination.UpdatedAt = destination.SendIfMissing ? pass.Now : attempt;
                destination.SendIfMissing = false;
                await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
                return;
            }

            await FailAsync(pass, post, destination, Unreachable, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        // The read could not be made. An hour from when the looking began, it is Failed, still as one
        // Bluesky may have: Try again reads back first.
        if (pass.Now - destination.UpdatedAt >= PostRules.StopLookingAfter)
        {
            await FailAsync(pass, post, destination, Unreachable, mayBeSent: true).ConfigureAwait(false);
            return;
        }

        destination.CheckAt = pass.Now + PostRules.NotSentRetryAfter;
        destination.Error = Trim(read.Failure is { } failure ? BlueskyErrors.Sentence(failure) : Unreachable);
        destination.ErrorAt = pass.Now;
        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);
    }

    /// <summary>What a read-back of a key found.</summary>
    /// <param name="Found">The post at the key.</param>
    /// <param name="NotThere">Bluesky said nothing is at the key.</param>
    /// <param name="Failure">Why the read could not be made.</param>
    private sealed record ReadBack(BlueskyRecordRef? Found, bool NotThere, BlueskyFailure? Failure);

    /// <summary>
    /// Reads the key back on the account's own server, with no sign-in. The account in Settings is
    /// read where Settings says; a post sent from an account changed since is read where its own
    /// document says.
    /// </summary>
    private async Task<ReadBack> ReadBackAsync(Pass pass, string did, string key)
    {
        Uri server;

        if (string.Equals(did, pass.Did, StringComparison.Ordinal) && pass.Server is { } own)
        {
            server = own;
        }
        else
        {
            var found = await _identity.ServerOfAsync(did, pass.Ct).ConfigureAwait(false);
            if (found.Value is not { } other)
                return new ReadBack(null, false, found.Failure);

            server = other;
        }

        var read = await _client.GetRecordAsync(server, did, BlueskyClient.PostCollection, key, pass.Ct).ConfigureAwait(false);

        if (read.Failure is null)
            return new ReadBack(read.Value.Ref, false, null);

        return read.Failure.Problem == BlueskyProblem.RecordNotFound
            ? new ReadBack(null, true, null)
            : new ReadBack(null, false, read.Failure);
    }

    // ── Too late ─────────────────────────────────────────────────────────────────────────

    private async Task FailLateAsync(Pass pass)
    {
        var waiting = await _db.PostDestinations
            .Join(_db.Posts, d => d.PostId, p => p.Id, (d, p) => new { Destination = d, Post = p })
            .Where(x => x.Destination.Network == PostNetworks.Bluesky
                && x.Destination.State == PostDestinationStates.Waiting
                && x.Post.Status == PostStatuses.Scheduled
                && x.Post.SendAt != null
                && x.Post.SendAt <= pass.Now)
            .ToListAsync(pass.Ct).ConfigureAwait(false);

        foreach (var row in waiting.Where(x => PostRules.IsLate(x.Post, x.Destination, pass.Now)))
            await FailAsync(pass, row.Post, row.Destination, PostRules.NotSentOnTime, mayBeSent: false).ConfigureAwait(false);
    }

    // ── The send ─────────────────────────────────────────────────────────────────────────

    private async Task SendOneAsync(Pass pass)
    {
        var hourAgo = pass.Now - TimeSpan.FromHours(1);
        var sentLastHour = await _db.PostDestinations
            .CountAsync(d => d.Network == PostNetworks.Bluesky && d.SentAt != null && d.SentAt > hourAgo, pass.Ct)
            .ConfigureAwait(false);

        if (!PostRules.UnderHourlyCap(sentLastHour))
            return;

        // The soonest due first. Tracked: the claim saves these two as they were read.
        var next = await _db.PostDestinations
            .Join(_db.Posts, d => d.PostId, p => p.Id, (d, p) => new { Destination = d, Post = p })
            .Where(x => x.Destination.Network == PostNetworks.Bluesky
                && x.Destination.State == PostDestinationStates.Waiting
                && x.Post.Status == PostStatuses.Scheduled
                && x.Post.SendAt != null
                && x.Post.SendAt <= pass.Now
                // One that could not go a moment ago waits its turn, so the posts behind it go.
                && (x.Destination.CheckAt == null || x.Destination.CheckAt <= pass.Now))
            .OrderBy(x => x.Post.SendAt)
            .FirstOrDefaultAsync(pass.Ct).ConfigureAwait(false);

        if (next is null || PostRules.IsLate(next.Post, next.Destination, pass.Now))
            return;

        var post = next.Post;
        var destination = next.Destination;
        var text = PostTexts.Bluesky(post, destination);

        if (text.Length == 0)
        {
            await FailAsync(pass, post, destination, NoText, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        if (!BlueskyText.Fits(text))
        {
            await FailAsync(pass, post, destination, BlueskyText.TooLong, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        // Checked again at send time, not only when it was saved: only the account in Settings.
        if (pass.Did is null || !string.Equals(destination.Target, pass.Did, StringComparison.Ordinal))
        {
            await FailAsync(pass, post, destination, AccountChanged, mayBeSent: false).ConfigureAwait(false);
            return;
        }

        // When it started waiting, kept for a send that turns out not to have been made.
        var waitingSince = destination.UpdatedAt;

        // Belt and braces: a key Bluesky once called taken is never put to again.
        if (PostTexts.BlueskyOptionsOf(destination).KeyTaken)
        {
            await FailAsync(pass, post, destination, KeyTaken, mayBeSent: true).ConfigureAwait(false);
            return;
        }

        var signIn = await _session.AccessAsync(_db, prove: false, pass.Ct).ConfigureAwait(false);
        if (signIn.Access is not { } access)
        {
            // Nothing was sent. It waits, and is not tried again for a while, so the posts behind it
            // are not held up; a limit waits until it resets.
            pass.Stopped |= signIn.Hold == BlueskyHold.Stopped;
            await NotSentAsync(pass, destination, signIn.Problem, waitingSince, signIn.Until).ConfigureAwait(false);
            return;
        }

        // The key is made once, and kept for every try after: the heart of at most once.
        destination.ClientKey ??= Tid.Next(_clock);
        var key = destination.ClientKey;

        // Written first. Nothing is sent unless this went through.
        if (!await _claim.ClaimAsync(post, destination, null, text, pass.Ct).ConfigureAwait(false))
            return;

        var card = BlueskyPostRecord.CardFor(PostTexts.TidyTitle(post.Title), text);
        JsonObject? thumb = null;

        if (card is not null && PostTexts.BlueskyPictureFor(post, destination, text) is { } pictureId)
        {
            var picture = await _db.CalendarCoverPictures.AsNoTracking()
                .Where(c => c.Id == pictureId)
                .Select(c => new { c.Bytes, c.ContentType })
                .FirstOrDefaultAsync(pass.Ct).ConfigureAwait(false);

            if (picture is not null && picture.Bytes.Length <= BlueskyText.CardPictureMaxBytes)
            {
                // The same bytes give the same blob, so an upload made again is harmless.
                var uploaded = await _client.UploadBlobAsync(access, picture.Bytes, picture.ContentType, pass.Ct)
                    .ConfigureAwait(false);

                if (uploaded.Failure is { } refused)
                {
                    await RefusedBeforePutAsync(pass, post, destination, access, refused, waitingSince).ConfigureAwait(false);
                    return;
                }

                thumb = uploaded.Value;
            }
        }

        // createdAt is this send's own time: feeds sort by it (fact 21).
        var record = BlueskyPostRecord.Build(text, pass.Now, card, thumb);

        var put = await _client.PutRecordAsync(access, access.Did, BlueskyClient.PostCollection, key, record, pass.Ct)
            .ConfigureAwait(false);

        if (put.Value is { } made)
        {
            await PostedAsync(pass, post, destination, made, adopted: false).ConfigureAwait(false);
            return;
        }

        var failure = put.Failure!;

        if (failure.Problem == BlueskyProblem.InvalidSwap)
        {
            // Something is at the key, and only Modbot ever puts a post there: an earlier try landed.
            var read = await ReadBackAsync(pass, access.Did, key).ConfigureAwait(false);
            if (read.Found is { } found)
            {
                await PostedAsync(pass, post, destination, found, adopted: true).ConfigureAwait(false);
                return;
            }

            // Bluesky said the key is taken, and now cannot show what is there. The post may well be
            // on Bluesky, so from here on it is only ever read back, never sent again: not by this
            // loop, and not by Try again (posts design §4.2c).
            destination.Options = PostTexts.WriteBlueskyOptions(PostTexts.BlueskyOptionsOf(destination) with { KeyTaken = true });

            if (read.NotThere)
            {
                await FailAsync(pass, post, destination, KeyTaken, mayBeSent: true).ConfigureAwait(false);
                return;
            }

            await CheckingAsync(pass, post, destination, failure).ConfigureAwait(false);
            return;
        }

        if (failure.Unclear)
        {
            await CheckingAsync(pass, post, destination, failure).ConfigureAwait(false);
            return;
        }

        await RefusedBeforePutAsync(pass, post, destination, access, failure, waitingSince).ConfigureAwait(false);
    }

    /// <summary>
    /// A call that made no post: a rate limit (the lane stops), a token that ended (the session is
    /// refreshed next pass), no answer to the picture upload, or a refusal of the post itself.
    /// </summary>
    private async Task RefusedBeforePutAsync(
        Pass pass, Post post, PostDestination destination, BlueskyAccess access, BlueskyFailure failure, DateTimeOffset waitingSince)
    {
        if (failure.StopsTheLane)
        {
            var until = await StopAsync(pass, failure).ConfigureAwait(false);
            await NotSentAsync(pass, destination, BlueskyErrors.Limited, waitingSince, until).ConfigureAwait(false);
            return;
        }

        if (failure.Problem is BlueskyProblem.TokenExpired or BlueskyProblem.TokenRefused)
        {
            await _session.ExpiredAsync(_db, access.AccessJwt, pass.Ct).ConfigureAwait(false);
            await NotSentAsync(pass, destination, BlueskyErrors.Sentence(failure), waitingSince, pass.Now).ConfigureAwait(false);
            return;
        }

        if (failure.Unclear)
        {
            await NotSentAsync(pass, destination, BlueskyErrors.Sentence(failure), waitingSince, null).ConfigureAwait(false);
            return;
        }

        // A refusal of this post: sending it again would get the same answer.
        await FailAsync(pass, post, destination, BlueskyErrors.Sentence(failure), mayBeSent: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Nothing was made: back to waiting, its waiting time unchanged (so a post that never gets
    /// through still turns Failed once it is an hour late), not tried again before <paramref name="until"/>,
    /// or for <see cref="PostRules.NotSentRetryAfter"/> when that is null.
    /// </summary>
    private async Task NotSentAsync(Pass pass, PostDestination destination, string? why, DateTimeOffset waitingSince, DateTimeOffset? until)
    {
        destination.State = PostDestinationStates.Waiting;
        destination.Error = Trim(why);
        destination.ErrorAt = pass.Now;
        destination.CheckAt = until is { } at && at > pass.Now ? at : pass.Now + PostRules.NotSentRetryAfter;
        destination.UpdatedAt = waitingSince;
        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

        _log.Information("Bluesky post {PostDestinationId} waits: {Reason}", destination.Id, destination.Error);
    }

    /// <summary>No clear answer: read back a minute after the attempt. Sent again only if it is not there.</summary>
    private async Task CheckingAsync(Pass pass, Post post, PostDestination destination, BlueskyFailure failure)
    {
        destination.State = PostDestinationStates.Checking;
        destination.MayBeSent = true;
        destination.CheckAt = (destination.SentAt ?? pass.Now) + PostRules.FirstLookAfter + TimeSpan.FromSeconds(1);
        destination.UpdatedAt = pass.Now;
        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

        _log.Warning(
            "Bluesky gave no clear answer to post {PostId} ({Status}); Modbot will read it back: {Reason}",
            post.Id, failure.Status, BlueskyErrors.Sentence(failure));
    }

    private Task<DateTimeOffset> StopAsync(Pass pass, BlueskyFailure failure)
    {
        pass.Stopped = true;
        _log.Warning("Bluesky is limiting Modbot; nothing is sent to it until the limit resets");
        return _session.StopAsync(_db, failure, pass.Ct);
    }

    // ── Outcomes ─────────────────────────────────────────────────────────────────────────

    private async Task PostedAsync(Pass pass, Post post, PostDestination destination, BlueskyRecordRef made, bool adopted)
    {
        var key = destination.ClientKey!;

        destination.State = PostDestinationStates.Posted;
        destination.ExternalId = made.Uri;
        destination.Link = BlueskyText.PostLink(destination.Target, key);
        destination.Options = PostTexts.WriteBlueskyOptions(PostTexts.BlueskyOptionsOf(destination) with { Cid = made.Cid.Length > 0 ? made.Cid : null });
        destination.PostedAt = pass.Now;
        destination.MayBeSent = false;
        destination.SendIfMissing = false;
        destination.CheckAt = null;
        destination.Error = null;
        destination.ErrorAt = null;
        destination.MissingPermission = null;
        destination.UpdatedAt = pass.Now;

        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

        pass.Sent++;

        await WriteFactAsync(FactType.PostSent, post, pass.Now, new JsonObject
        {
            ["title"] = post.Title,
            ["network"] = PostNetworks.Bluesky,
            ["handle"] = pass.Handle,
            ["link"] = destination.Link,
            ["externalId"] = made.Uri,
            ["sentText"] = destination.SentText,
            ["adopted"] = adopted,
            ["scheduledBy"] = post.CreatedByUserId?.ToString(),
        }, pass.Ct).ConfigureAwait(false);
    }

    private async Task FailAsync(Pass pass, Post post, PostDestination destination, string error, bool mayBeSent)
    {
        destination.State = PostDestinationStates.Failed;
        destination.Error = Trim(error);
        destination.ErrorAt = pass.Now;
        destination.MissingPermission = null;
        destination.MayBeSent = mayBeSent;
        destination.SendIfMissing = false;
        destination.CheckAt = null;
        destination.UpdatedAt = pass.Now;

        await _db.SaveChangesAsync(pass.Ct).ConfigureAwait(false);

        pass.Failed++;
        _log.Warning("Post {PostId} did not go to Bluesky: {Error}", post.Id, destination.Error);

        await WriteFactAsync(FactType.PostFailed, post, pass.Now, new JsonObject
        {
            ["title"] = post.Title,
            ["network"] = PostNetworks.Bluesky,
            ["handle"] = pass.Handle,
            ["error"] = destination.Error,
        }, pass.Ct).ConfigureAwait(false);
    }

    private async Task WriteFactAsync(string type, Post post, DateTimeOffset now, JsonObject data, CancellationToken ct)
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
                Data = data,
            },
            ct).ConfigureAwait(false);
    }

    private static string? Trim(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];

    private sealed class Pass(Core.Data.Entities.Settings settings, DateTimeOffset now, CancellationToken ct)
    {
        /// <summary>The account in Settings, or null when there is none.</summary>
        public string? Did { get; } = string.IsNullOrWhiteSpace(settings.BlueskyDid) ? null : settings.BlueskyDid;

        public Uri? Server { get; } = BlueskyIdentity.ServerAddress(settings.BlueskyServer);

        public string? Handle { get; } = settings.BlueskyHandle;

        /// <summary>Bluesky is limiting Modbot: nothing more is asked of it this pass.</summary>
        public bool Stopped { get; set; } = settings.BlueskyStoppedUntil is { } until && now < until;

        public DateTimeOffset Now { get; } = now;

        public CancellationToken Ct { get; } = ct;

        public int Sent { get; set; }

        public int Looked { get; set; }

        public int Failed { get; set; }
    }
}
