using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// Makes the "We're live" reply under a date's Bluesky post when the first person is in the instance
/// Modbot opened for the event (calendar design, Bluesky extras).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The reply is an ordinary post.</strong> A <see cref="Post"/> of kind <see cref="PostKinds.Live"/>
/// with a Bluesky destination whose <see cref="PostDestination.ReplyToId"/> is the date post's
/// destination; <see cref="PostBlueskySender"/> sends it, with every rule it has (the hourly cap, the
/// late rule, the Posting switch, Pause all posting, the rate limit that stops the lane). The database
/// allows one such post for a date, so a restart cannot make a second.
/// </para>
/// <para>
/// <strong>Once per occurrence.</strong> <see cref="CalendarOpening.FirstJoinBlueskyPostedAt"/> is
/// written before the reply is made. No Bluesky post for the date, or one that is not on Bluesky, means
/// no reply, with the reason kept on the opening. A reply Bluesky refused has its words copied to the
/// opening too.
/// </para>
/// <para>
/// The link is the instance's join link when Who can join is Anyone, and the group's page otherwise,
/// so nobody is handed a way into an instance they could not enter.
/// </para>
/// </remarks>
public sealed class CalendarBlueskyFirstJoin
{
    /// <summary>The event's Who can join when anyone may. The same word as <c>CalendarJoinLink.AnyoneCanJoin</c>, which this project cannot reach.</summary>
    private const string AnyoneCanJoin = "public";

    /// <summary>The longest one occurrence may last; openings older than this cannot still be running.</summary>
    private static readonly TimeSpan LongestOccurrence = TimeSpan.FromDays(7);

    public const string NoDatePost = "There is no Bluesky post for this date to reply to.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public CalendarBlueskyFirstJoin(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The reply's words: the world when it is known, then the link when there is one.</summary>
    public static string Text(string? worldName, string? link)
    {
        var words = PostTexts.TidyTitle(worldName) is { } world ? $"We're live in {world}." : "We're live.";
        return link is null ? words : $"{words}\n{link}";
    }

    /// <returns>How many replies were made.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        if (settings is null)
            return 0;

        var sites = new PostSites(
            settings.PostsPaused,
            DiscordOn: false,
            DiscordSetUp: false,
            VRChatOn: false,
            VRChatSetUp: false,
            settings.BlueskyPostingOn,
            PostSites.BlueskyReady(settings));

        // Nothing is made while Bluesky is held: the first join is looked at again once it is not.
        if (sites.HoldFor(PostNetworks.Bluesky) is not null || string.IsNullOrWhiteSpace(settings.BlueskyDid))
            return 0;

        var now = _clock.UtcNow;
        var since = now - LongestOccurrence;

        await CopyRefusalsAsync(since, ct).ConfigureAwait(false);

        var openings = await _db.CalendarOpenings
            .Where(o => o.Location != null && o.InstanceId != null && o.FirstJoinBlueskyPostedAt == null && o.OccurrenceStartsAt > since)
            .ToListAsync(ct).ConfigureAwait(false);

        var made = 0;

        foreach (var opening in openings)
        {
            var calendarEvent = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == opening.EventId, ct).ConfigureAwait(false);

            if (calendarEvent is null
                || !calendarEvent.AnnounceFirstJoinOnBluesky
                || calendarEvent.DeletedAt is not null
                || !CalendarEventStates.IsLive(calendarEvent.State))
            {
                continue;
            }

            if (now >= opening.OccurrenceStartsAt + (calendarEvent.EndsAt - calendarEvent.StartsAt))
                continue;

            var instance = await _db.VRChatInstances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == opening.InstanceId, ct).ConfigureAwait(false);

            if (instance is null || instance.ClosedAt is not null)
                continue;

            // The group instance poll's own count: nothing here asks VRChat who is there.
            if ((instance.LastUserCount ?? 0) <= 0 && (instance.PeakUserCount ?? 0) <= 0)
                continue;

            var datePost = await _db.Posts.AsNoTracking().Include(p => p.Destinations)
                .FirstOrDefaultAsync(
                    p => p.Kind == PostKinds.EventDate && p.EventId == calendarEvent.Id && p.DateStartsAt == opening.OccurrenceStartsAt,
                    ct).ConfigureAwait(false);

            var parent = datePost?.Destinations.FirstOrDefault(d => d.Network == PostNetworks.Bluesky);

            // On its way: the reply waits for it (the late rule ends a wait that goes nowhere).
            if (parent?.State is PostDestinationStates.Waiting or PostDestinationStates.Sending or PostDestinationStates.Checking)
                continue;

            // Written first: once, however many restarts.
            opening.FirstJoinBlueskyPostedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (parent is not { State: PostDestinationStates.Posted })
            {
                opening.FirstJoinBlueskyPostError = NoDatePost;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                _log.Information("No Bluesky reply for event {EventId}: it has no post for this date", calendarEvent.Id);
                continue;
            }

            // A reply already made for this date stands.
            if (await _db.Posts.AnyAsync(
                    p => p.Kind == PostKinds.Live && p.EventId == calendarEvent.Id && p.DateStartsAt == opening.OccurrenceStartsAt, ct)
                .ConfigureAwait(false))
            {
                continue;
            }

            var link = await LinkAsync(settings, calendarEvent, opening, ct).ConfigureAwait(false);
            var text = Text(await WorldNameAsync(opening.Location, ct).ConfigureAwait(false), link);

            var post = new Post
            {
                Id = Guid.NewGuid(),
                Title = PostTexts.TidyTitle(calendarEvent.Title),
                Text = text,
                Status = PostStatuses.Scheduled,
                SendAt = now,
                TimeZone = calendarEvent.TimeZone,
                EventId = calendarEvent.Id,
                Kind = PostKinds.Live,
                DateStartsAt = opening.OccurrenceStartsAt,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };

            post.Destinations.Add(new PostDestination
            {
                Id = Guid.NewGuid(),
                PostId = post.Id,
                Network = PostNetworks.Bluesky,
                Target = settings.BlueskyDid.Trim(),
                TextOverride = text,
                Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions()),
                State = PostDestinationStates.Waiting,
                ReplyToId = parent.Id,
                UpdatedAt = now,
            });

            try
            {
                _db.Posts.Add(post);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                made++;
            }
            catch (DbUpdateException ex)
            {
                // The date already has its reply, made since the check above.
                _db.ChangeTracker.Clear();
                _log.Warning(ex, "The Bluesky reply for event {EventId} was made by someone else first", calendarEvent.Id);
                break;
            }
        }

        return made;
    }

    /// <summary>The join link when anyone may join, else the group's page; null when neither exists.</summary>
    private async Task<string?> LinkAsync(
        Core.Data.Entities.Settings settings, CalendarEvent calendarEvent, CalendarOpening opening, CancellationToken ct)
    {
        if (calendarEvent.AccessType == AnyoneCanJoin)
            return Modbot.Core.Data.InstanceJoinLink.For(opening.Location);

        await CalendarAutoLinks.ResolveAsync(_db, settings, [calendarEvent], ct).ConfigureAwait(false);
        return CalendarAutoLinks.For(calendarEvent, CalendarSite.Bluesky, ofADate: true)?.VRChatGroupPage;
    }

    /// <summary>The name Modbot knows for the world the instance is in, or null.</summary>
    private async Task<string?> WorldNameAsync(string? location, CancellationToken ct)
    {
        var colon = location?.IndexOf(':') ?? -1;
        if (location is null || colon <= 0)
            return null;

        var worldId = location[..colon];

        return await _db.VRChatWorlds.AsNoTracking()
            .Where(w => w.WorldId == worldId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>What Bluesky said when it turned a reply away goes on the opening, where the event shows it.</summary>
    private async Task CopyRefusalsAsync(DateTimeOffset since, CancellationToken ct)
    {
        var refused = await _db.Posts.AsNoTracking().Include(p => p.Destinations)
            .Where(p => p.Kind == PostKinds.Live
                && p.EventId != null
                && p.CreatedAt > since
                && p.Destinations.Any(d => d.Network == PostNetworks.Bluesky && d.State == PostDestinationStates.Failed && d.Error != null))
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var post in refused)
        {
            var error = post.Destinations.First(d => d.Network == PostNetworks.Bluesky).Error!;

            var opening = await _db.CalendarOpenings
                .FirstOrDefaultAsync(o => o.EventId == post.EventId && o.OccurrenceStartsAt == post.DateStartsAt, ct)
                .ConfigureAwait(false);

            if (opening is null || opening.FirstJoinBlueskyPostError == error)
                continue;

            opening.FirstJoinBlueskyPostError = error.Length <= 1024 ? error : error[..1024];
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
