using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Onboarding.CreateAdmin;

/// <summary>
/// Spec 7.1 step 1: the first staff account, and the moment the deployment stops being open.
/// </summary>
public static class CreateAdminHandler
{
    /// <summary>
    /// Names the lock so two concurrent first-run requests contend on the same number. Any stable
    /// string works; this one is spelled out so a future reader can find every lock Modbot takes
    /// by grepping for the prefix.
    /// </summary>
    private const string FirstAdminLock = "modbot:onboarding:first-admin";

    public static async Task<IResult> HandleAsync(
        [FromBody] CreateAdminRequest request,
        [FromServices] ModbotContext db,
        [FromServices] UserAccountService accounts,
        [FromServices] AccountFacts facts,
        [FromServices] AdministratorContact contact,
        [FromServices] IModbotClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(http);

        if (PasswordRules.Validate(request.Username, request.Password, request.ConfirmPassword) is { } problem)
            return Results.BadRequest(new { error = problem });

        // Everything from here to the commit is inside one transaction holding an advisory lock,
        // because the check the access filter already did -- "does any account exist?" -- can go
        // stale between two requests that arrive together. Without this, two browsers pointed at
        // a fresh deployment at the same moment both see "no accounts", and both create an
        // administrator; the second one is an account the operator never asked for and cannot see.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({FirstAdminLock}))", ct);

        var firstRun = !await db.Users.AnyAsync(ct);

        // Re-checked under the lock rather than trusted from the filter. On a first run anyone
        // may create the account; afterwards only somebody already signed in with ManageUsers,
        // and the filter's copy of that answer was read before the lock was held.
        if (!firstRun && !IsAuthorised(http))
            return Results.Unauthorized();

        // The first administrator's email is the contact VRChat sees in every request, so the
        // deployment must not come up without one. Later accounts made through here may skip it.
        var email = UserEndpoints.Clean(request.Email);
        if (firstRun && email is null)
            return Results.BadRequest(new { error = "A contact email is required for the administrator account." });

        if (email is not null && !UserEndpoints.LooksLikeEmail(email))
            return Results.BadRequest(new { error = "That email address does not look like one." });

        var normalized = UserAccountService.Normalize(request.Username);
        if (await db.Users.AnyAsync(u => u.UsernameNormalized == normalized, ct))
            return Results.Conflict(new { error = "That username is already taken." });

        // The first account gets the Administrator role, whose one permission is checked rather
        // than expanded (spec 7.3), so it keeps covering permissions that do not exist yet. A
        // later account created through here gets Viewer: the users screen is where roles are
        // chosen, and this endpoint is not that screen.
        var roleId = firstRun ? BuiltInRoles.AdministratorId : BuiltInRoles.ViewerId;

        var user = await accounts.CreateAsync(request.Username, request.Password, [roleId], ct);

        if (email is not null)
        {
            user.Email = email;
            await db.SaveChangesAsync(ct);
        }

        await facts.RecordAsync(
            FactType.UserCreated,
            user,
            // On a first run there is nobody to attribute it to but the account itself.
            firstRun ? new Actor(user.Id, user.Username) : Actor.Of(http),
            new System.Text.Json.Nodes.JsonObject
            {
                ["roles"] = string.Join(", ", user.Roles.Select(r => r.Role.Name)),
                ["how"] = firstRun ? "first-run" : "setup-wizard",
            },
            ct);

        await transaction.CommitAsync(ct);

        // The User-Agent's contact may have just come into existence.
        contact.Invalidate();

        // Signed in immediately, and only on the first run. It is the same credential check the
        // login endpoint would do a second later, and without it the operator is bounced to a
        // login form in the middle of a wizard that is about to require authentication for its
        // remaining steps -- which reads as the setup having failed.
        if (firstRun)
            await ModbotAuth.SignInAsync(http, user, clock);

        return Results.Ok(SessionUser.From(user));
    }

    private static bool IsAuthorised(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true)
            return false;

        return ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManageUsers);
    }
}
