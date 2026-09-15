namespace Modbot.VRChat.Session;

/// <summary>
/// Everything needed to build the one authenticated client.
/// </summary>
/// <param name="Username">The VRChat account's login name or email.</param>
/// <param name="Password">Its password. Never logged, never returned to the UI.</param>
/// <param name="TotpSecret">The TOTP shared secret, so re-login needs no human.</param>
/// <param name="AuthCookie">A previous session's <c>auth</c> cookie, if one was stored.</param>
/// <param name="TwoFactorAuthCookie">
/// The <c>twoFactorAuth</c> cookie, which lets a sign-in skip the two-factor challenge -- two fewer
/// requests counted against the sign-in limit (spec 4.1.2).
/// </param>
/// <param name="ProxyUrl">The optional single egress proxy (spec 2.3.1).</param>
/// <param name="GroupId">
/// The managed group, if one is chosen. The session check reads it (spec 4.1.2): a stored cookie is
/// tested with Get Group rather than with <c>/auth/user</c>, which VRChat treats as a sign-in.
/// </param>
/// <param name="SessionAccount">The username the stored cookies were issued to.</param>
/// <param name="SessionUserId">The VRChat user id the stored session belongs to.</param>
/// <param name="DisplayName">The account's display name, from the last sign-in.</param>
/// <param name="SessionCheckUserId">
/// A profile any signed-in account can read, used to tell a bad session from a lost group
/// (spec 4.1.2). Null means <see cref="DefaultSessionCheckUserId"/>. Never validated (spec 3.1.1).
/// </param>
public sealed record VRChatConnection(
    string? Username = null,
    string? Password = null,
    string? TotpSecret = null,
    string? AuthCookie = null,
    string? TwoFactorAuthCookie = null,
    string? ProxyUrl = null,
    string? ProxyUsername = null,
    string? ProxyPassword = null,
    string? GroupId = null,
    string? SessionAccount = null,
    string? SessionUserId = null,
    string? DisplayName = null,
    string? SessionCheckUserId = null)
{
    /// <summary>
    /// VRChat staff member Nayir's profile: public, long-standing, and readable by any signed-in
    /// account, so a 401 reading it says the session is bad rather than that access to something
    /// was taken away.
    /// </summary>
    public const string DefaultSessionCheckUserId = "usr_fbdf2c30-fcea-4220-88f4-c3f83e11215a";

    /// <summary>Whether there is an account to log in as at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    /// <summary>The profile the session check reads.</summary>
    public string CheckUserId =>
        string.IsNullOrWhiteSpace(SessionCheckUserId) ? DefaultSessionCheckUserId : SessionCheckUserId.Trim();

    /// <summary>Whether a stored session is there to try before signing in.</summary>
    public bool HasSession => !string.IsNullOrWhiteSpace(AuthCookie);

    /// <summary>
    /// The stored session alone, with no password. A client built from this can never send the
    /// credentials by accident: the SDK adds a Basic header to <c>/auth/user</c> whenever a
    /// username is set, which turns a cookie check into a sign-in.
    /// </summary>
    public VRChatConnection WithoutCredentials() =>
        this with { Username = null, Password = null, TotpSecret = null };

    /// <summary>
    /// What a sign-in sends: the password, and the two-factor cookie if there is one -- but never the
    /// old <c>auth</c> cookie, which is the thing being replaced.
    /// </summary>
    public VRChatConnection ForSignIn() => this with { AuthCookie = null };

    /// <summary>Drops the stored session entirely.</summary>
    public VRChatConnection WithoutSession() =>
        this with { AuthCookie = null, TwoFactorAuthCookie = null };
}

/// <summary>Who a new session belongs to, stored beside its cookies.</summary>
public sealed record VRChatSignedInAccount(string? Account, string? UserId, string? DisplayName);

/// <summary>
/// Where the VRChat account's credentials and session live.
/// </summary>
/// <remarks>
/// Spec 4.1: the cookie is persisted in the database, never a <c>cookie.txt</c> on disk. It is a
/// live credential, so it is encrypted by <c>ISecretProtector</c> like the password beside it
/// (spec 8.3). A container filesystem is thrown away on every deploy; the database is not, which is
/// what lets a deploy reuse the session instead of signing in again (spec 4.1.2).
/// </remarks>
public interface IVRChatConnectionStore
{
    Task<VRChatConnection> ReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores the cookies VRChat handed out. Nulls clear a dead session. Leaves who the session
    /// belongs to alone, because a cookie VRChat refreshes belongs to the same account.
    /// </summary>
    Task SaveSessionAsync(string? authCookie, string? twoFactorAuthCookie, CancellationToken ct = default);

    /// <summary>Stores the cookies from a sign-in, and who they belong to.</summary>
    Task SaveSignInAsync(
        string authCookie, string? twoFactorAuthCookie, VRChatSignedInAccount account, CancellationToken ct = default);
}
