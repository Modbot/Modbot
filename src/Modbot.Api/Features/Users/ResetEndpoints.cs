using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Auth.Login;
using Modbot.Api.Features.Onboarding.CreateAdmin;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Email;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.VRChat.RateLimiting;

namespace Modbot.Api.Features.Users;

/// <summary>
/// Using a reset link, and asking for one (accounts and access design §4.1, §4.2). All anonymous:
/// the person has, by definition, no session.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Forgot-password never says whether the username exists.</strong> The response is the
/// same sentence for a real account, an unknown one, a disabled one, one with no way to be
/// reached, and one that already asked five minutes ago. Everything that differs happens after
/// the answer is decided.
/// </para>
/// <para>
/// The link in the message is built from the saved public address and from nothing in the
/// request (see <see cref="OneTimeLinkService.UrlFor"/>). With no public address saved, nothing
/// is sent, and the sign-in page says so.
/// </para>
/// </remarks>
public static class ResetEndpoints
{
    /// <summary>One self-requested link per account per this long. A flood of requests is one message.</summary>
    public static readonly TimeSpan SelfRequestGap = TimeSpan.FromMinutes(10);

    public const string NoPublicAddress =
        "This server's public address is not set.";

    public static IEndpointRouteBuilder MapResetLinks(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var reset = app.MapGroup("/api/reset").WithTags("Users").AllowAnonymous();

        reset.MapGet("/{token}", async (
                string token,
                [FromServices] ModbotContext db,
                [FromServices] OneTimeLinkService links,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var (link, user, reason) = await CheckAsync(db, links, clock, token, ct);
                return Results.Ok(new ResetView(link is not null && reason is null, reason, user?.Username));
            })
            .WithName("DescribeResetLink")
            .WithSummary("Check a reset link")
            .WithDescription("Whether a reset link can still be used, and for which account.")
            .Produces<ResetView>();

        reset.MapPost("/{token}", async (
                string token,
                [FromBody] ResetRequest body,
                [FromServices] ModbotContext db,
                [FromServices] OneTimeLinkService links,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var (link, user, reason) = await CheckAsync(db, links, clock, token, ct);
                if (link is null || user is null || reason is not null)
                    return Results.BadRequest(new { error = reason ?? "This reset link is not valid." });

                if (PasswordRules.ValidatePassword(body.Password, body.ConfirmPassword) is { } problem)
                    return Results.BadRequest(new { error = problem });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                link.UsedAt = clock.UtcNow;
                await accounts.SetPasswordAsync(user, body.Password, ct);

                // Whoever had a session on this account before the reset does not any more --
                // the reset may be happening precisely because that session was not theirs.
                await accounts.EndSessionsAsync(user, ct);

                await facts.RecordAsync(
                    FactType.ResetLinkUsed,
                    user,
                    new Actor(user.Id, user.Username),
                    new JsonObject { ["linkId"] = link.Id.ToString() },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .WithName("UseResetLink")
            .WithSummary("Use a reset link")
            .WithDescription(
                "Set a new password with a reset link. "
                + "Spends the link and ends every session the account had. Sign in with the new password afterwards.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);

        var forgot = app.MapGroup("/api/auth/forgot-password").WithTags("Auth").AllowAnonymous();

        forgot.MapGet("/", async (
                [FromServices] ResetLinkDelivery delivery,
                [FromServices] OneTimeLinkService links,
                CancellationToken ct) =>
            {
                var ways = await delivery.WaysAsync(ct);
                var publicAddress = await links.PublicAddressAsync(ct);

                // Reports what the deployment can do. Nothing here is about any account.
                return Results.Ok(new ForgotPasswordWays(
                    ways.Count > 0 && publicAddress is not null,
                    ways,
                    publicAddress is null ? NoPublicAddress : null));
            })
            .WithName("GetForgotPasswordWays")
            .WithSummary("Get password reset ways")
            .WithDescription("Whether this deployment can send reset links, and how.")
            .Produces<ForgotPasswordWays>();

        forgot.MapPost("/", async (
                [FromBody] ForgotPasswordRequest body,
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] OneTimeLinkService links,
                [FromServices] ResetLinkDelivery delivery,
                [FromServices] AccountFacts facts,
                [FromServices] ForgotPasswordSlowdown slowdown,
                [FromServices] IEmailSender email,
                [FromServices] IDelayScheduler delay,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var address = http.Connection.RemoteIpAddress?.ToString();

                // Every request counts against the address, whatever comes of it, so a flood of
                // them slows down rather than becoming a flood of messages.
                var wait = slowdown.WaitFor(null, address);
                if (wait > TimeSpan.Zero)
                    await delay.DelayAsync(wait, ct);
                slowdown.RecordFailure(null, address);

                // The answer is decided here. Everything below it is invisible to the caller.
                // Whether email is being held under the daily limit (design §4.4) is a fact about
                // the deployment, asked before any account is looked up, so it says nothing about
                // whether this one exists.
                var answer = Results.Ok(await email.WouldQueueAsync(EmailKind.Account, ct)
                    ? ForgotPasswordResponse.Delayed
                    : ForgotPasswordResponse.Standard);

                var publicAddress = await links.PublicAddressAsync(ct);
                if (publicAddress is null)
                    return answer;

                var user = await accounts.FindByUsernameAsync(body.Username ?? string.Empty, ct);
                if (user is null || user.IsDisabled)
                    return answer;

                var recent = clock.UtcNow - SelfRequestGap;
                var askedRecently = await db.OneTimeLinks.AnyAsync(
                    l => l.Kind == OneTimeLinkKind.PasswordReset
                         && l.UserId == user.Id
                         && l.CreatedByUserId == user.Id
                         && l.UsedAt == null
                         && l.CreatedAt > recent,
                    ct);

                if (askedRecently)
                    return answer;

                // Nothing to send it with: no link, no fact. A link nobody can receive would
                // only be a token sitting in the database.
                if (user.Email is null && user.DiscordUserId is null)
                    return answer;

                var (link, token) = await links.CreateResetAsync(user.Id, user.Id, ct);
                var url = OneTimeLinkService.UrlFor(publicAddress, link, token)!;

                var result = await delivery.SendAsync(user, url, link.ExpiresAt, ct);

                await facts.RecordAsync(
                    FactType.ResetLinkCreated,
                    user,
                    new Actor(user.Id, user.Username),
                    new JsonObject
                    {
                        ["requestedBy"] = "self",
                        ["sentVia"] = result.Via,
                        ["sent"] = result.Sent,
                        ["queued"] = result.Queued,
                        ["sendsAt"] = result.Outcome?.SendsAt?.ToString("o"),
                        ["error"] = result.Outcome?.Error,
                        ["expiresAt"] = link.ExpiresAt.ToString("o"),
                    },
                    ct);

                return answer;
            })
            .WithName("ForgotPassword")
            .WithSummary("Ask for a reset link")
            .WithDescription(
                "Always answers the same sentence, so it cannot be used to find out which usernames "
                + "exist. Sends by email when SMTP is set up and the account has an address, otherwise "
                + "by Discord direct message; sends nothing when neither applies or no public address "
                + "is saved.")
            .Produces<ForgotPasswordResponse>();

        return app;
    }

    private static async Task<(OneTimeLink? Link, ModbotUser? User, string? Reason)> CheckAsync(
        ModbotContext db, OneTimeLinkService links, IModbotClock clock, string token, CancellationToken ct)
    {
        var link = await links.FindAsync(token, OneTimeLinkKind.PasswordReset, ct);
        if (link is null)
            return (null, null, "This reset link is not valid.");

        if (link.UsedAt is not null)
            return (link, null, "This reset link has already been used.");

        if (clock.UtcNow >= link.ExpiresAt)
            return (link, null, "This reset link has expired.");

        var user = link.UserId is { } id
            ? await db.Users.Include(u => u.Roles).ThenInclude(r => r.Role).FirstOrDefaultAsync(u => u.Id == id, ct)
            : null;

        if (user is null || user.IsDisabled)
            return (link, null, "This reset link is for an account that has been disabled.");

        return (link, user, null);
    }
}
