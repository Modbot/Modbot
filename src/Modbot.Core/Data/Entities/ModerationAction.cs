using System.ComponentModel.DataAnnotations.Schema;

namespace Modbot.Core.Data.Entities;

/// <summary>
/// One kick, ban or unban a moderator asked Modbot to perform, and how it went. The table is
/// <c>moderation_action</c>.
/// </summary>
/// <remarks>
/// <para>
/// M4 §4.1: an action produces a record when it is <em>attempted</em> and a record when it
/// <em>resolves</em>, because the gap between the two is real — the request may be waiting for its
/// turn in the queue, and it may fail. This row is both: it is written before anything is sent to
/// VRChat and finished afterwards. The fact log carries the history; this row carries the outcome
/// of one press of one button.
/// </para>
/// <para>
/// <strong>It is also the guard against acting twice.</strong> M4 §4.3 asks for a
/// client-generated key on every action, and <see cref="Key"/> is it: a unique index, claimed
/// before the outbound call. A double-submitted ban, an impatient second click, or a retry after a
/// timeout all land on the same row and get the first one's answer back — one ban, one fact. The
/// same shape the calendar already uses for opening an instance, where a crash between "send" and
/// "record" must not be able to open two.
/// </para>
/// <para>
/// Every VRChat id is opaque and stored exactly as it arrived (foundation §3.1.1); every time
/// comes from <c>IModbotClock</c>.
/// </para>
/// </remarks>
public class ModerationAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// The key the browser generated for this confirmation, unique across the table.
    /// </summary>
    /// <remarks>
    /// Opaque text, never parsed. It is generated once when the confirmation dialog opens, so every
    /// press of that dialog's button carries the same one and only the first does anything.
    /// </remarks>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// <c>kick</c>, <c>ban</c> or <c>unban</c>. Text rather than an enum, for the same reason
    /// <see cref="FactType"/> is: a value stored for years should not depend on a number nobody can
    /// read.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>VRChat's id for the person acted on. Opaque.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The managed group at the time.</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>The Modbot account that pressed the button.</summary>
    public Guid ModeratorUserId { get; set; }

    /// <summary>Their username at the time, kept because accounts get renamed and this should not.</summary>
    public string ModeratorUsername { get; set; } = string.Empty;

    /// <summary>The ban reasons picked, as a JSON array of ids.</summary>
    [Column(TypeName = "jsonb")]
    public string ReasonIds { get; set; } = "[]";

    /// <summary>The moderator's optional note. Never the primary input (foundation §5.8.2).</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>When the row was claimed, before anything was sent.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When VRChat answered. Null while the action is still in flight.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Whether VRChat accepted. Null while in flight — never confused with false, because
    /// "still going" and "refused" are different things to tell a moderator.
    /// </summary>
    public bool? Succeeded { get; set; }

    /// <summary>What VRChat answered with, or 0 when nothing was sent at all.</summary>
    public int StatusCode { get; set; }

    /// <summary>VRChat's own words when it refused, for the moderator to read.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// Whether it failed because of a rate limit — VRChat's, or a bucket Modbot had already cold
    /// stopped.
    /// </summary>
    /// <remarks>
    /// Its own column rather than a reading of <see cref="StatusCode"/>, because a cold stop sends
    /// nothing at all and so has no status: the gate reports <c>0</c> for it deliberately, and
    /// writing <c>429</c> there would be inventing a response VRChat never gave.
    /// </remarks>
    public bool RateLimited { get; set; }

    /// <summary>The case file a successful ban wrote or updated, when one was written.</summary>
    public Guid? CaseFileId { get; set; }
}
