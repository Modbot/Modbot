using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
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
        CreateAdminRequest request,
        ModbotContext db,
        UserAccountService accounts,
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
        // may create the account; afterwards only somebody already signed in with ManageSettings,
        // and the filter's copy of that answer was read before the lock was held.
        if (!firstRun && !IsAuthorised(http))
            return Results.Unauthorized();

        var normalized = request.Username.Trim().ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.UsernameNormalized == normalized, ct))
            return Results.Conflict(new { error = "That username is already taken." });

        // The first account gets Administrator, which is checked rather than expanded (spec 7.3),
        // so it keeps covering permissions that do not exist yet. Later accounts are created from
        // the user-management screen with narrower flags -- this endpoint is not that screen.
        var permissions = firstRun
            ? ModbotPermissions.Administrator
            : ModbotPermissions.ManageSettings | ModbotPermissions.ViewMembers;

        var user = await accounts.CreateAsync(request.Username, request.Password, permissions, ct);

        await transaction.CommitAsync(ct);

        // Signed in immediately, and only on the first run. It is the same credential check the
        // login endpoint would do a second later, and without it the operator is bounced to a
        // login form in the middle of a wizard that is about to require authentication for its
        // remaining steps -- which reads as the setup having failed.
        if (firstRun)
        {
            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                ModbotAuth.CreatePrincipal(user));
        }

        return Results.Ok(new CreateAdminResponse(user.Id, user.Username, user.Permissions));
    }

    private static bool IsAuthorised(HttpContext http)
    {
        if (http.User.Identity?.IsAuthenticated != true)
            return false;

        var held = ModbotAuth.PermissionsOf(http.User);

        return held.HasFlag(ModbotPermissions.Administrator)
            || held.HasFlag(ModbotPermissions.ManageUsers);
    }
}
