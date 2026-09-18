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
public sealed record LoginRequest(string Username, string Password);
