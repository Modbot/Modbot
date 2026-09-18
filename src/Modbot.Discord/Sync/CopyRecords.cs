using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Discord.Sync;

/// <summary>
/// Writes down every change Modbot copies from one platform to the other, and recognises the event
/// that comes back from it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole of loop prevention</strong> (M5 §4.2). Modbot bans in Discord; Discord
/// emits a ban event; Modbot records a Discord ban fact; ban sync sees a Discord ban and copies it
/// into VRChat; VRChat's audit log records it; ban sync sees a VRChat ban and copies it into
/// Discord. Left alone that circle never closes and writes duplicate facts forever.
/// </para>
/// <para>
/// <strong>The row is written before the call goes out</strong>, and every incoming ban, unban or
/// role change is checked against the rows first. That ordering is the point: the fact that
/// records the event is written later, by whatever observed it — the Discord audit-log reader, the
/// VRChat ban sweep — and by code that has no idea a sync exists. A row written first is the only
/// record certain to be there when the event arrives. It also means the check sits at the fact
/// layer, where §4.2 asks for it, rather than in whichever code path happened to act.
/// </para>
/// <para>
/// <strong>A row answers for one returning event and then stops.</strong> Once it has excused one,
/// <see cref="CopiedAction.SeenBackAt"/> is set and it excuses nothing further. Otherwise a single
/// copy would swallow every later ban of the same person: somebody unbanned by hand and banned
/// again an hour later would quietly fail to cross over, which is exactly the silent failure a
/// moderation tool must not have.
/// </para>
/// <para>
/// <strong>A copy that failed excuses nothing either.</strong> No call reached the platform, so no
/// event is coming, and a row left waiting would swallow the next real one.
/// </para>
/// </remarks>
public sealed class CopyRecords
{
    /// <summary>
    /// How far back a returning event may find the copy that caused it.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. A Discord ban is seen within seconds, but a VRChat ban is not seen
    /// until the group's audit log is next read, and a deployment that has been rate limited or
    /// offline reads it much later. An hour is longer than any of that and far shorter than the
    /// gap between two separate decisions about the same person.
    /// </remarks>
    public static readonly TimeSpan LooksBack = TimeSpan.FromHours(1);

    /// <summary>
    /// How far after a copy an event may still be its own.
    /// </summary>
    /// <remarks>
    /// The times being compared come from two systems: Modbot stamps the row from
    /// <c>IModbotClock</c>, and the event carries the time the platform put on it. A few minutes of
    /// slack costs nothing and stops a clock a little behind from turning a copy into a loop.
    /// </remarks>
    public static readonly TimeSpan LooksForward = TimeSpan.FromMinutes(5);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public CopyRecords(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Records that a copy is about to be sent, and saves it. Call this before the outbound call,
    /// never after.
    /// </summary>
    public async Task<CopiedAction> StartAsync(
        string direction,
        string kind,
        string subjectId,
        string? otherSideId,
        string? roleId,
        long? causedByFactId,
        CancellationToken ct)
    {
        var row = new CopiedAction
        {
            Direction = direction,
            Kind = kind,
            SubjectId = subjectId,
            OtherSideId = otherSideId,
            RoleId = roleId,
            CausedByFactId = causedByFactId,
            StartedAt = _clock.UtcNow,
        };

        _db.CopiedActions.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Records how the copy went.
    /// </summary>
    /// <param name="nothingHappened">
    /// The platform had nothing to change — already banned, or not banned at all. The copy counts
    /// as done, and the row is closed at once because no event is coming back from it.
    /// </param>
    public async Task FinishAsync(CopiedAction row, bool done, string? error, bool nothingHappened, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);

        var now = _clock.UtcNow;

        row.FinishedAt = now;
        row.Done = done;
        row.Error = error;

        if (!done || nothingHappened)
            row.SeenBackAt = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether an event Modbot has just seen is one of its own copies coming back, and if it is,
    /// closes the row that caused it.
    /// </summary>
    /// <param name="direction">
    /// The direction of the copy that would have caused this event — so a Discord ban event is
    /// checked against copies <em>to</em> Discord.
    /// </param>
    /// <param name="at">When the event happened, as the platform stated it.</param>
    public async Task<bool> WasOursAsync(
        string direction, string kind, string subjectId, string? roleId, DateTimeOffset at, CancellationToken ct)
    {
        var from = at - LooksBack;
        var to = at + LooksForward;

        var row = await _db.CopiedActions
            .Where(c => c.SeenBackAt == null
                        && c.Done == true
                        && c.Direction == direction
                        && c.Kind == kind
                        && c.SubjectId == subjectId
                        && c.RoleId == roleId
                        && c.StartedAt >= from
                        && c.StartedAt <= to)
            .OrderBy(c => c.StartedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
            return false;

        row.SeenBackAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Records a change made and finished in one step, for the sort where nothing comes back to
    /// recognise — a role change, whose sync compares both sides and so cannot loop.
    /// </summary>
    public async Task<CopiedAction> RecordAsync(
        string direction,
        string kind,
        string subjectId,
        string? otherSideId,
        string? roleId,
        bool done,
        string? error,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var row = new CopiedAction
        {
            Direction = direction,
            Kind = kind,
            SubjectId = subjectId,
            OtherSideId = otherSideId,
            RoleId = roleId,
            StartedAt = now,
            FinishedAt = now,
            Done = done,
            Error = error,
            SeenBackAt = now,
        };

        _db.CopiedActions.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }
}
