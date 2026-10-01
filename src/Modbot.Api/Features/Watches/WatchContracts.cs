namespace Modbot.Api.Features.Watches;

/// <summary>What a moderator asks for when they start watching somebody.</summary>
/// <param name="UserId">
/// The account to watch. Opaque: taken as sent, never parsed or checked for shape (foundation
/// §3.1.1).
/// </param>
/// <param name="Platform"><c>VRChat</c> or <c>Discord</c>. Defaults to VRChat.</param>
/// <param name="Reason">Why, in the moderator's words. Required, at most 200 characters.</param>
/// <param name="EndsAt">When the watch stops on its own. Null keeps it until somebody stops it.</param>
/// <param name="FollowUpAt">When somebody should check on the person again. Null for no follow-up.</param>
public sealed record StartWatchRequest(
    string UserId,
    string? Platform = null,
    string? Reason = null,
    DateTimeOffset? EndsAt = null,
    DateTimeOffset? FollowUpAt = null);

/// <summary>One watch, as it is read back.</summary>
/// <param name="SubjectPlatform"><c>VRChat</c> or <c>Discord</c>.</param>
/// <param name="SubjectName">The person's name as Modbot last stored it, when it has one.</param>
/// <param name="Reason">Why. Null when the reader may not read the audit log.</param>
/// <param name="SetByName">The username of whoever started it, as it was then.</param>
/// <param name="FollowUpDue">The follow-up day has come and nobody has followed up yet.</param>
/// <param name="Standing">It still stands: not stopped, and its end day has not passed.</param>
/// <param name="EndedAt">When it stopped. For one that ran out on its own, the day it was set to end.</param>
/// <param name="EndedByName">Who stopped it. Null when it ran out on its own.</param>
/// <param name="CanChange">
/// Whether the person reading may stop it or follow up on it: they started it, or they may write
/// notes. Always false for one that no longer stands.
/// </param>
public sealed record WatchView(
    Guid Id,
    string SubjectPlatform,
    string SubjectId,
    string? SubjectName,
    string? Reason,
    string SetByName,
    DateTimeOffset SetAt,
    DateTimeOffset? EndsAt,
    DateTimeOffset? FollowUpAt,
    bool FollowUpDue,
    bool Standing,
    DateTimeOffset? EndedAt,
    string? EndedByName,
    bool CanChange);

/// <summary>One person's watches, the standing one first, then the rest newest first.</summary>
/// <param name="CanWrite">Whether the person reading may start a watch.</param>
/// <param name="Now">Modbot's clock, for the ages.</param>
public sealed record PersonWatchList(IReadOnlyList<WatchView> Watches, bool CanWrite, DateTimeOffset Now);

/// <summary>Every standing watch, or only those with a follow-up due, soonest due first.</summary>
public sealed record WatchList(IReadOnlyList<WatchView> Watches, DateTimeOffset Now);
