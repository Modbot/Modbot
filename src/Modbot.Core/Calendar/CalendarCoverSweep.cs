using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Time;

namespace Modbot.Core.Calendar;

/// <summary>
/// Deletes pictures cropped for Discord that no event was saved with (calendar design §15.4).
/// </summary>
/// <remarks>
/// A picture is uploaded when Upload is pressed, before the event is saved, so a form closed without
/// saving leaves one behind. Once it is a day old -- long enough for any form still open -- and no
/// live event points at it, it is deleted. The calendar's schedule loop runs this once an hour
/// (<see cref="Every"/>), so nothing waits on somebody uploading again.
/// </remarks>
public sealed class CalendarCoverSweep(ModbotContext db, IModbotClock clock)
{
    /// <summary>How old a picture no event uses gets before it is deleted.</summary>
    public static readonly TimeSpan KeptUnsaved = TimeSpan.FromDays(1);

    /// <summary>How often the sweep runs.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    /// <summary>Deletes every unsaved picture older than <see cref="KeptUnsaved"/>; answers how many.</summary>
    public Task<int> RunOnceAsync(CancellationToken ct)
    {
        var before = clock.UtcNow - KeptUnsaved;

        return db.CalendarCoverPictures
            .Where(c => c.CreatedAt < before
                && !db.CalendarEvents.Any(e => e.CoverPictureId == c.Id && e.DeletedAt == null))
            .ExecuteDeleteAsync(ct);
    }
}
