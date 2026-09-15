using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Onboarding.CreateAdmin;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Users;

/// <summary>
/// The users page's API (accounts and access design §4). Everything here needs
/// <see cref="ModbotPermissions.ManageUsers"/>.
/// </summary>
/// <remarks>
/// <para>
/// Accounts are never deleted, only disabled: facts reference the actor's id, and deleting the
/// account would orphan the attribution that spec 5.9.1 exists to preserve.
/// </para>
/// <para>
/// Two guards run on anything that narrows an account. <strong>The last administrator</strong>:
/// disabling an account or changing its roles is refused when it would leave no enabled account
/// holding Administrator (design §3.4). <strong>No giving what you do not have</strong>: a caller
/// may only assign roles whose permissions they hold themselves, or a manage-users account could
/// hand itself Administrator by way of a role.
/// </para>
/// <para>
/// Every change and its fact commit in one transaction (design §6). Every parameter is
/// explicitly attributed, because minimal APIs infer an unattributed concrete type as the body
/// and on a GET that throws while the route is mapped.
/// </para>
/// </remarks>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUsers(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/users")
            .WithTags("Users")
            .RequiresFlag(ModbotPermissions.ManageUsers);

        group.MapGet("/", async (
                [FromServices] UserAccountService accounts,
                CancellationToken ct) =>
            {
                var users = await accounts.UsersWithRoles()
                    .AsNoTracking()
                    .OrderBy(u => u.UsernameNormalized)
                    .ToListAsync(ct);

                return Results.Ok(users.Select(UserSummary.From).ToList());
            })
            .WithName("ListUsers")
            .WithSummary("Every staff account, with roles and state")
            .Produces<List<UserSummary>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                [FromBody] CreateUserRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (PasswordRules.Validate(body.Username, body.Password, body.ConfirmPassword) is { } problem)
                    return Results.BadRequest(new { error = problem });

                if (await MayNotAssignAsync(accounts, http, body.RoleIds ?? [], ct) is { } refused)
                    return refused;

                var normalized = UserAccountService.Normalize(body.Username);
                if (await db.Users.AnyAsync(u => u.UsernameNormalized == normalized, ct))
                    return Results.Conflict(new { error = "That username is already taken." });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var user = await accounts.CreateAsync(body.Username, body.Password, body.RoleIds ?? [], ct);

                user.Email = Clean(body.Email);
                user.DiscordUserId = Clean(body.DiscordUserId);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.UserCreated,
                    user,
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["roles"] = string.Join(", ", user.Roles.Select(r => r.Role.Name)),
                        ["how"] = "temporary-password",
                    },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(UserSummary.From(user));
            })
            .WithName("CreateUser")
            .WithSummary("Create an account with a temporary password")
            .WithDescription(
                "The administrator passes the password on. Until the person changes it, this is "
                + "an account two people can act as -- prefer an invite link, which is why the "
                + "users page offers that first.")
            .Produces<UserSummary>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPut("/{id:guid}/roles", async (
                Guid id,
                [FromBody] SetRolesRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var user = await accounts.FindAsync(id, ct);
                if (user is null)
                    return Results.NotFound();

                if (await MayNotAssignAsync(accounts, http, body.RoleIds ?? [], ct) is { } refused)
                    return refused;

                IReadOnlyList<ModbotRole> roles;
                try
                {
                    roles = await accounts.RolesAsync(body.RoleIds ?? [], ct);
                }
                catch (UnknownRoleException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }

                var after = ModbotRole.Union(roles.Select(r => r.Permissions));
                if (!await accounts.AnAdministratorWouldRemainAsync((id, !user.IsDisabled, after), ct))
                    return Results.BadRequest(new { error = LastAdministrator });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // Ids as well as names, so a Discord route can tell which roles the account held
                // before this change even after a role is renamed (Discord event routes design §3).
                var beforeIds = user.Roles.Select(r => r.RoleId).Order().ToList();
                var (before, now) = await accounts.SetRolesAsync(user, body.RoleIds ?? [], ct);

                await facts.RecordAsync(
                    FactType.UserRolesChanged,
                    user,
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["before"] = string.Join(", ", before),
                        ["after"] = string.Join(", ", now),
                        ["beforeRoleIds"] = new JsonArray(beforeIds.Select(r => (JsonNode?)r.ToString()).ToArray()),
                        ["afterRoleIds"] = new JsonArray(roles.Select(r => r.Id).Order().Select(r => (JsonNode?)r.ToString()).ToArray()),
                    },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(UserSummary.From(user));
            })
            .WithName("SetUserRoles")
            .WithSummary("Replace an account's roles")
            .WithDescription("Takes effect on that person's next request, not their next sign-in.")
            .Produces<UserSummary>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/disable", async (
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var user = await accounts.FindAsync(id, ct);
                if (user is null)
                    return Results.NotFound();

                if (ModbotAuth.UserIdOf(http.User) == id)
                    return Results.BadRequest(new { error = "You cannot disable your own account." });

                if (user.IsDisabled)
                    return Results.Ok(UserSummary.From(user));

                if (!await accounts.AnAdministratorWouldRemainAsync((id, false, user.EffectivePermissions), ct))
                    return Results.BadRequest(new { error = LastAdministrator });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                user.IsDisabled = true;

                // Ends every session now, and stays in place if the account is re-enabled, so an
                // old cookie does not come back to life with it.
                await accounts.EndSessionsAsync(user, ct);

                await facts.RecordAsync(FactType.UserDisabled, user, Actor.Of(http), null, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(UserSummary.From(user));
            })
            .WithName("DisableUser")
            .WithSummary("Disable an account and end its sessions")
            .WithDescription(
                "Never delete: facts reference the account. Refused for your own account and for "
                + "the last enabled administrator.")
            .Produces<UserSummary>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/enable", async (
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var user = await accounts.FindAsync(id, ct);
                if (user is null)
                    return Results.NotFound();

                if (!user.IsDisabled)
                    return Results.Ok(UserSummary.From(user));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                user.IsDisabled = false;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(FactType.UserEnabled, user, Actor.Of(http), null, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(UserSummary.From(user));
            })
            .WithName("EnableUser")
            .WithSummary("Re-enable a disabled account")
            .Produces<UserSummary>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}/contact", async (
                Guid id,
                [FromBody] ContactRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                [FromServices] AdministratorContact contact,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var user = await accounts.FindAsync(id, ct);
                if (user is null)
                    return Results.NotFound();

                return await ApplyContactAsync(db, facts, contact, user, body, Actor.Of(http), ct)
                    ? Results.Ok(UserSummary.From(user))
                    : Results.BadRequest(new { error = "That email address does not look like one." });
            })
            .WithName("SetUserContact")
            .WithSummary("Set the email address and Discord user id a reset link can be sent to")
            .Produces<UserSummary>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/reset-link", async (
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] OneTimeLinkService links,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var user = await accounts.FindAsync(id, ct);
                if (user is null)
                    return Results.NotFound();

                if (user.IsDisabled)
                    return Results.BadRequest(new { error = "That account is disabled. Enable it first." });

                var actor = Actor.Of(http)!.Value;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var (link, token) = await links.CreateResetAsync(actor.Id, user.Id, ct);

                await facts.RecordAsync(
                    FactType.ResetLinkCreated,
                    user,
                    actor,
                    new JsonObject
                    {
                        ["requestedBy"] = "administrator",
                        ["expiresAt"] = link.ExpiresAt.ToString("o"),
                    },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(new LinkCreated(
                    link.Id,
                    OneTimeLinkService.PathFor(link, token),
                    OneTimeLinkService.UrlFor(await links.PublicAddressAsync(ct), link, token),
                    link.ExpiresAt));
            })
            .WithName("CreateResetLink")
            .WithSummary("Make a one-time password reset link for this account")
            .WithDescription(
                "Shown once. The server keeps only a hash, so copy it now. Good for 24 hours, "
                + "and using it ends every session the account has.")
            .Produces<LinkCreated>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    internal const string LastAdministrator =
        "That would leave nobody who can administer Modbot.";

    /// <summary>
    /// Sets the contact fields from a request, recording which changed. False when the email is
    /// not shaped like one.
    /// </summary>
    internal static async Task<bool> ApplyContactAsync(
        ModbotContext db,
        AccountFacts facts,
        AdministratorContact contact,
        ModbotUser user,
        ContactRequest body,
        Actor? actor,
        CancellationToken ct)
    {
        var changed = new List<string>();

        if (body.Email is not null)
        {
            var email = Clean(body.Email);
            if (email is not null && !LooksLikeEmail(email))
                return false;

            if (email != user.Email)
            {
                user.Email = email;
                changed.Add("email");
            }
        }

        if (body.DiscordUserId is not null)
        {
            var discord = Clean(body.DiscordUserId);
            if (discord != user.DiscordUserId)
            {
                user.DiscordUserId = discord;
                changed.Add("discordUserId");
            }
        }

        if (changed.Count == 0)
            return true;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await db.SaveChangesAsync(ct);

        // Which fields, never the values: an address is not a secret, but it is not history
        // anybody needs in the audit log either.
        await facts.RecordAsync(
            FactType.ContactChanged,
            user,
            actor,
            new JsonObject { ["fields"] = string.Join(", ", changed) },
            ct);

        await transaction.CommitAsync(ct);

        // The User-Agent's contact is read from the oldest administrator's email; if this was it,
        // the next client build should see the new address rather than the cached one.
        if (changed.Contains("email"))
            contact.Invalidate();

        return true;
    }

    /// <summary>
    /// Refuses roles whose permissions the caller does not hold. Administrator holds everything.
    /// </summary>
    private static async Task<IResult?> MayNotAssignAsync(
        UserAccountService accounts, HttpContext http, IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        var held = ModbotAuth.PermissionsOf(http.User);
        if (held.HasFlag(ModbotPermissions.Administrator))
            return null;

        IReadOnlyList<ModbotRole> roles;
        try
        {
            roles = await accounts.RolesAsync(roleIds, ct);
        }
        catch (UnknownRoleException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }

        var wanted = ModbotRole.Union(roles.Select(r => r.Permissions));
        return ModbotAuth.Allows(held, wanted)
            ? null
            : Results.BadRequest(new { error = "You can only give people permissions you have yourself." });
    }

    internal static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The loosest useful check: something, an @, something. Anything stricter rejects real addresses.</summary>
    internal static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at < value.Length - 1 && !value.Contains(' ') && value.Length <= 256;
    }
}
