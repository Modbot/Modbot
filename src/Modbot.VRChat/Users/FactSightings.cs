using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Users;

/// <summary>
/// Marks the people a fact names as seen the moment the fact writer records it.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-09-29 <c>last_seen_at</c> moved only when the profile sync's next pass read the
/// fact back out of the log (<c>UserProfileSync.DiscoverAsync</c>). Between the write and that
/// pass the People page said one thing and the audit log beside it another: a moderator whose own
/// client had reported them an hour earlier still read "4h ago". Now the writer records the
/// sighting as the fact lands, and the pass stays as the catch-up: the upsert is idempotent
/// (<c>LEAST</c> / <c>GREATEST</c>), so recording the same sighting twice costs a statement and
/// changes nothing.
/// </para>
/// <para>
/// One rule, one statement. Which facts name a person is <see cref="UserSightings"/>'s list, the
/// same one the pass uses, and the row is written by <see cref="VRChatUserProfiles.RecordSeenAsync(ModbotContext, IReadOnlyCollection{UserSighting}, CancellationToken)"/>,
/// the same statement the pass runs. Both pages are fed by one answer, which is what makes them
/// agree.
/// </para>
/// <para>
/// This holds only a context, not <see cref="VRChatUserProfiles"/>: that class takes the fact
/// writer, and the fact writer takes this, so going through it would be a circle.
/// </para>
/// <para>
/// Only the row moves here; nothing is offered to the refresh queue. The pass's discovery already
/// has the rules for what is recent enough to queue and what a refresh since the sighting has
/// answered, and a second offer from here would be a second chance to get those wrong. The top-up
/// reads <c>last_seen_at</c> like any other row's, so a person bumped here may be found a pass
/// sooner; that is the row doing its job, not a second queueing path.
/// </para>
/// </remarks>
public sealed class FactSightings : ISightingRecorder
{
    private readonly ModbotContext _db;

    public FactSightings(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task RecordAsync(FactRecord fact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var sightings = UserSightings
            .From(fact.Type, fact.SubjectPlatform, fact.SubjectId, fact.ActorPlatform, fact.ActorId, fact.OccurredAt)
            .ToList();

        return sightings.Count == 0
            ? Task.CompletedTask
            : VRChatUserProfiles.RecordSeenAsync(_db, sightings, ct);
    }
}
