using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Auth.VRChatLink;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordLink;

/// <summary>The Discord account signed in on the link page.</summary>
public sealed record LinkPageDiscord(string UserId, string Username);

/// <summary>A VRChat code handed out and not yet found.</summary>
/// <param name="ChecksLeft">Null before Discord sign-in, when nothing has been counted yet.</param>
public sealed record LinkPagePending(string VRChatUserId, string Code, DateTimeOffset ExpiresAt, int? ChecksLeft);

/// <summary>The saved link for the signed-in Discord account.</summary>
public sealed record LinkPageLink(string VRChatUserId, string? VRChatDisplayName, DateTimeOffset LinkedAt);

/// <summary>Everything the link page shows.</summary>
/// <param name="Available">The OAuth client and the public address are set, so linking can work.</param>
/// <param name="ServerName">The Discord server's name, once the bot has read it.</param>
public sealed record LinkPageStatus(
    bool Available,
    string? ServerName,
    LinkPageDiscord? Discord,
    LinkPagePending? Pending,
    LinkPageLink? Link,
    string ProfileUrl);

/// <param name="UserIdOrUrl">A VRChat user id, or the address of the profile page. Never validated.</param>
public sealed record LinkPageVRChatRequest(string? UserIdOrUrl);

public sealed record LinkPageCheckResult(bool Linked, string Message, LinkPageStatus Status);

/// <summary>
/// The member-facing link page's API: Sign in with Discord, name a VRChat account, check the bio
/// (Discord account linking design §3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No Modbot account.</strong> Everything here is open to anyone; who is asking is the
/// Discord account in the page's own encrypted cookie, proved by the sign-in. Nothing else about the
/// deployment is shown.
/// </para>
/// <para>
/// <strong>Check needs Discord sign-in, whichever order the member took.</strong> Handing out a code
/// costs nothing, so it is allowed before sign-in and kept in the cookie; reading a VRChat profile
/// costs budget, so it waits until there is a Discord account to count it against.
/// </para>
/// <para>
/// Every link the page can be sent to is built from the public address setting, never from the
/// request (accounts and access design §4.2); the redirects below are relative to the same origin.
/// </para>
/// </remarks>
public static class DiscordLinkEndpoints
{
    public const string PagePath = "/link";

    public static IEndpointRouteBuilder MapDiscordLink(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(LinkCookies.CookiePath)
            .WithTags("Discord account link")
            // The member-facing link page's own browser flow, driven by its cookie and Discord's
            // redirects: left out of the public API reference.
            .ExcludeFromDescription()
            .AllowAnonymous();

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] LinkCookies cookies,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                var now = clock.UtcNow;
                return Results.Ok(await StatusAsync(db, cookies.ReadSession(http.Request, now), now, ct));
            })
            .WithName("GetDiscordLinkPage")
            .WithSummary("Who is signed in with Discord on the link page, and how far they are")
            .Produces<LinkPageStatus>();

        group.MapGet("/sign-in", async (
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] LinkCookies cookies,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                var client = await ClientAsync(db, protector, ct);
                if (client is null)
                    return Results.Redirect(PagePath + "?error=not-set-up");

                var attempt = new SignInAttempt(DiscordOAuth.NewRandomValue(), DiscordOAuth.NewRandomValue(), clock.UtcNow);
                cookies.WriteSignIn(http.Response, attempt);

                return Results.Redirect(
                    DiscordOAuth.AuthorizeUrlFor(client.ClientId, client.RedirectUrl, attempt.State, attempt.Verifier));
            })
            .WithName("StartDiscordSignIn")
            .WithSummary("Send the browser to Discord to sign in")
            .Produces(StatusCodes.Status302Found);

        group.MapGet("/callback", async (
                [FromQuery] string? code,
                [FromQuery] string? state,
                [FromQuery] string? error,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] LinkCookies cookies,
                [FromServices] DiscordOAuth oauth,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                var now = clock.UtcNow;

                // Taken whatever happens next: a sign-in attempt is good once.
                var attempt = cookies.TakeSignIn(http, now);

                if (!string.IsNullOrEmpty(error))
                    return Results.Redirect(PagePath + "?error=cancelled");

                if (attempt is null || string.IsNullOrEmpty(state) || string.IsNullOrEmpty(code)
                    || !LinkCookies.Same(attempt.State, state))
                {
                    return Results.Redirect(PagePath + "?error=sign-in-expired");
                }

                var client = await ClientAsync(db, protector, ct);
                if (client is null)
                    return Results.Redirect(PagePath + "?error=not-set-up");

                var signedIn = await oauth.SignInAsync(
                    client.ClientId, client.ClientSecret, client.RedirectUrl, code, attempt.Verifier, ct);

                if (signedIn.Identity is not { } identity)
                    return Results.Redirect(PagePath + "?error=discord");

                // A VRChat code handed out before sign-in moves to the database now, where its checks
                // are counted against this Discord account.
                var before = cookies.ReadSession(http.Request, now);
                if (before is { PendingVRChatUserId: { } pendingId, PendingCode: { } pendingCode, PendingExpiresAt: { } expires }
                    && now < expires)
                {
                    await SavePendingAsync(db, identity.UserId, pendingId, pendingCode, expires, LinkStartedFrom.VRChat, ct);
                }

                cookies.WriteSession(http.Response, new LinkSession(now, identity.UserId, identity.Username));
                return Results.Redirect(PagePath);
            })
            .WithName("FinishDiscordSignIn")
            .WithSummary("Where Discord sends the browser back after sign-in")
            .WithDescription(
                "Checks the state value against the page's sign-in cookie, exchanges the code with the "
                + "PKCE verifier, reads users/@me once and revokes the token. Always redirects to /link.")
            .Produces(StatusCodes.Status302Found);

        group.MapPost("/vrchat", async (
                [FromBody] LinkPageVRChatRequest body,
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] LinkCookies cookies,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (await ClientAsync(db, protector, ct) is null)
                    return Results.BadRequest(new { error = "Account linking is not set up." });

                var vrchatUserId = VRChatLinkEndpoints.ParseUserIdOrUrl(body.UserIdOrUrl);
                if (vrchatUserId is null)
                    return Results.BadRequest(new { error = "Enter your VRChat profile link or user id." });

                var now = clock.UtcNow;
                var code = VRChatLinkEndpoints.NewCode();
                var expires = now + VRChatLinkEndpoints.CodeLifetime;
                var session = cookies.ReadSession(http.Request, now);

                if (session is { SignedIn: true })
                {
                    await SavePendingAsync(db, session.DiscordUserId!, vrchatUserId, code, expires, LinkStartedFrom.Discord, ct);
                }
                else
                {
                    session = new LinkSession(now, PendingVRChatUserId: vrchatUserId, PendingCode: code, PendingExpiresAt: expires);
                    cookies.WriteSession(http.Response, session);
                }

                return Results.Ok(await StatusAsync(db, session, now, ct));
            })
            .WithName("StartDiscordLinkVRChat")
            .WithSummary("Name a VRChat account and get the code to put in its bio")
            .WithDescription("Allowed before Discord sign-in. The code lasts 30 minutes; naming an account again replaces it.")
            .Produces<LinkPageStatus>()
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/check", async (
                [FromServices] ModbotContext db,
                [FromServices] ISecretProtector protector,
                [FromServices] LinkCookies cookies,
                [FromServices] LinkCheckLimit limit,
                [FromServices] VRChatBioCheck bioCheck,
                [FromServices] DiscordAccountLinks links,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                var now = clock.UtcNow;
                var session = cookies.ReadSession(http.Request, now);

                if (session is not { SignedIn: true })
                    return Results.Json(new { error = "Sign in with Discord first." }, statusCode: StatusCodes.Status401Unauthorized);

                if (await ClientAsync(db, protector, ct) is null)
                    return Results.BadRequest(new { error = "Account linking is not set up." });

                var pending = await db.DiscordLinkCodes.FirstOrDefaultAsync(c => c.DiscordUserId == session.DiscordUserId, ct);

                if (pending is null)
                    return Results.BadRequest(new { error = "Enter your VRChat profile link first." });

                if (now >= pending.ExpiresAt)
                    return Results.BadRequest(new { error = "That code has expired." });

                if (pending.Checks >= VRChatLinkEndpoints.MaxChecksPerCode)
                    return Results.BadRequest(new { error = "That code has been checked too many times." });

                if (pending.LastCheckAt is { } last && now - last < VRChatLinkEndpoints.MinimumGapBetweenChecks)
                {
                    var wait = (int)Math.Ceiling((VRChatLinkEndpoints.MinimumGapBetweenChecks - (now - last)).TotalSeconds);
                    return Results.Json(
                        new { error = $"Wait {wait} more second{(wait == 1 ? "" : "s")} before checking again." },
                        statusCode: StatusCodes.Status429TooManyRequests);
                }

                if (!limit.TryTake(now))
                {
                    return Results.Json(
                        new { error = "Too many checks right now. Try again in a minute." },
                        statusCode: StatusCodes.Status429TooManyRequests);
                }

                // Counted before the call, so a call that fails still cost a check.
                pending.Checks++;
                pending.LastCheckAt = now;
                await db.SaveChangesAsync(ct);

                var check = await bioCheck.CheckAsync(pending.VRChatUserId, pending.Code, ct);

                if (!check.Read)
                    return Results.Ok(new LinkPageCheckResult(false, check.Problem!, await StatusAsync(db, session, now, ct)));

                if (!check.CodeFound)
                {
                    return Results.Ok(new LinkPageCheckResult(
                        false, $"{pending.Code} is not in that account's bio yet.", await StatusAsync(db, session, now, ct)));
                }

                var saved = await links.LinkAsync(
                    new DiscordIdentity(session.DiscordUserId!, session.DiscordUsername ?? session.DiscordUserId!),
                    pending.VRChatUserId,
                    check.Profile?.DisplayName,
                    pending.StartedFrom,
                    ct);

                if (!saved.Saved)
                    return Results.Ok(new LinkPageCheckResult(false, saved.Refusal!, await StatusAsync(db, session, now, ct)));

                db.DiscordLinkCodes.Remove(pending);
                await db.SaveChangesAsync(ct);

                return Results.Ok(new LinkPageCheckResult(
                    true,
                    $"Linked to {check.Profile?.DisplayName ?? pending.VRChatUserId}.",
                    await StatusAsync(db, session, now, ct)));
            })
            .WithName("CheckDiscordLinkBio")
            .WithSummary("Read the VRChat bio and save the link if the code is there")
            .WithDescription(
                "Needs Discord sign-in. One profile fetch through the gate on users.read. Six checks per "
                + "code, one every ten seconds per Discord account, thirty a minute across the deployment.")
            .Produces<LinkPageCheckResult>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests);

        group.MapPost("/unlink", async (
                [FromServices] ModbotContext db,
                [FromServices] LinkCookies cookies,
                [FromServices] DiscordAccountLinks links,
                [FromServices] IModbotClock clock,
                HttpContext http,
                CancellationToken ct) =>
            {
                var now = clock.UtcNow;
                var session = cookies.ReadSession(http.Request, now);

                if (session is not { SignedIn: true })
                    return Results.Json(new { error = "Sign in with Discord first." }, statusCode: StatusCodes.Status401Unauthorized);

                if (await links.ActiveForDiscordAsync(session.DiscordUserId!, ct) is { } link)
                    await links.UnlinkAsync(link, moderator: null, ct);

                return Results.Ok(await StatusAsync(db, session, now, ct));
            })
            .WithName("UnlinkOwnDiscordLink")
            .WithSummary("End the signed-in Discord account's link. History is kept.")
            .Produces<LinkPageStatus>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/sign-out", (
                [FromServices] LinkCookies cookies,
                HttpContext http) =>
            {
                LinkCookies.DeleteSession(http.Response);
                return Results.NoContent();
            })
            .WithName("SignOutOfDiscordLinkPage")
            .WithSummary("Forget the Discord sign-in on the link page")
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    /// <summary>The OAuth client, when linking is set up.</summary>
    internal sealed record OAuthClient(string ClientId, string ClientSecret, string RedirectUrl);

    internal static async Task<OAuthClient?> ClientAsync(ModbotContext db, ISecretProtector protector, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordOAuthClientId, s.DiscordOAuthClientSecretEncrypted, s.PublicAddress })
            .FirstOrDefaultAsync(ct);

        var redirect = DiscordInvite.RedirectUrlFor(settings?.PublicAddress);
        var secret = protector.Unprotect(settings?.DiscordOAuthClientSecretEncrypted);

        if (redirect is null || string.IsNullOrWhiteSpace(settings?.DiscordOAuthClientId) || string.IsNullOrEmpty(secret))
            return null;

        return new OAuthClient(settings.DiscordOAuthClientId.Trim(), secret, redirect);
    }

    private static async Task SavePendingAsync(
        ModbotContext db,
        string discordUserId,
        string vrchatUserId,
        string code,
        DateTimeOffset expiresAt,
        string startedFrom,
        CancellationToken ct)
    {
        var row = await db.DiscordLinkCodes.FirstOrDefaultAsync(c => c.DiscordUserId == discordUserId, ct);

        if (row is null)
        {
            row = new DiscordLinkCode { DiscordUserId = discordUserId };
            db.DiscordLinkCodes.Add(row);
        }

        // A new code starts a new count. The gap since the last check is kept, so naming an account
        // again is not a way round the ten seconds.
        if (row.Code != code)
            row.Checks = 0;

        row.VRChatUserId = vrchatUserId;
        row.Code = code;
        row.ExpiresAt = expiresAt;
        row.StartedFrom = startedFrom;

        await db.SaveChangesAsync(ct);
    }

    private static async Task<LinkPageStatus> StatusAsync(ModbotContext db, LinkSession? session, DateTimeOffset now, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordOAuthClientId, s.DiscordOAuthClientSecretEncrypted, s.PublicAddress, s.DiscordGuildId })
            .FirstOrDefaultAsync(ct);

        var available = settings is not null
                        && !string.IsNullOrWhiteSpace(settings.DiscordOAuthClientId)
                        && settings.DiscordOAuthClientSecretEncrypted is not null
                        && DiscordInvite.RedirectUrlFor(settings.PublicAddress) is not null;

        var serverName = settings?.DiscordGuildId is { } guildId
            ? await db.DiscordServers.AsNoTracking().Where(s => s.GuildId == guildId).Select(s => s.Name).FirstOrDefaultAsync(ct)
            : null;

        LinkPageDiscord? discord = null;
        LinkPagePending? pending = null;
        LinkPageLink? link = null;

        if (session is { SignedIn: true })
        {
            discord = new LinkPageDiscord(session.DiscordUserId!, session.DiscordUsername ?? session.DiscordUserId!);

            var code = await db.DiscordLinkCodes.AsNoTracking()
                .FirstOrDefaultAsync(c => c.DiscordUserId == session.DiscordUserId, ct);

            if (code is not null && now < code.ExpiresAt)
            {
                pending = new LinkPagePending(
                    code.VRChatUserId, code.Code, code.ExpiresAt,
                    Math.Max(0, VRChatLinkEndpoints.MaxChecksPerCode - code.Checks));
            }

            link = await db.DiscordAccountLinks.AsNoTracking()
                .Where(l => l.DiscordUserId == session.DiscordUserId && l.UnlinkedAt == null)
                .Select(l => new LinkPageLink(l.VRChatUserId, l.VRChatDisplayName, l.LinkedAt))
                .FirstOrDefaultAsync(ct);
        }
        else if (session is { PendingVRChatUserId: { } id, PendingCode: { } pendingCode, PendingExpiresAt: { } expires }
                 && now < expires)
        {
            pending = new LinkPagePending(id, pendingCode, expires, null);
        }

        return new LinkPageStatus(available, serverName, discord, pending, link, VRChatLinkEndpoints.ProfileUrl);
    }
}
