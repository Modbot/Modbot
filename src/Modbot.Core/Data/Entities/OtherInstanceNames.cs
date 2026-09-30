namespace Modbot.Core.Data.Entities;

/// <summary>
/// The name another group's instance was opened with, asked of VRChat once, the first time an
/// instance popup's World tab listed it. The table is <c>other_instance_name</c>.
/// </summary>
/// <remarks>
/// <para>
/// The world's page (<see cref="WorldHeadCount"/>) lists every instance in a world by its id and a
/// head count, and nothing else. The group's own instances have a name in <see cref="VRChatInstance"/>;
/// everybody else's are only a number until their own page is read, and this keeps what that one
/// read said so no popup and no moderator ever has it read again.
/// </para>
/// <para>
/// <strong>One row per asking, never updated.</strong> A row with no name is an answer too: the
/// instance had none, or VRChat would not say (<see cref="Refused"/>). Either way the instance is
/// never asked about again, and it keeps its number on screen.
/// </para>
/// </remarks>
public class OtherInstanceName
{
    /// <summary>
    /// The world's id, a <c>:</c>, and the instance's id exactly as the world's list carried it,
    /// qualifiers and all. Opaque text, as every VRChat id Modbot keeps.
    /// </summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>The name it was opened with. Null when it had none, or when VRChat refused.</summary>
    public string? Name { get; set; }

    /// <summary>When VRChat was asked.</summary>
    public DateTimeOffset AskedAt { get; set; }

    /// <summary>True when VRChat answered with a refusal rather than the instance's page.</summary>
    public bool Refused { get; set; }
}

/// <summary>
/// Another group's name, asked of VRChat once, the first time an instance popup's World tab listed
/// one of its instances. The table is <c>other_group_name</c>.
/// </summary>
/// <remarks>
/// Kept once, as agreed with the maintainer on 2026-09-30: a group can rename itself, and this
/// keeps the name it had when it was first asked about. The managed group's own name comes from
/// its own poll and is never read into here.
/// </remarks>
public class OtherGroupName
{
    /// <summary>VRChat's group id, opaque text.</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>The group's name. Null when VRChat refused or sent none.</summary>
    public string? Name { get; set; }

    /// <summary>When VRChat was asked.</summary>
    public DateTimeOffset AskedAt { get; set; }

    /// <summary>True when VRChat answered with a refusal rather than the group's page.</summary>
    public bool Refused { get; set; }
}
