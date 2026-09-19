using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using VRChat.API.Model;

namespace Modbot.VRChat.Invites;

/// <summary>Why an invite did not go out, when it did not.</summary>
public enum InviteOutcome
{
    /// <summary>VRChat accepted it.</summary>
    Sent,

    /// <summary>Another invite went out less than <see cref="GroupInvites.NoFasterThan"/> ago.</summary>
    TooSoon,

    /// <summary>VRChat refused it, or the gate declined to send it.</summary>
    Refused,
}

/// <summary>What happened to one invite.</summary>
/// <param name="Outcome">Whether it went out, and why not when it did not.</param>
/// <param name="Problem">A sentence for a person, when something went wrong.</param>
public readonly record struct InviteResult(InviteOutcome Outcome, string? Problem = null)
{
    public bool Worked => Outcome == InviteOutcome.Sent;
}

/// <summary>
/// Sends one group invite, no faster than one every thirty seconds (auto-invites design §5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The pacing belongs here, not to whoever calls.</strong> "At most one invite every
/// thirty seconds across the whole deployment" is a property of what it means to send a group
/// invite, so it is enforced where invites are sent: a second caller added later gets it without
/// knowing about it.
/// </para>
/// <para>
/// It is enforced twice on purpose. The limiter's <c>groups.invites</c> bucket is capped at the
/// same rate, but the token count is flushed to the database every thirty seconds at most
/// (<c>RateLimitOptions.StateFlushInterval</c>), so a crash can hand back up to a bucket's worth
/// of allowance. The stored timestamp cannot: it is written before the request goes out, and a
/// restarted process reads the same row.
/// </para>
/// <para>
/// The row in <c>group_auto_invite</c> is written <em>before</em> the call and updated with the
/// answer afterwards, so a crash between the two remembers an invite that may not have happened
/// rather than forgetting one that did (auto-invites design §6). A refusal still counts as an
/// attempt, or a person VRChat keeps saying no about would be retried forever.
/// </para>
/// <para>
/// Through the gate like everything else (foundation §4.1), on
/// <c>...WithHttpInfoAsync</c> (§4.1.1), and never retried on a 429 (§4.3.1) — the invite simply
/// did not go out, and the next pass will find the bucket cold-stopped.
/// </para>
/// </remarks>
public sealed class GroupInvites
{
    /// <summary>
    /// The shortest gap between two invites, anywhere in the deployment. Not per person, not per
    /// instance.
    /// </summary>
    public static readonly TimeSpan NoFasterThan = TimeSpan.FromSeconds(30);

    /// <summary>How much of VRChat's answer is kept on the row and in the fact.</summary>
    private const int MaxProblemLength = 500;

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public GroupInvites(IVRChatGate gate, ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _db = db;
        _clock = clock;
    }

    /// <summary>When the last invite went out, or null when none ever has.</summary>
    public Task<DateTimeOffset?> LastSentAtAsync(CancellationToken ct = default)
        => _db.GroupAutoInvites.AsNoTracking()
            .OrderByDescending(i => i.InvitedAt)
            .Select(i => (DateTimeOffset?)i.InvitedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>Whether another invite may go out right now.</summary>
    public async Task<bool> MaySendAsync(CancellationToken ct = default)
    {
        var last = await LastSentAtAsync(ct).ConfigureAwait(false);
        return last is not { } when || _clock.UtcNow - when >= NoFasterThan;
    }

    /// <summary>
    /// Invites one person to the group, and records that it did.
    /// </summary>
    /// <param name="groupId">The managed group. Opaque text, never validated (foundation §3.1.1).</param>
    /// <param name="userId">Who to invite. Opaque text too.</param>
    /// <param name="instanceId">VRChat's number for the instance they were in, for the record.</param>
    public async Task<InviteResult> SendAsync(
        string groupId, string userId, string? instanceId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var now = _clock.UtcNow;

        if (!await MaySendAsync(ct).ConfigureAwait(false))
            return new InviteResult(InviteOutcome.TooSoon);

        var row = await _db.GroupAutoInvites.FirstOrDefaultAsync(i => i.UserId == userId, ct).ConfigureAwait(false);

        if (row is null)
        {
            row = new GroupAutoInvite { UserId = userId, FirstInvitedAt = now };
            _db.GroupAutoInvites.Add(row);
        }

        row.InvitedAt = now;
        row.Attempts++;
        row.InstanceId = instanceId;
        row.Worked = null;
        row.Problem = null;

        // Written first, on purpose: see the remarks above.
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // `confirmOverrideBlock` is left at VRChat's own default. Modbot does not decide to talk
        // past somebody's block; if VRChat refuses for that reason, the refusal is the answer.
        var request = new CreateGroupInviteRequest(userId: userId);

        var result = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsInvites, groupId, "CreateGroupInvite"),
            (client, token) => client.Groups.CreateGroupInviteWithHttpInfoAsync(groupId, request, cancellationToken: token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        row.Worked = result.Success;
        row.Problem = result.Success ? null : Short(result.ErrorMessage ?? $"VRChat answered {result.StatusCode}.");
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return result.Success
            ? new InviteResult(InviteOutcome.Sent)
            : new InviteResult(InviteOutcome.Refused, row.Problem);
    }

    private static string Short(string text)
        => text.Length <= MaxProblemLength ? text : text[..MaxProblemLength];
}
