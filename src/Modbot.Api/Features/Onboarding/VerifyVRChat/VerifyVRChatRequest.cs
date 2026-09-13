namespace Modbot.Api.Features.Onboarding.VerifyVRChat;

/// <param name="Username">
/// The email address or login name of the VRChat account Modbot will act as. Not validated for
/// shape: it is a credential for someone else's system, and guessing at its format only produces
/// false rejections.
/// </param>
/// <param name="Password">The account's password. Encrypted at rest (spec 8.3); never returned.</param>
/// <param name="TotpSecret">
/// The TOTP shared secret, base32, as the authenticator app was given it. Optional only because
/// an account without two-factor authentication exists — but Modbot is a daemon and cannot be
/// asked for a code, so an account that later enables 2FA without this stops being able to log in.
/// </param>
public sealed record VerifyVRChatRequest(string Username, string Password, string? TotpSecret = null);

/// <param name="DisplayName">Who VRChat says these credentials belong to.</param>
/// <param name="UserId">The account's id, stored and compared as an opaque string (spec 3.1.1).</param>
/// <param name="VerifiedAt">The instant, from <c>IModbotClock</c>, the credentials were accepted.</param>
public sealed record VerifyVRChatResponse(string? DisplayName, string? UserId, DateTimeOffset VerifiedAt);
