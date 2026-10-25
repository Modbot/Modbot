using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.Api.Features.Companion.ClipRequests;

/// <summary>
/// What a companion is asked, how it answers, and what is counted as missed.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam for the steps that follow: the delivery step reads
/// <see cref="PendingForAsync"/> to send the request and calls <see cref="AnswerAsync"/> when the
/// companion's upload or refusal comes back; the automatic roles read
/// <see cref="MissedCountAsync"/>. The checks that make an answer safe -- the right device, the
/// right request, once, inside 15 minutes -- are one conditional update here, so no caller can
/// forget one of them.
/// </para>
/// </remarks>
public sealed class ClipRequestStore
{
    private readonly ModbotContext _db;

    public ClipRequestStore(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    /// <summary>
    /// The requests this device may still answer, oldest first. Sent on connect and on every poll;
    /// the companion treats a repeated request id as the same request.
    /// </summary>
    public async Task<IReadOnlyList<ClipRequest>> PendingForAsync(Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var earliest = now - ClipRequest.AnswerWindow;

        return await _db.ClipRequests.AsNoTracking()
            .Where(r => r.DeviceId == deviceId && r.Status == ClipStatus.Asked && r.RequestedAt >= earliest)
            .OrderBy(r => r.RequestedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The request this device may answer right now, or null. A wrong device, a wrong id, an answer
    /// already taken and a request past its 15 minutes are all the same null, so a caller cannot
    /// tell them apart or probe for ids.
    /// </summary>
    public Task<ClipRequest?> OpenAsync(Guid requestId, Guid deviceId, DateTimeOffset now, CancellationToken ct)
    {
        var earliest = now - ClipRequest.AnswerWindow;

        return _db.ClipRequests.AsNoTracking()
            .Where(r => r.Id == requestId
                && r.DeviceId == deviceId
                && r.Status == ClipStatus.Asked
                && r.RequestedAt >= earliest)
            .FirstOrDefaultAsync(ct)!;
    }

    /// <summary>
    /// Takes the answer, once. True when this call is the one that closed the request.
    /// </summary>
    /// <param name="outcome">One of <see cref="ClipStatusLabels.IsAnswer"/>.</param>
    /// <param name="blobHash">The stored clip's hash; required for "Clip attached", dropped otherwise.</param>
    public async Task<bool> AnswerAsync(
        Guid requestId,
        Guid deviceId,
        ClipStatus outcome,
        string? blobHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (!ClipStatusLabels.IsAnswer(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "That is not an answer a companion can give.");

        if (outcome == ClipStatus.Attached && string.IsNullOrEmpty(blobHash))
            throw new ArgumentException("A clip that is attached has a stored file.", nameof(blobHash));

        var earliest = now - ClipRequest.AnswerWindow;
        var hash = outcome == ClipStatus.Attached ? blobHash : null;

        // One conditional update, not a read and a write: two uploads racing for one request must
        // not both win.
        var changed = await _db.ClipRequests
            .Where(r => r.Id == requestId
                && r.DeviceId == deviceId
                && r.Status == ClipStatus.Asked
                && r.RequestedAt >= earliest)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, outcome)
                    .SetProperty(r => r.AnsweredAt, now)
                    .SetProperty(r => r.BlobHash, hash),
                ct);

        return changed == 1;
    }

    /// <summary>
    /// Closes every request whose 15 minutes ran out as "Companion didn't answer". Returns how many.
    /// </summary>
    public Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct)
    {
        var earliest = now - ClipRequest.AnswerWindow;

        return _db.ClipRequests
            .Where(r => r.Status == ClipStatus.Asked && r.RequestedAt < earliest)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, ClipStatus.DidNotAnswer)
                    .SetProperty(r => r.AnsweredAt, now),
                ct);
    }

    /// <summary>
    /// How many clips this moderator missed since the given moment, counted from when each action
    /// was decided. A request still waiting is not a miss yet.
    /// </summary>
    public Task<int> MissedCountAsync(Guid moderatorUserId, DateTimeOffset since, CancellationToken ct)
        => _db.ClipRequests.AsNoTracking()
            .CountAsync(r => r.ModeratorUserId == moderatorUserId
                && r.CreatedAt >= since
                && ClipStatusLabels.Missed.Contains(r.Status), ct);

    /// <summary>The clip request for an action, for showing its status. Null when none was made.</summary>
    public Task<ClipRequest?> ForFactAsync(long factId, CancellationToken ct)
        => _db.ClipRequests.AsNoTracking().FirstOrDefaultAsync(r => r.FactId == factId, ct)!;
}
