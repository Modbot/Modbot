using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Onboarding.CreateAdmin;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Auth.Account;

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string? ConfirmPassword);

public sealed record ChangeUsernameRequest(string Username, string CurrentPassword);

/// <summary>
/// The signed-in person's own account (accounts and access design §4): password, username,
/// contact details, and ending every session.
/// </summary>
/// <remarks>
/// Password and username changes require the current password, so a browser left open cannot be
/// used to take over the account by renaming it or resetting its password. Both are recorded as
/// facts; the username fact carries the old and new names, because "who was Gunner24 before" is a
/// question the audit log has to keep answering.
/// </remarks>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccount(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/auth").WithTags("Auth").RequireAuthorization();

        group.MapPut("/password", async (
                [FromBody] ChangePasswordRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var user = await accounts.FindAsync(ModbotAuth.UserIdOf(http.User)!.Value, ct);
                if (user is null)
                    return Results.Unauthorized();

                if (!accounts.PasswordMatches(user, body.CurrentPassword))
                    return Results.BadRequest(new { error = "The current password is not right." });

                if (PasswordRules.ValidatePassword(body.NewPassword, body.ConfirmPassword) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var actor = new Actor(user.Id, user.Username);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                await accounts.SetPasswordAsync(user, body.NewPassword, ct);

                // Every other session ends. This one is re-issued below, stamped with the same
                // instant as the cut-off, which the session check treats as surviving it.
                var cutOff = await accounts.EndSessionsAsync(user, ct);

                await facts.RecordAsync(FactType.PasswordChanged, user, actor, null, ct);

                await transaction.CommitAsync(ct);

                await ModbotAuth.SignInAsync(http, user, cutOff);

                return Results.NoContent();
            })
            .WithName("ChangePassword")
            .WithSummary("Change your own password")
            .WithDescription("Ends every other session you have. The one you did this from stays signed in.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPut("/username", async (
                [FromBody] ChangeUsernameRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var user = await accounts.FindAsync(ModbotAuth.UserIdOf(http.User)!.Value, ct);
                if (user is null)
                    return Results.Unauthorized();

                if (!accounts.PasswordMatches(user, body.CurrentPassword))
                    return Results.BadRequest(new { error = "The current password is not right." });

                if (PasswordRules.ValidateUsername(body.Username) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var normalized = UserAccountService.Normalize(body.Username);
                var from = user.Username;

                if (normalized != user.UsernameNormalized
                    && await db.Users.AnyAsync(u => u.UsernameNormalized == normalized && u.Id != user.Id, ct))
                {
                    return Results.Conflict(new { error = "That username is already taken." });
                }

                if (from == body.Username.Trim())
                    return Results.Ok(SessionUser.From(user));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                user.Username = body.Username.Trim();
                user.UsernameNormalized = normalized;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.UsernameChanged,
                    user,
                    new Actor(user.Id, user.Username),
                    new JsonObject { ["from"] = from, ["to"] = user.Username },
                    ct);

                await transaction.CommitAsync(ct);

                // Re-issued so the name in the cookie is the new one. Same signed-in-at, so the
                // session's age -- and its standing against any cut-off -- does not change.
                var signedInAt = ModbotAuth.SignedInAtOf(http.User);
                if (signedInAt is { } at)
                    await ModbotAuth.SignInAsync(http, user, at);

                return Results.Ok(SessionUser.From(user));
            })
            .WithName("ChangeUsername")
            .WithSummary("Change your own username")
            .WithDescription("Same rule as everywhere: two names that differ only in case are one name.")
            .Produces<SessionUser>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPut("/contact", async (
                [FromBody] ContactRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                [FromServices] AdministratorContact contact,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var user = await accounts.FindAsync(ModbotAuth.UserIdOf(http.User)!.Value, ct);
                if (user is null)
                    return Results.Unauthorized();

                var actor = new Actor(user.Id, user.Username);

                return await UserEndpoints.ApplyContactAsync(db, facts, contact, accounts, user, body, actor, ct) is { } problem
                    ? Results.BadRequest(new { error = problem })
                    : Results.Ok(SessionUser.From(user));
            })
            .WithName("SetOwnContact")
            .WithSummary("Set the email address and Discord user id a reset link can reach you at")
            .Produces<SessionUser>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/sign-out-everywhere", async (
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var user = await accounts.FindAsync(ModbotAuth.UserIdOf(http.User)!.Value, ct);
                if (user is null)
                    return Results.Unauthorized();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                await accounts.EndSessionsAsync(user, ct);
                await facts.RecordAsync(FactType.SignedOutEverywhere, user, new Actor(user.Id, user.Username), null, ct);

                await transaction.CommitAsync(ct);

                await ModbotAuth.SignOutAsync(http);

                return Results.NoContent();
            })
            .WithName("SignOutEverywhere")
            .WithSummary("End every session you have, including this one")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
