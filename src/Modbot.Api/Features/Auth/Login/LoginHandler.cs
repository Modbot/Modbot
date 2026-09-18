using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat.RateLimiting;

namespace Modbot.Api.Features.Auth.Login;

public static class LoginHandler
{
    public static async Task<IResult> HandleAsync(
        [FromBody] LoginRequest request,
        [FromServices] UserAccountService accounts,
        [FromServices] AccountFacts facts,
        [FromServices] LoginSlowdown slowdown,
        [FromServices] IDelayScheduler delay,
        [FromServices] IModbotClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var address = http.Connection.RemoteIpAddress?.ToString();

        // Keyed on what was typed, whichever of the two it is: an attacker who knows somebody's
        // address and their username would otherwise get two separate allowances against the one
        // account. The wait comes before the check, and applies to a correct password too: that is
        // what makes it a slowdown rather than a lockout (design §7).
        var wait = slowdown.WaitFor(request.Username, address);
        if (wait > TimeSpan.Zero)
            await delay.DelayAsync(wait, ct);

        var user = await accounts.VerifyCredentialsAsync(request.Username, request.Password, ct);

        // One answer for every kind of failure -- wrong password, no such account, disabled, an
        // address nobody here uses. Anything more specific tells an attacker which usernames are
        // real, and, since the field also takes an address, which people have accounts here.
        if (user is null)
        {
            slowdown.RecordFailure(request.Username, address);

            // The username attempted and where from; never the password (design §6). Where the
            // name matches an account the fact is about that account, so its history shows the
            // attempts against it.
            var matched = await accounts.FindIdAsync(request.Username ?? string.Empty, ct);

            await facts.RecordAsync(
                FactType.LoginFailed,
                matched?.ToString() ?? AccountFacts.NoAccount,
                actor: null,
                new JsonObject
                {
                    ["username"] = request.Username ?? string.Empty,
                    ["address"] = address,
                },
                ct);

            return Results.Unauthorized();
        }

        slowdown.RecordSuccess(request.Username, address);

        await facts.RecordAsync(
            FactType.Login,
            user,
            new Actor(user.Id, user.Username),
            new JsonObject { ["address"] = address },
            ct);

        await ModbotAuth.SignInAsync(http, user, clock);

        return Results.Ok(SessionUser.From(user));
    }
}
