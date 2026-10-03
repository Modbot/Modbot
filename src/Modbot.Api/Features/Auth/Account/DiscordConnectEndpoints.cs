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
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Core.Users;

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
/// <strong>Or a code and a command.</strong> Sign-in needs the account linking card's client id
/// and secret, which many servers never set up. The same card hands out a code instead
/// (<see cref="StaffDiscordCodes"/>), and <c>/verify</c> with it in the Discord server proves the
/// Discord account that ran it (Discord account linking design §14). Both go through
/// <see cref="StaffDiscordProof"/>, so the rules above are the same for each.
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

        group.MapGet("/code", async (
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] IModbotClock clock,
                [FromServices] DemoMode? demo,
                HttpContext http,
                CancellationToken ct) =>
            {
                var live = await StaffDiscordCodes.LiveAsync(db, ModbotAuth.UserIdOf(http.User)!.Value, clock.UtcNow, ct);
                return Results.Ok(await CodeStatusAsync(db, protector, demo, live, ct));
            })
            .WithName("GetDiscordCode")
            .WithSummary("Get your /verify code")
            .WithDescription(
                "The code you can run /verify with in the Discord server to connect your Discord account, "
                + "while one is live, and which ways of connecting this server offers.")
            .Produces<DiscordCodeStatus>();

        group.MapPost("/code", async (
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] IModbotClock clock,
                [FromServices] DemoMode? demo,
                HttpContext http,
                CancellationToken ct) =>
            {
                if (!await CommandSetUpAsync(db, demo, ct))
                    return Results.BadRequest(new { error = "The Discord bot is not set up on this server." });

                var code = await StaffDiscordCodes.IssueAsync(db, ModbotAuth.UserIdOf(http.User)!.Value, clock.UtcNow, ct);
                return Results.Ok(await CodeStatusAsync(db, protector, demo, code, ct));
            })
            .WithName("NewDiscordCode")
            .WithSummary("Get a new /verify code")
            .WithDescription(
                "Make a code to run as /verify in the Discord server, which connects the Discord account "
                + "that runs it to your Modbot account. It works once, for fifteen minutes, and replaces "
                + "any code you had.")
            .Produces<DiscordCodeStatus>()
            .Produces(StatusCodes.Status400BadRequest);

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
                    StaffDiscordProof.Clear(user);
                    await db.SaveChangesAsync(ct);
                    await StaffDiscordProof.ForgetAgreementsAsync(db, [user.Id], ct);

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

    /// <summary>The code and the ways of connecting, for the account page.</summary>
    private static async Task<DiscordCodeStatus> CodeStatusAsync(
        ModbotContext db, ISecretProtector protector, DemoMode? demo, StaffDiscordCode? code, CancellationToken ct)
        => new(
            code is null ? null : StaffDiscordCodes.Show(code.Code),
            code?.ExpiresAt,
            await DiscordLinkEndpoints.ClientAsync(db, protector, ct) is not null,
            await CommandSetUpAsync(db, demo, ct));

    /// <summary>
    /// Whether <c>/verify</c> can be answered: a bot token and a server are saved, and this is not a
    /// demo, which never runs the bot.
    /// </summary>
    private static async Task<bool> CommandSetUpAsync(ModbotContext db, DemoMode? demo, CancellationToken ct)
    {
        if (DemoAuthentication.MayServeEveryoneAsAdministrator(demo))
            return false;

        var bot = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordBotTokenEncrypted, s.DiscordGuildId })
            .FirstOrDefaultAsync(ct);

        return bot is { DiscordBotTokenEncrypted: not null } && !string.IsNullOrWhiteSpace(bot.DiscordGuildId);
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

        var actor = new Actor(user.Id, user.Username);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var proof = await StaffDiscordProof.ProveAsync(db, user, identity.UserId, identity.Username, clock.UtcNow, ct);

        switch (proof.Outcome)
        {
            case StaffDiscordProofOutcome.AlreadyProven:
                return Back("connected");

            case StaffDiscordProofOutcome.Taken:
                // Another account proved it first. Nothing of this attempt is kept: the typed ids
                // taken off other accounts go back on with it.
                await transaction.RollbackAsync(ct);
                return Back("taken");
        }

        foreach (var other in proof.TypedElsewhere)
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
                ["replaced"] = proof.Replaced,
            },
            ct);

        await transaction.CommitAsync(ct);

        return Back("connected");
    }
}

/// <summary>The account page's Discord card: the <c>/verify</c> code, and which ways of connecting work here.</summary>
/// <param name="Code">The live code as people read it, <c>K7P-42Q</c>; null when there is none.</param>
/// <param name="ExpiresAt">When the code stops working.</param>
/// <param name="SignInSetUp">Connect Discord works: the account linking card's client id and secret, and a public address, are saved.</param>
/// <param name="CommandSetUp"><c>/verify</c> can be answered: the bot is set up.</param>
public sealed record DiscordCodeStatus(
    string? Code,
    DateTimeOffset? ExpiresAt,
    bool SignInSetUp,
    bool CommandSetUp);
