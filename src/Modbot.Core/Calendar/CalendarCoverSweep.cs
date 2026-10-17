using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Time;

namespace Modbot.Core.Calendar;

/// <summary>
/// Deletes pictures cropped for Discord that no event or post was saved with (calendar design §15.4,
/// posts design §2.2), and the same for the videos kept for Bluesky's posts
/// (<see cref="CalendarBlueskyVideosInUse"/> says which are still needed).
/// </summary>
/// <remarks>
/// A picture is uploaded when Upload is pressed, before the event or post is saved, so a form closed
/// without saving leaves one behind. Once it is a day old -- long enough for any form still open --
/// and no live event (as its cover or in its Bluesky pictures), no template (as its cover or in its Bluesky
/// pictures), no post, no post's site copy (Bluesky's copy of the picture) and no sent Bluesky post's
/// pictures point at it, it is deleted. A post keeps its picture whatever its
/// state: a cancelled one can be duplicated, and a sent one is shown with it. The calendar's schedule loop runs this once an hour
/// (<see cref="Every"/>), so nothing waits on somebody uploading again.
/// </remarks>
public sealed class CalendarCoverSweep(ModbotContext db, IModbotClock clock)
{
    /// <summary>How old a picture no event uses gets before it is deleted.</summary>
    public static readonly TimeSpan KeptUnsaved = TimeSpan.FromDays(1);

    /// <summary>How often the sweep runs.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    /// <summary>Deletes every picture nothing uses that is older than <see cref="KeptUnsaved"/>; answers how many.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var before = clock.UtcNow - KeptUnsaved;

        // The pictures of Bluesky's posts: an event's, a template's, and those a post was sent with.
        var inBlueskyPosts = (await CalendarBlueskyPicturesInUse.ReadAsync(db, ct).ConfigureAwait(false)).ToList();

        var pictures = await db.CalendarCoverPictures
            .Where(c => c.CreatedAt < before
                && !inBlueskyPosts.Contains(c.Id)
                && !db.CalendarEvents.Any(e => e.CoverPictureId == c.Id && e.DeletedAt == null)
                && !db.CalendarTemplates.Any(t => t.CoverPictureId == c.Id)
                && !db.Posts.Any(p => p.PictureId == c.Id)
                && !db.PostDestinations.Any(d => d.SitePictureId == c.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        // The videos kept for Bluesky's posts, on the same day's wait.
        var videosInUse = (await CalendarBlueskyVideosInUse.ReadAsync(db, ct).ConfigureAwait(false)).ToList();

        var videos = await db.CalendarBlueskyVideos
            .Where(v => v.CreatedAt < before && !videosInUse.Contains(v.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return pictures + videos;
    }
}
