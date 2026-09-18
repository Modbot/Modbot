namespace Modbot.Api.Features.Auth.Login;

/// <param name="Username">
/// An email address <em>or</em> a username, matched case-insensitively against both (server info
/// and account email design §6). One field, because a sign-in form that makes somebody choose
/// which of their two names to type is a form that makes them choose wrong.
///
/// Still called <c>username</c>: the field is what every client already sends, and renaming it
/// would break companions and scripts to say something the description already says.
/// </param>
/// <param name="Password">Checked against the stored hash; never logged.</param>
/// <param name="KeepSignedIn">
/// Whether the session should survive closing the browser. A yes-or-no and nothing more: how long
/// it then lasts is <see cref="Modbot.Api.Auth.ModbotAuth.KeepSignedInLength"/>, which is Modbot's
/// to decide and cannot be raised by what is posted here. Left out, it is no -- the same session
/// every sign-in got before this field existed.
/// </param>
public sealed record LoginRequest(string Username, string Password, bool KeepSignedIn = false);
