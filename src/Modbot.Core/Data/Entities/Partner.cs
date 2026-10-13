namespace Modbot.Core.Data.Entities;

/// <summary>
/// Another VRChat group the group works with: who to talk to there, who here looks after the
/// friendship, and when they last did something together. The table is <c>partner</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything here is typed by staff.</strong> Modbot reads nothing about the other group
/// from VRChat for this: no new call, and no refresh. <see cref="GroupId"/> is kept as the text it
/// was typed as and never checked against any id format; when the name is left empty it is filled
/// from <see cref="OtherGroupName"/> if an instance popup has already asked VRChat for that group.
/// </para>
/// <para>
/// The envoy and the contacts are free text, not links to a person, so a purge of a VRChat or
/// Discord account has nothing to find in this table.
/// </para>
/// </remarks>
public class Partner
{
    public const int MaxNameLength = 100;

    public const int MaxGroupIdLength = 64;

    public const int MaxEnvoyLength = 100;

    public const int MaxContacts = 20;

    public const int MaxContactLength = 200;

    public const int MaxCollabNoteLength = 300;

    public const int MaxNotesLength = 2000;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>VRChat's id for the group, as typed. Opaque text; null when staff did not give one.</summary>
    public string? GroupId { get; set; }

    /// <summary>One of <see cref="PartnerStatuses"/>.</summary>
    public string Status { get; set; } = PartnerStatuses.Active;

    /// <summary>Who represents us to them: a name as typed. Null when nobody is assigned.</summary>
    public string? Envoy { get; set; }

    /// <summary>Who to talk to in their group, one entry for each person, as typed.</summary>
    public string[] Contacts { get; set; } = [];

    /// <summary>The day of the last thing done together. Null when there has been none.</summary>
    public DateOnly? LastCollabOn { get; set; }

    /// <summary>What it was.</summary>
    public string? LastCollabNote { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Where the friendship stands.</summary>
public static class PartnerStatuses
{
    public const string Active = "active";

    public const string Paused = "paused";

    public const string Ended = "ended";

    public static readonly IReadOnlyList<string> All = [Active, Paused, Ended];
}
