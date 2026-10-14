using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Calendar;

/// <param name="Made">Posts made for dates that came due.</param>
/// <param name="Changed">Posts changed, cancelled or made ready to go again.</param>
/// <param name="Deleted">Posts taken down from Bluesky.</param>
public sealed record CalendarBlueskyPass(int Made, int Changed, int Deleted);

/// <summary>
/// Keeps an event's Bluesky posts in line with the event: one post for each date of an event with
/// Bluesky on, going a set time before the date starts, taken down when the event stops wanting it
/// (calendar design, Bluesky step).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The posts are ordinary posts.</strong> Each date is a <see cref="Post"/> of kind
/// <see cref="PostKinds.EventDate"/> with a Bluesky destination, and <see cref="PostBlueskySender"/>
/// sends it: the one record key picked before the first try, the read-back after a call with no clear
/// answer, the hourly cap, the late rule, the Posting switch, Pause all posting and the rate limit
/// that stops the lane. This pass only decides what the posts should be and takes them down. A date is
/// known by its start (<see cref="Post.DateStartsAt"/>), so the database allows one post for it, a
/// restart cannot make a second, and a date moved to another time is a different date: the old post
/// is taken down and the new one made.
/// </para>
/// <para>
/// <strong>A change that alters what the post says</strong> (title, words, time, world, picture,
/// link) is found by comparing what the calendar wants now with the fingerprint kept on the post
/// (<see cref="BlueskyPostOptions.Fingerprint"/>). A post that was not sent yet is rewritten. One that
/// went out is deleted on Bluesky, which cannot edit, and made again under a new record key. A cancelled
/// date, a cancelled or deleted event, or Bluesky switched off on the event takes its posts down and
/// makes none. A post a person deleted or cancelled in Marketing stays that way until the event changes.
/// </para>
/// <para>
/// <strong>Nothing happens while the Bluesky Posting switch is off</strong>, Pause all posting is on
/// or the account has not passed Check: no post is made, rewritten or deleted. A rate limit from
/// Bluesky stops the deletes too, and a delete is never sent again for a limit (CLAUDE.md); one that
/// failed otherwise waits <see cref="PostRules.NotSentRetryAfter"/>. Only dates that have not started
/// are looked at: a post for a date under way or past stays as it is, and a date whose post time went
/// by is posted right away, as long as the date has not started: its send time is set to now, so the
/// sender's late rule does not refuse it.
/// </para>
/// <para>
/// Quick edits fold into one change: an event is looked at once it has gone <see cref="SettleFor"/>
/// without being saved.
/// </para>
/// </remarks>
public sealed class CalendarBlueskyPlanner
{
    public static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(30);

    /// <summary>How many posts one pass takes down from Bluesky, at most: each is one call.</summary>
    public const int MostDeletesAPass = 3;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IBlueskyPostActions _bluesky;
    private readonly ILogger _log;

    public CalendarBlueskyPlanner(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IBlueskyPostActions bluesky,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(bluesky);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _bluesky = bluesky;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>What the calendar wants one date's post to be.</summary>
    private sealed record Want(
        DateTimeOffset StartsAt,
        string? Title,
        string Text,
        Guid? PictureId,
        DateTimeOffset SendAt,
        string Fingerprint,
        string TimeZone)
    {
        public bool Due(DateTimeOffset now) => SendAt <= now;

        /// <summary>
        /// The send time the post is given: the date's own, or now when that went by longer ago than a
        /// post may be late (an event made or changed inside its own lead time), so it goes at once.
        /// </summary>
        public DateTimeOffset SendAtAt(DateTimeOffset now) => SendAt >= now - PostRules.LateLimit ? SendAt : now;

        /// <summary>Whether <paramref name="post"/> already has a send time that gives this want.</summary>
        public bool Fits(Post post, DateTimeOffset now) => SendAt >= now - PostRules.LateLimit
            ? post.SendAt == SendAt
            : post.SendAt >= now - PostRules.LateLimit && post.SendAt <= now;
    }

    public async Task<CalendarBlueskyPass> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        if (settings is null)
            return new CalendarBlueskyPass(0, 0, 0);

        var sites = new PostSites(
            settings.PostsPaused,
            DiscordOn: false,
            DiscordSetUp: false,
            VRChatOn: false,
            VRChatSetUp: false,
            settings.BlueskyPostingOn,
            PostSites.BlueskyReady(settings));

        if (sites.HoldFor(PostNetworks.Bluesky) is not null || string.IsNullOrWhiteSpace(settings.BlueskyDid))
            return new CalendarBlueskyPass(0, 0, 0);

        var now = _clock.UtcNow;
        var run = new Run(settings.BlueskyDid.Trim(), now, settings.BlueskyStoppedUntil is { } until && now < until, ct);

        // Dates that have not started. A post for one that has is left as it is.
        var posts = await _db.Posts.Include(p => p.Destinations)
            .Where(p => p.Kind == PostKinds.EventDate && p.EventId != null && p.DateStartsAt > now)
            .ToListAsync(ct).ConfigureAwait(false);

        var wanting = await _db.CalendarEvents.AsNoTracking()
            .Where(e => e.PublishToBluesky
                && e.DeletedAt == null
                && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
            .Select(e => e.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = wanting.Union(posts.Select(p => p.EventId!.Value)).ToList();
        if (ids.Count == 0)
            return new CalendarBlueskyPass(0, 0, 0);

        var events = await _db.CalendarEvents.AsNoTracking()
            .Include(e => e.Categories)
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        var names = await CalendarEndpoints.FeedWorldNamesAsync(_db, events, ct).ConfigureAwait(false);
        await CalendarAutoLinks.ResolveAsync(_db, settings, events, ct).ConfigureAwait(false);

        foreach (var calendarEvent in events)
        {
            // Settling: a saved event is looked at once it has been left alone for a while.
            if (now - calendarEvent.UpdatedAt < SettleFor)
                continue;

            var wants = WantsOf(calendarEvent, names, settings.BlueskyPostTags, now);
            var mine = posts.Where(p => p.EventId == calendarEvent.Id).ToList();

            try
            {
                await ReconcileAsync(run, calendarEvent, mine, wants).ConfigureAwait(false);
            }
            catch (DbUpdateException ex)
            {
                // Somebody wrote a post since it was read, or the date already has its post. What this
                // pass holds is stale; the next one reads it again.
                _db.ChangeTracker.Clear();
                _log.Warning(ex, "A Bluesky post for event {EventId} was changed under the calendar; it will be looked at again", calendarEvent.Id);
                break;
            }
        }

        return new CalendarBlueskyPass(run.Made, run.Changed, run.Deleted);
    }

    // ── What the calendar wants ──────────────────────────────────────────────────────────

    /// <summary>
    /// The posts the event wants, by the start of their date: every date that has not started and
    /// starts within the longest "post it before" there is. Nothing when Bluesky is off on the event
    /// or the event is no longer scheduled.
    /// </summary>
    private static Dictionary<DateTimeOffset, Want> WantsOf(
        CalendarEvent calendarEvent, IReadOnlyDictionary<string, string> worldNames, string? everyPostTags, DateTimeOffset now)
    {
        var wants = new Dictionary<DateTimeOffset, Want>();

        if (!calendarEvent.PublishToBluesky
            || calendarEvent.DeletedAt is not null
            || !CalendarEventStates.IsLive(calendarEvent.State))
        {
            return wants;
        }

        var world = calendarEvent.WorldListId is null && calendarEvent.WorldId is { } id
            ? worldNames.GetValueOrDefault(id)
            : null;

        var tags = CalendarBlueskyText.TagsOf(calendarEvent, everyPostTags);
        var horizon = now + TimeSpan.FromMinutes(CalendarBlueskyText.BeforeChoices.Max()) + TimeSpan.FromTicks(1);

        foreach (var occurrence in CalendarRepeat.Between(calendarEvent, now, horizon))
        {
            if (occurrence.StartsAt <= now)
                continue;

            var text = CalendarBlueskyText.For(calendarEvent, occurrence, world, tags);

            // Bluesky shows the picture on a link card, so it goes only with a link.
            var picture = calendarEvent.CoverPictureId is { } cover && Modbot.Core.Bluesky.BlueskyText.CardLink(text) is not null
                ? cover
                : (Guid?)null;

            wants[occurrence.StartsAt] = new Want(
                occurrence.StartsAt,
                CalendarBlueskyText.TitleOf(calendarEvent, occurrence),
                text,
                picture,
                CalendarBlueskyText.SendAt(calendarEvent, occurrence),
                CalendarBlueskyText.Fingerprint(text, picture),
                calendarEvent.TimeZone);
        }

        return wants;
    }

    // ── Making the posts match ───────────────────────────────────────────────────────────

    private async Task ReconcileAsync(
        Run run, CalendarEvent calendarEvent, List<Post> posts, Dictionary<DateTimeOffset, Want> wants)
    {
        foreach (var post in posts)
        {
            var destination = post.Destinations.FirstOrDefault(d => d.Network == PostNetworks.Bluesky);
            if (destination is null || post.DateStartsAt is not { } date)
                continue;

            wants.Remove(date, out var want);
            await ApplyAsync(run, calendarEvent, post, destination, want).ConfigureAwait(false);
        }

        // A date with no post yet gets one once it is due, and not before: the pass would otherwise
        // make a row for every date of a long series. A date whose time went by longer ago than a post
        // may be late (an event made or changed inside its own lead time) is posted now: it has not
        // started, so the post is still useful, and Write sets its send time to now.
        foreach (var want in wants.Values
            .Where(w => w.Due(run.Now))
            .OrderBy(w => w.SendAt))
        {
            var post = new Post { Id = Guid.NewGuid(), CreatedAt = run.Now };
            var destination = new PostDestination { Id = Guid.NewGuid(), PostId = post.Id, Network = PostNetworks.Bluesky };
            post.Destinations.Add(destination);

            Write(run, calendarEvent, post, destination, want);
            _db.Posts.Add(post);

            await _db.SaveChangesAsync(run.Ct).ConfigureAwait(false);
            run.Made++;
        }
    }

    private async Task ApplyAsync(
        Run run, CalendarEvent calendarEvent, Post post, PostDestination destination, Want? want)
    {
        var options = PostTexts.BlueskyOptionsOf(destination);

        switch (destination.State)
        {
            case PostDestinationStates.Sending or PostDestinationStates.Checking:
                // On its way. Looked at again once the sender has an answer.
                return;

            case PostDestinationStates.Posted:
                await ApplyToPostedAsync(run, calendarEvent, post, destination, options, want).ConfigureAwait(false);
                return;

            case PostDestinationStates.Failed when destination.MayBeSent:
                // Bluesky may have it. A person presses Try again, which reads it back first.
                return;

            case PostDestinationStates.Waiting or PostDestinationStates.Failed:
                if (want is null)
                {
                    if (post.Status == PostStatuses.Scheduled)
                        await TurnOffAsync(run, post, destination, options).ConfigureAwait(false);

                    return;
                }

                // Rewritten while it waits; a failed one only when the event has changed since.
                if (destination.State == PostDestinationStates.Failed && options.Fingerprint == want.Fingerprint)
                    return;

                if (post.Status != PostStatuses.Scheduled
                    || options.Fingerprint != want.Fingerprint
                    || !want.Fits(post, run.Now)
                    || destination.State != PostDestinationStates.Waiting)
                {
                    Write(run, calendarEvent, post, destination, want);
                    await _db.SaveChangesAsync(run.Ct).ConfigureAwait(false);
                    run.Changed++;
                }

                return;

            default:
                // Removed or Skipped: by this pass, or by a person in Marketing. Made again only when the
                // event now wants something other than what the person turned away.
                if (want is not null && options.Fingerprint != want.Fingerprint)
                {
                    Write(run, calendarEvent, post, destination, want);
                    await _db.SaveChangesAsync(run.Ct).ConfigureAwait(false);
                    run.Changed++;
                }

                return;
        }
    }

    private async Task ApplyToPostedAsync(
        Run run, CalendarEvent calendarEvent, Post post, PostDestination destination, BlueskyPostOptions options, Want? want)
    {
        if (want is not null && options.Fingerprint == want.Fingerprint)
            return;

        // Taken down: Bluesky cannot edit. Not while Bluesky limits Modbot, not more than a few a pass,
        // and not before a failed try is due again.
        if (run.Stopped || run.Deleted >= MostDeletesAPass || destination.CheckAt is { } notBefore && notBefore > run.Now)
            return;

        var key = destination.ClientKey is { Length: > 0 } own
            ? own
            : destination.ExternalId?.Split('/').LastOrDefault() ?? string.Empty;

        if (key.Length == 0)
            return;

        run.Deleted++;
        var outcome = await _bluesky.DeleteAsync(destination.Target, key, run.Ct).ConfigureAwait(false);

        if (!outcome.Done)
        {
            // A limit has stopped the lane and is not tried again; any other failure waits a while. Either
            // way no more calls are made this pass.
            run.Stopped = true;
            destination.Error = outcome.Error is { Length: > 1024 } tooLong ? tooLong[..1024] : outcome.Error;
            destination.ErrorAt = run.Now;
            destination.CheckAt = run.Now + PostRules.NotSentRetryAfter;
            await _db.SaveChangesAsync(run.Ct).ConfigureAwait(false);

            _log.Warning("A Bluesky post for event {EventId} could not be taken down: {Error}", calendarEvent.Id, outcome.Error);
            return;
        }

        var link = destination.Link;

        destination.State = PostDestinationStates.Removed;
        destination.CheckAt = null;
        destination.Error = null;
        destination.ErrorAt = null;
        destination.UpdatedAt = run.Now;
        destination.Options = PostTexts.WriteBlueskyOptions(options with { Cid = null, Fingerprint = string.Empty });
        post.UpdatedAt = run.Now;
        post.Version++;

        // Wanted still, with other words: the new post goes out under a new key.
        if (want is not null)
            Write(run, calendarEvent, post, destination, want);

        await _db.SaveChangesAsync(run.Ct).ConfigureAwait(false);
        run.Changed++;

        await WriteFactAsync(post, run.Now, new JsonObject
        {
            ["title"] = post.Title,
            ["network"] = PostNetworks.Bluesky,
            ["by"] = null,
            ["link"] = link,
            ["automatic"] = true,
            ["eventId"] = calendarEvent.Id.ToString(),
            ["reason"] = want is null ? "The event no longer has this post." : "The event changed.",
        }).ConfigureAwait(false);
    }

    /// <summary>A post not sent yet that the event no longer wants: skipped, and remembered as not wanted.</summary>
    private async Task TurnOffAsync(Run run, Post post, PostDestination destination, BlueskyPostOptions options)
    {
        PostChanges.Cancel(post, run.Now);
        destination.State = PostDestinationStates.Skipped;
        destination.UpdatedAt = run.Now;
        destination.Options = PostTexts.WriteBlueskyOptions(options with { Fingerprint = string.Empty });

        await _db.SaveChangesAsync(run.Ct).ConfigureAwait(false);
        run.Changed++;
    }

    /// <summary>
    /// Sets the post and its destination to the words the calendar wants and makes it ready to go.
    /// A destination that held a post once, or failed, starts again under a new record key.
    /// </summary>
    private static void Write(Run run, CalendarEvent calendarEvent, Post post, PostDestination destination, Want want)
    {
        post.Kind = PostKinds.EventDate;
        post.EventId = calendarEvent.Id;
        post.DateStartsAt = want.StartsAt;
        post.Title = want.Title;
        post.Text = want.Text;
        post.PictureId = want.PictureId;
        post.TimeZone = want.TimeZone;
        post.Status = PostStatuses.Scheduled;
        post.SendAt = want.SendAtAt(run.Now);
        post.CancelledAt = null;
        post.UpdatedAt = run.Now;
        post.Version++;

        destination.Target = run.Did;
        destination.TitleOverride = null;
        destination.TextOverride = want.Text;
        destination.SitePictureId = want.PictureId;
        destination.Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions(want.PictureId, null, false, want.Fingerprint));

        if (destination.State != PostDestinationStates.Waiting || destination.ClientKey is null)
        {
            destination.State = PostDestinationStates.Waiting;
            destination.ClientKey = null;
            destination.ExternalId = null;
            destination.Link = null;
            destination.SentTitle = null;
            destination.SentText = null;
            destination.SentAt = null;
            destination.PostedAt = null;
            destination.MayBeSent = false;
            destination.SendIfMissing = false;
            destination.CheckAt = null;
            destination.Error = null;
            destination.ErrorAt = null;
            destination.MissingPermission = null;
        }

        destination.UpdatedAt = run.Now;
    }

    private async Task WriteFactAsync(Post post, DateTimeOffset now, JsonObject data)
    {
        await _partitions.EnsureForAsync(now, default).ConfigureAwait(false);
        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.PostRemoved,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = post.Id.ToString(),
                Source = FactSource.Modbot,
                Data = data,
            },
            default).ConfigureAwait(false);
    }

    private sealed class Run(string did, DateTimeOffset now, bool stopped, CancellationToken ct)
    {
        public string Did { get; } = did;

        public DateTimeOffset Now { get; } = now;

        /// <summary>Bluesky is limiting Modbot: nothing more is asked of it this pass.</summary>
        public bool Stopped { get; set; } = stopped;

        public CancellationToken Ct { get; } = ct;

        public int Made { get; set; }

        public int Changed { get; set; }

        public int Deleted { get; set; }
    }
}
