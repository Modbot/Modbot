using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Chat;
using Modbot.Api.Features.DiscordLink;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Npgsql;

namespace Modbot.Api.Features.Auth.Account;

/// <summary>
/// "Connect Discord" on the account page: the person signs in to Discord, and the id Discord gives
/// back goes on their Modbot account as proven (accounts and access design §4.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The member link page's sign-in, reused.</strong> The same OAuth client, the same
/// <c>identify</c> scope, <c>state</c> and PKCE, and the same callback address, so an operator who
/// set up account linking has nothing more to add in Discord's Developer Portal. The sign-in cookie
/// carries the Modbot account the attempt was started for; the callback sends an attempt that
/// carries one here instead of to the link page. The token is used for one read of
/// <c>users/@me</c> and revoked, as on the link page; nothing from Discord is kept but the id and
/// the username.
/// </para>
/// <para>
/// <strong>Started and finished by the same signed-in person.</strong> The callback checks that the
/// browser coming back from Discord is still signed in as the account in the sign-in cookie. The
/// session cookie is <c>SameSite=Lax</c>, so it comes along on that top-level redirect.
/// </para>
/// <para>
/// <strong>One proven Discord account, one Modbot account.</strong> A Discord account another
/// Modbot account has proven is refused. One that other accounts only typed in, before proving
/// existed, comes off them: whoever proves it is the person it belongs to, and each of those
/// accounts gets a <c>modbot.user.discord.unlink</c> fact saying so.
/// </para>
/// <para>
/// Nobody sets anybody else's Discord account. The users page shows it and has no control for it.
/// </para>
/// </remarks>
public static class DiscordConnectEndpoints
{
    public const string AccountPage = "/account";

    /// <summary>The query value the account page reads after Discord sends the browser back.</summary>
    public const string ResultParameter = "discord";

    public static IEndpointRouteBuilder MapDiscordConnect(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/auth/discord").WithTags("Auth").RequireAuthorization();

        group.MapGet("/connect", async (
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] LinkCookies cookies,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                var client = await DiscordLinkEndpoints.ClientAsync(db, protector, ct);
                if (client is null)
                    return Results.Redirect(Back("not-set-up"));

                var attempt = new SignInAttempt(
                    DiscordOAuth.NewRandomValue(),
                    DiscordOAuth.NewRandomValue(),
                    clock.UtcNow,
                    ModbotAuth.UserIdOf(http.User));

                cookies.WriteSignIn(http.Response, attempt);

                return Results.Redirect(
                    DiscordOAuth.AuthorizeUrlFor(client.ClientId, client.RedirectUrl, attempt.State, attempt.Verifier));
            })
            .WithName("ConnectDiscord")
            .WithSummary("Connect Discord")
            .WithDescription("Send the browser to Discord to sign in, to prove which Discord account is yours.")
            // A browser flow driven by a cookie and Discord's redirects, like the link page's.
            .ExcludeFromDescription()
            .Produces(StatusCodes.Status302Found);

        group.MapDelete("", async (
                [FromServices] ModbotContext db,
                [FromServices] UserAccountService accounts,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var user = await accounts.FindAsync(ModbotAuth.UserIdOf(http.User)!.Value, ct);
                if (user is null)
                    return Results.Unauthorized();

                if (user.DiscordUserId is { Length: > 0 } was)
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);

                    var proven = user.IsDiscordProven;
                    Clear(user);
                    await db.SaveChangesAsync(ct);

                    await facts.RecordAsync(
                        FactType.DiscordDisconnected,
                        user,
                        new Actor(user.Id, user.Username),
                        new JsonObject
                        {
                            ["discordUserId"] = was,
                            ["proven"] = proven,
                            ["why"] = "removed",
                        },
                        ct);

                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(SessionUser.From(user, await ChatSwitch.ReadAsync(db, ct)));
            })
            .WithName("DisconnectDiscord")
            .WithSummary("Disconnect Discord")
            .WithDescription(
                "Take the Discord account off your Modbot account. The bot stops treating that Discord "
                + "account as you, and direct messages stop.")
            .Produces<SessionUser>()
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>The account page, with what happened for it to say.</summary>
    public static string Back(string result) => $"{AccountPage}?{ResultParameter}={Uri.EscapeDataString(result)}";

    /// <summary>
    /// Finishes a Connect Discord the link page's callback received: the attempt in the sign-in
    /// cookie named this Modbot account. Returns where to send the browser.
    /// </summary>
    internal static async Task<string> FinishAsync(
        HttpContext http,
        Guid accountId,
        DiscordIdentity identity,
        ModbotContext db,
        AccountFacts facts,
        IModbotClock clock,
        CancellationToken ct)
    {
        // The browser coming back must still be signed in as the account that started this. A
        // sign-in cookie from another session, or one outliving a sign-out, proves nothing.
        if (ModbotAuth.UserIdOf(http.User) != accountId)
            return Back("signed-out");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == accountId, ct);
        if (user is null || user.IsDisabled || user.IsDeleted)
            return Back("signed-out");

        if (user.IsDiscordProven && user.DiscordUserId == identity.UserId && user.DiscordUsername == identity.Username)
            return Back("connected");

        var taken = await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id != user.Id && u.DiscordUserId == identity.UserId && u.DiscordVerifiedAt != null, ct);
        if (taken)
            return Back("taken");

        var now = clock.UtcNow;
        var actor = new Actor(user.Id, user.Username);
        var replaced = user.DiscordUserId != identity.UserId ? user.DiscordUserId : null;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Typed in on other accounts, never proven: it is this person's, so it comes off them.
        var typedElsewhere = await db.Users
            .Where(u => u.Id != user.Id && u.DiscordUserId == identity.UserId && u.DiscordVerifiedAt == null)
            .ToListAsync(ct);

        foreach (var other in typedElsewhere)
            Clear(other);

        user.DiscordUserId = identity.UserId;
        user.DiscordUsername = Fit(identity.Username);
        user.DiscordVerifiedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another account proved it between the check above and here. Nothing of this attempt
            // is kept: the typed ids taken off other accounts go back on with it.
            await transaction.RollbackAsync(ct);
            return Back("taken");
        }

        foreach (var other in typedElsewhere)
        {
            await facts.RecordAsync(
                FactType.DiscordDisconnected,
                other,
                actor,
                new JsonObject
                {
                    ["discordUserId"] = identity.UserId,
                    ["proven"] = false,
                    ["why"] = "proven-by-another-account",
                },
                ct);
        }

        await facts.RecordAsync(
            FactType.DiscordConnected,
            user,
            actor,
            new JsonObject
            {
                ["discordUserId"] = identity.UserId,
                ["discordUsername"] = identity.Username,
                ["replaced"] = replaced,
            },
            ct);

        await transaction.CommitAsync(ct);

        return Back("connected");
    }

    private static void Clear(ModbotUser user)
    {
        user.DiscordUserId = null;
        user.DiscordUsername = null;
        user.DiscordVerifiedAt = null;
    }

    /// <summary>Discord usernames are 32 characters at most; the column takes 64, in case that changes.</summary>
    private static string Fit(string username) => username.Length <= 64 ? username : username[..64];
}
