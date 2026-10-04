using Modbot.Shared.HeadsUps;

namespace Modbot.Core.Data.Entities;

/// <summary>Why a heads-up stopped standing.</summary>
public enum HeadsUpEnd
{
    /// <summary>A moderator cleared it.</summary>
    Cleared = 0,

    /// <summary>The person a message was about left the instance.</summary>
    PersonLeft = 1,

    /// <summary>The instance closed, or nobody was left in it.</summary>
    InstanceEnded = 2,
}

/// <summary>
/// A short note one moderator leaves, from the companion, for the other staff in the same VRChat
/// instance (heads-ups, 2026-10-03). The table is <c>heads_up</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A row, and not a fact.</strong> A heads-up is seen by the staff standing in that
/// instance and by nobody else: not the audit log, not the website, not Discord. Writing it to the
/// fact log would put it in front of everyone who reads that log, which is a different promise from
/// the one the moderator made when they pressed the button.
/// </para>
/// <para>
/// <strong>Short-lived.</strong> It stands until somebody clears it, until the person a message is
/// about leaves, or until the instance closes or empties. A Keep an eye outlasts its person leaving,
/// so their coming back can be told. The row stays once ended, with when and why, so who placed
/// what can still be answered.
/// </para>
/// </remarks>
public class HeadsUp
{
    public Guid Id { get; set; }

    /// <summary>The VRChat group this deployment manages, as it was when the heads-up was placed.</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>VRChat's number for the instance. Opaque, never checked for shape.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The world the instance is in, when the companion said. A number is only unique inside one world.</summary>
    public string? WorldId { get; set; }

    public HeadsUpKind Kind { get; set; }

    /// <summary>The person it is about, for a Keep an eye or a Message on a person. Opaque.</summary>
    public string? SubjectId { get; set; }

    /// <summary>Their name as the roster showed it then, so the panel can say who without a lookup.</summary>
    public string? SubjectName { get; set; }

    /// <summary>The moderator's words, cleaned. See <see cref="HeadsUpRules"/>.</summary>
    public string? Text { get; set; }

    /// <summary>For Ask for help: one of <see cref="HeadsUpRules.Places"/>.</summary>
    public string? Place { get; set; }

    /// <summary>The Modbot account whose companion placed it.</summary>
    public Guid PlacedByUserId { get; set; }

    /// <summary>Their VRChat name, or their username when it is not known, as it was then.</summary>
    public string PlacedByName { get; set; } = string.Empty;

    /// <summary>The companion it came from.</summary>
    public Guid PlacedByDeviceId { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    /// <summary>When it stopped standing. Null while it stands.</summary>
    public DateTimeOffset? ClearedAt { get; set; }

    /// <summary>Who cleared it, or null when it ended on its own.</summary>
    public Guid? ClearedByUserId { get; set; }

    public string? ClearedByName { get; set; }

    public HeadsUpEnd? ClearedBecause { get; set; }
}
