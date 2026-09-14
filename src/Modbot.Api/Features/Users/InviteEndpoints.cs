using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Auth;
using Modbot.Api.Features.Onboarding.CreateAdmin;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Users;

/// <summary>
/// Invite links (accounts and access design §4.1): made by somebody with
/// <see cref="ModbotPermissions.ManageUsers"/>, used by somebody with no account at all.
/// </summary>
/// <remarks>
/// An invite is good once, for 72 hours, and only while the person who made it is still enabled:
/// it is that person's standing offer, and a disabled account cannot make offers. The invitee
/// picks their own username and password, so nobody but them ever knows the password -- which is
/// why the users page offers this before the temporary-password path.
/// </remarks>
public static class InviteEndpoints
{
    public static IEndpointRouteBuilder MapInvites(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var manage = app.MapGroup("/api/invites")
            .WithTags("Users")
            .RequiresFlag(ModbotPermissions.ManageUsers);

        manage.MapPost("/", async (
                [FromBody] CreateInviteRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] OneTimeLinkService links,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                IReadOnlyList<ModbotRole> roles;
                try
                {
                    roles = await accounts.RolesAsync(body.RoleIds ?? [], ct);
                }
                catch (UnknownRoleException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }

                var held = ModbotAuth.PermissionsOf(http.User);
                if (!ModbotAuth.Allows(held, ModbotRole.Union(roles.Select(r => r.Permissions))))
                    return Results.BadRequest(new { error = "You can only give people permissions you have yourself." });

                var actor = Actor.Of(http)!.Value;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var (link, token) = await links.CreateInviteAsync(actor.Id, body.RoleIds ?? [], ct);

                await facts.RecordAsync(
                    FactType.UserInvited,
                    link.Id.ToString(),
                    actor,
                    new JsonObject
                    {
                        ["roles"] = string.Join(", ", roles.Select(r => r.Name)),
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
            .WithName("CreateInvite")
            .WithSummary("Make a one-time invite link carrying these roles")
            .WithDescription("Shown once; the server keeps only a hash. Good for 72 hours.")
            .Produces<LinkCreated>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        manage.MapGet("/", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var now = clock.UtcNow;

                var pending = await db.OneTimeLinks
                    .AsNoTracking()
                    .Where(l => l.Kind == OneTimeLinkKind.Invite && l.UsedAt == null && l.ExpiresAt > now)
                    .OrderByDescending(l => l.CreatedAt)
                    .ToListAsync(ct);

                var creatorIds = pending.Select(l => l.CreatedByUserId).Distinct().ToList();
                var roleIds = pending.SelectMany(l => l.RoleIds).Distinct().ToList();

                var creators = await db.Users.AsNoTracking()
                    .Where(u => creatorIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => u.Username, ct);
                var roles = await db.Roles.AsNoTracking()
                    .Where(r => roleIds.Contains(r.Id))
                    .ToDictionaryAsync(r => r.Id, r => r.Name, ct);

                return Results.Ok(pending.Select(l => new PendingInvite(
                    l.Id,
                    creators.GetValueOrDefault(l.CreatedByUserId, "unknown"),
                    l.RoleIds.Select(id => roles.GetValueOrDefault(id, "(deleted role)")).ToList(),
                    l.CreatedAt,
                    l.ExpiresAt)).ToList());
            })
            .WithName("ListInvites")
            .WithSummary("Invite links that have not been used or expired")
            .Produces<List<PendingInvite>>()
            .Produces(StatusCodes.Status403Forbidden);

        manage.MapDelete("/{id:guid}", async (
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var link = await db.OneTimeLinks
                    .FirstOrDefaultAsync(l => l.Id == id && l.Kind == OneTimeLinkKind.Invite, ct);

                if (link is null)
                    return Results.NotFound();

                if (link.UsedAt is not null)
                    return Results.BadRequest(new { error = "That invite has already been used." });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.OneTimeLinks.Remove(link);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(FactType.UserInviteRevoked, link.Id.ToString(), Actor.Of(http), null, ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .WithName("RevokeInvite")
            .WithSummary("Take back an unused invite link")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        // The invitee's side. Anonymous by nature: they have no account yet.
        var join = app.MapGroup("/api/join").WithTags("Users").AllowAnonymous();

        join.MapGet("/{token}", async (
                string token,
                [FromServices] ModbotContext db,
                [FromServices] OneTimeLinkService links,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var (link, reason) = await CheckAsync(db, links, clock, token, ct);

                if (link is null)
                    return Results.Ok(new InviteView(false, reason, null, [], null));

                var invitedBy = await db.Users.AsNoTracking()
                    .Where(u => u.Id == link.CreatedByUserId)
                    .Select(u => u.Username)
                    .FirstOrDefaultAsync(ct);

                var roles = await db.Roles.AsNoTracking()
                    .Where(r => link.RoleIds.Contains(r.Id))
                    .Select(r => r.Name)
                    .ToListAsync(ct);

                return Results.Ok(new InviteView(reason is null, reason, invitedBy, roles, link.ExpiresAt));
            })
            .WithName("DescribeInvite")
            .WithSummary("Whether an invite link can still be used, and what it offers")
            .Produces<InviteView>();

        join.MapPost("/{token}", async (
                string token,
                [FromBody] JoinRequest body,
                [FromServices] ModbotContext db,
                [FromServices] OneTimeLinkService links,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var (link, reason) = await CheckAsync(db, links, clock, token, ct);
                if (link is null || reason is not null)
                    return Results.BadRequest(new { error = reason ?? "This invite link is not valid." });

                if (PasswordRules.Validate(body.Username, body.Password, body.ConfirmPassword) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var normalized = UserAccountService.Normalize(body.Username);
                if (await db.Users.AnyAsync(u => u.UsernameNormalized == normalized, ct))
                    return Results.Conflict(new { error = "That username is already taken." });

                var inviter = await db.Users.AsNoTracking().FirstAsync(u => u.Id == link.CreatedByUserId, ct);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // The invite is marked used before the account is created, inside the same
                // transaction, so two people opening one link cannot both get an account.
                link.UsedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);

                ModbotUser user;
                try
                {
                    user = await accounts.CreateAsync(body.Username, body.Password, link.RoleIds, ct);
                }
                catch (UnknownRoleException)
                {
                    return Results.BadRequest(new
                    {
                        error = "A role on this invite no longer exists.",
                    });
                }

                link.UserId = user.Id;
                await db.SaveChangesAsync(ct);

                var actor = new Actor(inviter.Id, inviter.Username);

                await facts.RecordAsync(
                    FactType.UserCreated,
                    user,
                    actor,
                    new JsonObject
                    {
                        ["roles"] = string.Join(", ", user.Roles.Select(r => r.Role.Name)),
                        ["how"] = "invite",
                    },
                    ct);

                await facts.RecordAsync(
                    FactType.UserInviteUsed,
                    user,
                    actor,
                    new JsonObject { ["inviteId"] = link.Id.ToString() },
                    ct);

                await transaction.CommitAsync(ct);

                await ModbotAuth.SignInAsync(http, user, clock);

                return Results.Ok(SessionUser.From(user));
            })
            .WithName("AcceptInvite")
            .WithSummary("Create an account from an invite link and sign in")
            .WithDescription("The link is spent the moment this succeeds. The new account still has to link its VRChat account.")
            .Produces<SessionUser>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>The link, and why it cannot be used if it cannot. Null link means no such invite.</summary>
    private static async Task<(OneTimeLink? Link, string? Reason)> CheckAsync(
        ModbotContext db, OneTimeLinkService links, IModbotClock clock, string token, CancellationToken ct)
    {
        var link = await links.FindAsync(token, OneTimeLinkKind.Invite, ct);
        if (link is null)
            return (null, "This invite link is not valid.");

        if (link.UsedAt is not null)
            return (link, "This invite link has already been used.");

        if (clock.UtcNow >= link.ExpiresAt)
            return (link, "This invite link has expired.");

        var inviterEnabled = await db.Users.AsNoTracking()
            .Where(u => u.Id == link.CreatedByUserId)
            .Select(u => !u.IsDisabled)
            .FirstOrDefaultAsync(ct);

        if (!inviterEnabled)
            return (link, "The account that made this invite link has been disabled.");

        return (link, null);
    }
}
