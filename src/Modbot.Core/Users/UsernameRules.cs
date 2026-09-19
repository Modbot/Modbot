namespace Modbot.Core.Users;

/// <summary>
/// What a username may be made of (username rules and deleting accounts design §2).
/// </summary>
/// <remarks>
/// <para>
/// Letters, numbers and underscores. Nothing else — no spaces, no dots, no dashes, no accents, no
/// emoji. A username is typed into a sign-in box, read out loud over voice, pasted into a Discord
/// message and matched case-insensitively against what is stored, and every one of those goes
/// wrong for a name with a space or a look-alike letter in it. Two accounts called
/// <c>alice</c> and <c>аlice</c> — the second with a Cyrillic а — are two accounts nobody can tell
/// apart.
/// </para>
/// <para>
/// <strong>The rule applies when a username is set, never when one is read.</strong> Accounts made
/// before this rule existed may hold anything, and they keep signing in: the sign-in path matches
/// on the stored normalized form and asks no questions about its characters. Changing such a name
/// is what the rule catches.
/// </para>
/// <para>
/// Checked against the <em>trimmed</em> name, which is what gets stored, so leading and trailing
/// spaces are tidied away rather than refused.
/// </para>
/// </remarks>
public static class UsernameRules
{
    /// <summary>As long as the column (<c>modbot_user.username</c>).</summary>
    public const int MaximumLength = 64;

    /// <summary>The one sentence a moderator sees when the characters are wrong.</summary>
    public const string WrongCharacters =
        "A username can only have letters, numbers and underscores in it.";

    /// <summary>
    /// The problem with this username in plain words, or null when there is none.
    /// </summary>
    public static string? Validate(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "A username is required.";

        var trimmed = username.Trim();

        if (trimmed.Length > MaximumLength)
            return $"That username is longer than {MaximumLength} characters.";

        return Allowed(trimmed) ? null : WrongCharacters;
    }

    /// <summary>Whether the trimmed name is one this rule would accept.</summary>
    public static bool LooksLike(string? username) => Validate(username) is null;

    /// <summary>
    /// Deliberately not a regular expression and deliberately not <c>char.IsLetterOrDigit</c>:
    /// both would let in every letter and digit Unicode has, which is the thing being kept out.
    /// </summary>
    private static bool Allowed(string trimmed)
    {
        foreach (var c in trimmed)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
            if (!ok)
                return false;
        }

        return true;
    }
}
