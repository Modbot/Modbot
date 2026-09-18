namespace Modbot.Api.Features.Onboarding.CreateAdmin;

/// <param name="Username">How this person signs in. Matched case-insensitively.</param>
/// <param name="Password">Never logged, never echoed back (spec 4.4.1).</param>
/// <param name="ConfirmPassword">
/// Optional, and re-checked here when present. The browser already compares the two fields; this
/// exists because a mismatch that slips through creates an account whose password nobody knows,
/// and the only recovery from that is editing the database by hand.
/// </param>
/// <param name="Email">
/// Required. For the first account it is also the contact email VRChat sees in every request's
/// User-Agent -- the person to write to before blocking -- and the address <c>GET /api/server</c>
/// gives out as the owner's. For every account it is where the reset link goes and the other thing
/// the sign-in form accepts (server info and account email design §4).
/// </param>
/// <param name="SubscribeToUpdates">
/// The person ticked "Receive emails from Modbot about new features and updates". Ignored when
/// this server has no Modbot Cloud to ask (design §5).
/// </param>
public sealed record CreateAdminRequest(
    string Username,
    string Password,
    string? ConfirmPassword = null,
    string? Email = null,
    bool SubscribeToUpdates = false);

/// <summary>
/// The rules a password has to satisfy, and the reason there are so few of them.
/// </summary>
/// <remarks>
/// A length floor and nothing else. Composition rules — a digit, a symbol, mixed case — are known
/// to push people towards <c>Password1!</c> and away from the long passphrase that is actually
/// strong, and NIST withdrew them for that reason. The one rule kept is the one that is not
/// gameable.
/// </remarks>
public static class PasswordRules
{
    public const int MinimumLength = 12;

    public const int MaximumUsernameLength = 64;

    public static string? Validate(string? username, string? password, string? confirmation)
        => ValidateUsername(username) ?? ValidatePassword(password, confirmation);

    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "A username is required.";

        if (username.Trim().Length > MaximumUsernameLength)
            return $"That username is longer than {MaximumUsernameLength} characters.";

        return null;
    }

    public static string? ValidatePassword(string? password, string? confirmation)
    {
        if (string.IsNullOrEmpty(password))
            return "A password is required.";

        if (password.Length < MinimumLength)
            return $"The password must be at least {MinimumLength} characters.";

        // Checked server-side as well as in the form: a mismatch here means the account is
        // created with a password nobody knows, and the only recovery is database surgery.
        if (confirmation is not null && !string.Equals(password, confirmation, StringComparison.Ordinal))
            return "The passwords do not match.";

        return null;
    }
}
