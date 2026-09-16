namespace Modbot.Api.Features.Moderation;

/// <summary>What a moderator asked Modbot to do to somebody.</summary>
/// <param name="UserId">
/// VRChat's id for the person. Opaque: taken as sent, never parsed and never checked for shape
/// (foundation §3.1.1). Required — there is nobody to act on without it.
/// </param>
/// <param name="Key">
/// The key the browser made for this confirmation. Every press of that dialog's button carries the
/// same one, and only the first does anything (M4 §4.3).
/// </param>
/// <param name="ReasonIds">
/// Reasons from the group's list. Required on a ban; on a kick or an unban, required only when the
/// group has switched that on in Settings.
/// </param>
/// <param name="Note">The moderator's own words. Always optional, never the primary input.</param>
public sealed record ModerationActionRequest(
    string UserId,
    string Key,
    IReadOnlyList<Guid>? ReasonIds = null,
    string? Note = null);

/// <summary>How it went.</summary>
/// <param name="Action">
/// <c>kick</c>, <c>ban</c> or <c>unban</c> — what was asked for, whether or not it happened.
/// </param>
/// <param name="UserId">The person it was about.</param>
/// <param name="Done">
/// True only when VRChat accepted. False means nothing changed in VRChat, whatever else is in
/// here — a moderator must never read a success that has not happened (M4 §4.1).
/// </param>
/// <param name="At">When VRChat answered, on Modbot's clock.</param>
/// <param name="CaseId">The case file a ban wrote or updated, when one was written.</param>
/// <param name="Error">What VRChat said when it refused. Null when it worked.</param>
/// <param name="RateLimited">
/// True when VRChat rate limited this, or Modbot was already waiting one out. Nothing was retried
/// and nothing will be: the action simply did not happen, and pressing again sooner makes the wait
/// longer (spec 4.3.1).
/// </param>
/// <param name="Repeat">
/// True when this key had already been used: the answer is the first press's, and nothing was sent
/// a second time.
/// </param>
public sealed record ModerationActionResult(
    string Action,
    string UserId,
    bool Done,
    DateTimeOffset At,
    Guid? CaseId,
    string? Error,
    bool RateLimited,
    bool Repeat);
