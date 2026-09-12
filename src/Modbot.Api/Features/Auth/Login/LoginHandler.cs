using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Modbot.Api.Auth;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Auth.Login;

public static class LoginHandler
{
    public static async Task<IResult> HandleAsync(
        LoginRequest request,
        UserAccountService accounts,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await accounts.VerifyCredentialsAsync(request.Username, request.Password, ct);

        // One answer for every kind of failure -- wrong password, no such account, disabled.
        // Anything more specific tells an attacker which usernames are real.
        if (user is null)
            return Results.Unauthorized();

        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            ModbotAuth.CreatePrincipal(user));

        return Results.Ok(new LoginResponse(user.Id, user.Username, user.Permissions));
    }
}
