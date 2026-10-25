namespace Modbot.Core.Data.Entities;

/// <summary>Which kind of action a clip is asked for. <strong>Stored. Never renumber one.</strong></summary>
public enum ClipAction
{
    Ban = 0,

    /// <summary>Removed from the group, or kicked from a group instance.</summary>
    Kick = 1,

    Warning = 2,
}

/// <summary>
/// Where a clip request stands. <strong>These values are stored. Never renumber one.</strong>
/// </summary>
public enum ClipStatus
{
    /// <summary>Sent (or about to be sent) to the moderator's companion; no answer yet.</summary>
    Asked = 0,

    /// <summary>"Clip attached".</summary>
    Attached = 1,

    /// <summary>"Clip failed to upload".</summary>
    UploadFailed = 2,

    /// <summary>"Clip couldn't be cut".</summary>
    CouldNotCut = 3,

    /// <summary>"Companion didn't answer": nothing came back inside <see cref="ClipRequest.AnswerWindow"/>.</summary>
    DidNotAnswer = 4,

    /// <summary>"Clips was off": the companion answered that it was not recording.</summary>
    ClipsOff = 5,

    /// <summary>"Not in the instance": nobody's companion was there, so no clip was asked for.</summary>
    NotInInstance = 6,
}

/// <summary>The words each <see cref="ClipStatus"/> shows as, and which of them count as a missed clip.</summary>
public static class ClipStatusLabels
{
    public static string Of(ClipStatus status) => status switch
    {
        ClipStatus.Asked => "Waiting for the clip",
        ClipStatus.Attached => "Clip attached",
        ClipStatus.UploadFailed => "Clip failed to upload",
        ClipStatus.CouldNotCut => "Clip couldn't be cut",
        ClipStatus.DidNotAnswer => "Companion didn't answer",
        ClipStatus.ClipsOff => "Clips was off",
        ClipStatus.NotInInstance => "Not in the instance",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>
    /// Every status except "Clip attached" and "Not in the instance" counts as a missed clip --
    /// once the request is over. A request still waiting is neither.
    /// </summary>
    public static bool IsMissed(ClipStatus status) => status
        is ClipStatus.UploadFailed or ClipStatus.CouldNotCut or ClipStatus.DidNotAnswer or ClipStatus.ClipsOff;

    /// <summary>The statuses <see cref="IsMissed"/> is true for, for a database query.</summary>
    public static readonly ClipStatus[] Missed =
        [ClipStatus.UploadFailed, ClipStatus.CouldNotCut, ClipStatus.DidNotAnswer, ClipStatus.ClipsOff];

    /// <summary>What a companion may answer a request with.</summary>
    public static bool IsAnswer(ClipStatus status) => status
        is ClipStatus.Attached or ClipStatus.UploadFailed or ClipStatus.CouldNotCut or ClipStatus.ClipsOff;
}

/// <summary>
/// One action that a clip was (or would have been) asked for, and how that went.
/// </summary>
/// <remarks>
/// <para>
/// One row per action fact. The row is written when Modbot decides, so an action nobody could
/// clip still shows <see cref="ClipStatus.NotInInstance"/> rather than nothing. A row with
/// <see cref="ClipStatus.Asked"/> is the message for the moderator's companion: the live
/// connection sends it, and the companion's upload answers it, by <see cref="Id"/>.
/// </para>
/// <para>
/// The clip itself is not in this row: <see cref="BlobHash"/> names the stored evidence file, set
/// when the answer is accepted.
/// </para>
/// </remarks>
public class ClipRequest
{
    /// <summary>How long a companion has to answer. After that the request is "Companion didn't answer".</summary>
    public static readonly TimeSpan AnswerWindow = TimeSpan.FromMinutes(15);

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The fact of the action (<c>modbot_event.id</c>). One request per fact.</summary>
    public long FactId { get; set; }

    public ClipAction Action { get; set; }

    /// <summary>The VRChat account the action was taken against. Opaque; never parsed.</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>The Modbot account of the moderator who took the action.</summary>
    public Guid ModeratorUserId { get; set; }

    /// <summary>When the action happened: VRChat's time, or Modbot's own for a press in Modbot.</summary>
    public DateTimeOffset ActionAt { get; set; }

    /// <summary>The instance the person was in, when known. The companion must have been in it too.</summary>
    public string? WorldId { get; set; }

    public string? InstanceId { get; set; }

    public ClipStatus Status { get; set; }

    /// <summary>When Modbot decided. Missed clips are counted from this.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The moderator's companion that was asked. Null when nothing was asked.</summary>
    public Guid? DeviceId { get; set; }

    /// <summary>When the request was made, which the 15 minutes run from. Null when nothing was asked.</summary>
    public DateTimeOffset? RequestedAt { get; set; }

    /// <summary>When the answer was taken, or the wait ran out.</summary>
    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>The evidence file holding the clip, once <see cref="ClipStatus.Attached"/>.</summary>
    public string? BlobHash { get; set; }
}

/// <summary>
/// How far the clip requests have read the fact log. One row, like <see cref="Settings"/>.
/// </summary>
public class ClipRequestCursor
{
    /// <summary>Always 1. Enforced by a database check constraint.</summary>
    public int Id { get; set; } = 1;

    /// <summary>The last fact id read. Facts before it are never looked at again.</summary>
    public long ReadThrough { get; set; }
}
