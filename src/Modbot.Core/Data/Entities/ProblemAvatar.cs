namespace Modbot.Core.Data.Entities;

/// <summary>
/// One avatar name a moderator marked as a problem avatar. The table is <c>problem_avatar</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>By name, because that is all there is.</strong> VRChat's log names the avatar a person
/// switched to and never gives its id, so the list can only hold names. Two avatars that share a name
/// are one entry; that is the price of the log, and it is why the entry says what it matches.
/// </para>
/// <para>
/// <strong>Matched without regard to case or surrounding spaces</strong> (<see cref="NameKey"/>); the
/// name is kept exactly as it was marked, so the settings page shows what the moderator saw.
/// </para>
/// </remarks>
public class ProblemAvatar
{
    /// <summary>The longest a name may be. A name is one line of the log, not a note.</summary>
    public const int MaxNameLength = 200;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The avatar's name, exactly as the log gave it or as it was typed.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Name"/> trimmed and in lower case: what names are compared by, and what the
    /// database keeps unique.
    /// </summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary>The Modbot account that marked it.</summary>
    public Guid AddedByUserId { get; set; }

    /// <summary>Their username as it was then.</summary>
    public string AddedByUsername { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }

    /// <summary>The form names are compared in.</summary>
    public static string KeyOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Trim().ToLowerInvariant();
    }
}
