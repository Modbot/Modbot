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
/// <param name="Done">
/// True only when VRChat accepted (or, for an action on the Discord server alone, when Discord did).
/// False means nothing changed.
/// </param>
/// <param name="Error">What VRChat, Discord or Modbot said, when it was not done.</param>
/// <param name="Refused">Modbot refused before anything was sent (a reason missing, no group set up).</param>
/// <param name="Repeat">This confirmation had already acted; this is its first answer again.</param>
/// <param name="Gone">VRChat said there was nothing to act on: the request was already answered.</param>
/// <param name="CaseId">The case file a ban wrote, or an unban lifted.</param>
/// <param name="DiscordDone">The linked Discord account was banned or unbanned too.</param>
/// <param name="DiscordError">Why the linked Discord account was not, when Discord refused.</param>
/// <param name="CaseFileError">Done, but the case file could not be written or marked.</param>
/// <param name="Unchanged">
/// An action on the Discord server alone: Discord was already so (already banned, not in the server),
/// so it was accepted and nothing was written.
/// </param>
public sealed record StaffActionAnswer(
    bool Done,
    string? Error,
    bool Refused = false,
    bool Repeat = false,
    bool Gone = false,
    Guid? CaseId = null,
    bool DiscordDone = false,
    string? DiscordError = null,
    string? CaseFileError = null,
    bool Unchanged = false)
{
    public static StaffActionAnswer RefusedWith(string error) => new(false, error, Refused: true);
}

/// <summary>How writing a note from Discord went: the fact's id, or why not.</summary>
public sealed record StaffNoteAnswer(long? NoteId, string? Error)
{
    public bool Written => NoteId is not null;
}

/// <summary>How starting a watch from Discord went: the watch's id, or why not.</summary>
public sealed record StaffWatchAnswer(Guid? WatchId, string? Error)
{
    public bool Started => WatchId is not null;
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
/// <para>
/// <strong>On the Discord server alone</strong> (<see cref="DiscordCheckAsync"/>,
/// <see cref="DiscordBanAsync"/>, <see cref="DiscordKickAsync"/>) is what the API's
/// <c>/api/discord</c> endpoints do, through the same service: a different permission ("Ban on
/// Discord", "Remove from Discord"), and never the bot, the server's owner or a staff account.
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

    /// <summary>
    /// Starts watching a person on VRChat or on Discord, as the web app's Watch button does: the
    /// reason is required, and the end day and the follow-up day are optional. Nothing is changed
    /// on VRChat or Discord.
    /// </summary>
    /// <param name="endsAt">When the watch stops on its own, or null to keep it until somebody stops it.</param>
    /// <param name="followUpAt">When somebody should check on the person again, or null for no follow-up.</param>
    Task<StaffWatchAnswer> StartWatchAsync(
        FactPlatform platform,
        string userId,
        string reason,
        DateTimeOffset? endsAt,
        DateTimeOffset? followUpAt,
        StaffMember by,
        CancellationToken ct = default);

    /// <summary>
    /// Every check an action on the Discord server alone makes before it asks Discord anything:
    /// the permission, the server being set up, and the three accounts never acted on (the bot, the
    /// server's owner, a staff account). Nothing is sent. Null means it may go ahead; otherwise the
    /// sentence to show.
    /// </summary>
    /// <param name="action"><c>ban</c> or <c>kick</c>.</param>
    Task<string?> DiscordCheckAsync(string action, string discordUserId, StaffMember by, CancellationToken ct = default);

    /// <summary>Bans one person from the Discord server, as <c>POST /api/discord/bans</c> does.</summary>
    /// <param name="reason">Why, for Discord's audit log and Modbot's. May be empty.</param>
    /// <param name="deleteMessageDays">How many days of their messages Discord deletes too, 0 to 7.</param>
    Task<StaffActionAnswer> DiscordBanAsync(
        string discordUserId, string reason, int deleteMessageDays, StaffMember by, CancellationToken ct = default);

    /// <summary>Removes one person from the Discord server without banning them, as <c>POST /api/discord/members/{id}/kick</c> does.</summary>
    Task<StaffActionAnswer> DiscordKickAsync(
        string discordUserId, string reason, StaffMember by, CancellationToken ct = default);
}
