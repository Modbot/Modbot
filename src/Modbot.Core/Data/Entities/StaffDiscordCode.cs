namespace Modbot.Core.Data.Entities;

/// <summary>
/// A code a staff account runs <c>/verify</c> with, to prove which Discord account is theirs
/// without Discord sign-in (Discord account linking design §14). The table is
/// <c>staff_discord_code</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One per account.</strong> Keyed by the account, so asking for a new code replaces the
/// old one. It works once: the row goes when the code is used.
/// </para>
/// <para>
/// Separate from <see cref="DiscordLinkCode"/>, which a member puts in a VRChat bio to link two
/// accounts of their own. This one ties a Discord account to a Modbot account, which is a
/// different promise with a different reader.
/// </para>
/// </remarks>
public class StaffDiscordCode
{
    /// <summary>The Modbot account that asked for the code.</summary>
    public Guid UserId { get; set; }

    /// <summary>The code, without its dash: <c>K7P42Q</c>. See <c>StaffDiscordCodes</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Wrong codes run that began like this one. At <c>StaffDiscordCodes.MaxFailedTries</c> it no
    /// longer works.
    /// </summary>
    public int FailedTries { get; set; }
}
