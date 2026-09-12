namespace Modbot.Api.Features.Auth.Login;

/// <param name="Username">Matched case-insensitively.</param>
/// <param name="Password">Checked against the stored hash; never logged.</param>
public sealed record LoginRequest(string Username, string Password);
