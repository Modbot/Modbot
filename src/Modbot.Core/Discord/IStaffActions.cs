using Modbot.Core.Data.Entities;

namespace Modbot.Core.Discord;

/// <summary>A staff account acting from Discord, as the web app would know it from a session.</summary>
/// <param name="UserId">The Modbot account.</param>
/// <param name="Username">Its username now, recorded on anything it writes.</param>
/// <param name="Held">What its roles allow, unioned.</param>
public sealed record StaffMember(Guid UserId, string Username, ModbotPermissions Held);

/// <summary>One reason a moderator may pick for an action.</summary>
/// <param name="NeedsNote">Picking it needs a written note too ("Other").</param>
public sealed record StaffReason(Guid Id, string Label, string Description, bool NeedsNote);

/// <summary>The reasons offered for one action, and whether one must be picked.</summary>
public sealed record StaffReasons(IReadOnlyList<StaffReason> Reasons, bool Required)
{
    public static StaffReasons None { get; } = new([], false);
}

/// <summary>How an action asked for from Discord went.</summary>
/// <param name="Done">True only when VRChat accepted. False means nothing changed in VRChat.</param>
/// <param name="Error">What VRChat or Modbot said, when it was not done.</param>
/// <param name="Refused">Modbot refused before anything was sent (a reason missing, no group set up).</param>
/// <param name="Repeat">This confirmation had already acted; this is its first answer again.</param>
/// <param name="Gone">VRChat said there was nothing to act on: the request was already answered.</param>
/// <param name="CaseId">The case file a ban wrote, or an unban lifted.</param>
/// <param name="DiscordDone">The linked Discord account was banned or unbanned too.</param>
/// <param name="DiscordError">Why the linked Discord account was not, when Discord refused.</param>
/// <param name="CaseFileError">Done, but the case file could not be written or marked.</param>
public sealed record StaffActionAnswer(
    bool Done,
    string? Error,
    bool Refused = false,
    bool Repeat = false,
    bool Gone = false,
    Guid? CaseId = null,
    bool DiscordDone = false,
    string? DiscordError = null,
    string? CaseFileError = null)
{
    public static StaffActionAnswer RefusedWith(string error) => new(false, error, Refused: true);
}

/// <summary>How writing a note from Discord went: the fact's id, or why not.</summary>
public sealed record StaffNoteAnswer(long? NoteId, string? Error)
{
    public bool Written => NoteId is not null;
}

/// <summary>
/// What a staff member may do from Discord, done exactly the way the web app does it (acting from
/// Discord design §5).
/// </summary>
/// <remarks>
/// <para>
/// In Core so the bot can ask without depending on the API, whose moderation and note services do
/// the work. The API registers the implementation; a host without it has nothing registered, and the
/// bot then says it is not set up to act.
/// </para>
/// <para>
/// The caller has already resolved who is acting and checked their permission, the same way the
/// endpoint's own checks would. The implementation checks the permission again: one place that
/// forgot is not allowed to be the one that bans.
/// </para>
/// <para>
/// Actions are the web app's own words: <c>ban</c>, <c>kick</c>, <c>unban</c>, <c>approve</c>,
/// <c>reject</c>.
/// </para>
/// </remarks>
public interface IStaffActions
{
    /// <summary>The reasons offered for an action: switched on and marked for it, in the list's order.</summary>
    Task<StaffReasons> ReasonsAsync(string action, CancellationToken ct = default);

    /// <summary>
    /// Every check the action makes before it sends anything, without sending it. Null means it may
    /// go ahead; otherwise the sentence to show.
    /// </summary>
    Task<string?> CheckAsync(
        string action, string vrchatUserId, IReadOnlyList<Guid> reasonIds, string note, StaffMember by, CancellationToken ct = default);

    /// <summary>
    /// Runs the action. <paramref name="key"/> makes one confirmation act once: the same key again
    /// answers with the first result rather than acting twice.
    /// </summary>
    Task<StaffActionAnswer> RunAsync(
        string action,
        string key,
        string vrchatUserId,
        IReadOnlyList<Guid> reasonIds,
        string note,
        StaffMember by,
        CancellationToken ct = default);

    /// <summary>Writes a note about a person on VRChat or on Discord.</summary>
    Task<StaffNoteAnswer> WriteNoteAsync(
        FactPlatform platform, string userId, string text, StaffMember by, CancellationToken ct = default);
}
