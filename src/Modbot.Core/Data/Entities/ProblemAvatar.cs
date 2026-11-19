namespace Modbot.Core.Data.Entities;

/// <summary>
/// One avatar a moderator marked as a problem avatar, by its name or from a link. The table is <c>problem_avatar</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Alerts match by name, because that is all the log has.</strong> VRChat's log names the avatar
/// a person switched to and never gives its id. An entry can also carry an avatar id, when it was added
/// from a link or an id, but an alert still matches the name, so an entry with an id and no name never
/// alerts. Two avatars that share a name are one entry; that is the price of the log.
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

    /// <summary>The longest an avatar id may be. A real one is 41 characters; ids are never checked for shape.</summary>
    public const int MaxIdLength = 100;

    /// <summary>The avatar's name, exactly as the log gave it or as it was typed. Empty for an id with no name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Name"/> trimmed and in lower case: what names are compared by, and what the
    /// database keeps unique. Empty when there is no name, and then it is not compared.
    /// </summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary>The <c>avtr_</c> id, when the entry was added from a link or an id. Null when it was added by name.</summary>
    public string? AvatarId { get; set; }

    /// <summary>The Modbot account that marked it.</summary>
    public Guid AddedByUserId { get; set; }

    /// <summary>Their username as it was then.</summary>
    public string AddedByUsername { get; set; } = string.Empty;

    public DateTimeOffset AddedAt { get; set; }

    /// <summary>
    /// The form names are compared in. Cut to <see cref="MaxNameLength"/>, as a stored name is, so a
    /// name too long for the column still matches the entry it was saved as.
    /// </summary>
    public static string KeyOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Clip(name.Trim().ToLowerInvariant());
    }

    /// <summary>
    /// The avatar id in what an admin pasted: the text itself when it is a bare <c>avtr_</c> id, or the
    /// <c>avtr_</c> part of a vrchat.com link's path. Null for anything else, which is taken as a name.
    /// </summary>
    /// <remarks>
    /// The id is never checked for shape (foundation §3.1.1): the link is only taken apart to find
    /// the part that starts with <c>avtr_</c>.
    /// </remarks>
    public static string? AvatarIdFrom(string? text)
    {
        const string Prefix = "avtr_";

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var link)
            && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp))
        {
            return link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .FirstOrDefault(part => part.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && part.Length > Prefix.Length);
        }

        return trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && trimmed.Length > Prefix.Length
            && !trimmed.Any(char.IsWhiteSpace)
                ? trimmed
                : null;
    }

    /// <summary>A name cut to <see cref="MaxNameLength"/> characters, the most the column holds.</summary>
    public static string Clip(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }
}
