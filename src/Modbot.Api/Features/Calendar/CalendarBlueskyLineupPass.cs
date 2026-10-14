using Microsoft.EntityFrameworkCore;
using Modbot.AI.Insights;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Posts;
using Modbot.Core.Profiles;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// Makes the week's line-up post when its time comes (Settings → Bluesky, Weekly line-up): one
/// ordinary Bluesky post listing the next seven days' public dates.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An ordinary post.</strong> The post is a <see cref="Post"/> of kind
/// <see cref="PostKinds.Lineup"/> with a Bluesky destination. <c>PostBlueskySender</c> sends it, with the one record key picked before the first try, the
/// read-back, the hourly cap, the late rule, the Posting switch, Pause all posting and the rate limit
/// that stops the lane (a 429 is never tried again). A post that fails shows on Health like any other.
/// What the post says is <see cref="CalendarBlueskyLineup"/>'s.
/// </para>
/// <para>
/// <strong>Once a week.</strong> The time is the latest one at or before now that the settings name
/// (day and hour in their zone). The pass claims it on the settings row, and the Monday of its week is
/// the post's <see cref="Post.ExternalKey"/> under a unique index, so a restart, a second pass or a
/// day changed within the week cannot make two. Saving the settings marks the times already past as
/// dealt with, so turning the line-up on never posts for a time that went by before. A time more than
/// <see cref="PostRules.LateLimit"/> past, for instance because Posting was off, is not posted late.
/// </para>
/// <para>
/// A date changed or cancelled after the post is made is left alone: the post is that week as it was.
/// A week with no dates gets no post. Nothing is made while the Bluesky Posting switch is off, Pause
/// all posting is on or the account has not passed Check.
/// </para>
/// </remarks>
public sealed class CalendarBlueskyLineupPass
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public CalendarBlueskyLineupPass(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The latest time the settings name, at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset LatestMoment(Core.Data.Entities.Settings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return InsightTimes.LatestMoment(
            now,
            InsightKinds.EveryWeek,
            settings.BlueskyLineupHour,
            settings.BlueskyLineupDay,
            InsightTimes.ZoneOrUtc(settings.BlueskyLineupTimeZone));
    }

    /// <summary>Makes the post if one is due. Returns whether it made one.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        if (settings is null || !settings.BlueskyLineupOn || string.IsNullOrWhiteSpace(settings.BlueskyDid))
            return false;

        var sites = new PostSites(
            settings.PostsPaused,
            DiscordOn: false,
            DiscordSetUp: false,
            VRChatOn: false,
            VRChatSetUp: false,
            settings.BlueskyPostingOn,
            PostSites.BlueskyReady(settings));

        if (sites.HoldFor(PostNetworks.Bluesky) is not null)
            return false;

        var now = _clock.UtcNow;
        var moment = LatestMoment(settings, now);

        if (settings.BlueskyLineupHandledThrough is { } handled && handled >= moment)
            return false;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Claimed first, in the same transaction as the post: a second pass finds the time taken, and
        // a pass that fails gives the claim back with the rest.
        var claimed = await _db.Settings
            .Where(s => s.Id == settings.Id && (s.BlueskyLineupHandledThrough == null || s.BlueskyLineupHandledThrough < moment))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyLineupHandledThrough, moment), ct)
            .ConfigureAwait(false);

        if (claimed == 0)
            return false;

        var zone = InsightTimes.ZoneOrUtc(settings.BlueskyLineupTimeZone);
        var week = MemberSpotlight.WeekOf(moment, zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // Too late to post as this week's: the sender would refuse it. A week with a post already
        // (the day was changed within the week) gets no second one.
        if (now - moment > PostRules.LateLimit
            || await _db.Posts.AnyAsync(p => p.Kind == PostKinds.Lineup && p.ExternalKey == week, ct).ConfigureAwait(false))
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return false;
        }

        var events = await _db.CalendarEvents.AsNoTracking()
            .Where(e => e.DeletedAt == null
                && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open)
                && e.Visibility == "public")
            .ToListAsync(ct).ConfigureAwait(false);

        // From the planned time, not from now, so a post made a little late still covers the planned week.
        var dates = CalendarBlueskyLineup.DatesOf(events, moment);

        if (dates.Count == 0)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _log.Information("No Bluesky line-up this week: no public dates in the next seven days");
            return false;
        }

        var text = CalendarBlueskyLineup.TextOf(settings.ManagedGroupName, dates, zone, CalendarBlueskyLineup.PageLinkOf(settings));

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = CalendarBlueskyLineup.Heading(settings.ManagedGroupName),
            Text = text,
            Status = PostStatuses.Scheduled,
            SendAt = moment,
            TimeZone = zone.Id,
            Kind = PostKinds.Lineup,
            ExternalKey = week,
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
            UpdatedAt = now,
        });

        _db.Posts.Add(post);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        _log.Information("Made the Bluesky line-up post for the week of {Week}", week);
        return true;
    }
}
