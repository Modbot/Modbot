namespace Modbot.VRChat.Session;

/// <summary>
/// Everything needed to build the one authenticated client.
/// </summary>
/// <param name="Username">The VRChat account's login name or email.</param>
/// <param name="Password">Its password. Never logged, never returned to the UI.</param>
/// <param name="TotpSecret">The TOTP shared secret, so re-login needs no human.</param>
/// <param name="AuthCookie">A previous session's <c>auth</c> cookie, if one was stored.</param>
/// <param name="TwoFactorAuthCookie">
/// The <c>twoFactorAuth</c> cookie, which lets a rebuilt session skip the two-factor challenge —
/// one fewer request against the auth endpoint on every restart.
/// </param>
/// <param name="ProxyUrl">The optional single egress proxy (spec 2.3.1).</param>
public sealed record VRChatConnection(
    string? Username = null,
    string? Password = null,
    string? TotpSecret = null,
    string? AuthCookie = null,
    string? TwoFactorAuthCookie = null,
    string? ProxyUrl = null,
    string? ProxyUsername = null,
    string? ProxyPassword = null)
{
    /// <summary>Whether there is an account to log in as at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    /// <summary>Drops the stored session, forcing a password login.</summary>
    public VRChatConnection WithoutSession() =>
        this with { AuthCookie = null, TwoFactorAuthCookie = null };
}

/// <summary>
/// Where the VRChat account's credentials and session live.
/// </summary>
/// <remarks>
/// Spec 4.1: the cookie is persisted in the database, never a <c>cookie.txt</c> on disk. It is a
/// live credential, so it is encrypted by <c>ISecretProtector</c> like the password beside it
/// (spec 8.3), and a deploy that loses it costs a login rather than a locked-out account.
/// </remarks>
public interface IVRChatConnectionStore
{
    Task<VRChatConnection> ReadAsync(CancellationToken ct = default);

    /// <summary>Stores the cookies from a successful login. Nulls clear a dead session.</summary>
    Task SaveSessionAsync(string? authCookie, string? twoFactorAuthCookie, CancellationToken ct = default);
}
