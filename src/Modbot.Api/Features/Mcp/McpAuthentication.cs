using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Mcp;

/// <summary>
/// The door onto <c>/mcp</c>: an API key, or an access token from the MCP server's own sign-in
/// (MCP server design).
/// </summary>
/// <remarks>
/// <para>
/// Its own scheme rather than a branch in the default forwarder, so that the MCP endpoint alone
/// answers a refusal the way the MCP specification wants -- a 401 whose <c>WWW-Authenticate</c>
/// names the resource metadata -- and the rest of the API keeps answering a plain
/// <c>Bearer</c>. The endpoint names this scheme in its authorization metadata; nothing else
/// uses it.
/// </para>
/// <para>
/// Both kinds of credential end in <strong>the same principal a session has</strong>, built from
/// the account read again now, so <c>RequiresFlag</c> and every tool's own permission check work
/// unchanged and a revoked token, a demoted account or a disabled one stops on its next request.
/// </para>
/// </remarks>
public static class McpAuthentication
{
    public const string Scheme = "Mcp";

    /// <summary>The connection a principal authenticated with. Absent on an API key.</summary>
    public const string GrantIdClaim = "modbot:mcp_grant_id";

    public static Guid? GrantIdOf(ClaimsPrincipal? principal)
        => Guid.TryParse(principal?.FindFirst(GrantIdClaim)?.Value, out var id) ? id : null;

    /// <summary>The principal for a connection: a session's claims for the person, plus the connection's id.</summary>
    public static ClaimsPrincipal CreatePrincipal(ApiCaller caller, Guid grantId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var principal = ModbotAuth.CreatePrincipal(caller.UserId, caller.Username, caller.Permissions, vrchatLinked: true, now);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(GrantIdClaim, grantId.ToString()));
        return principal;
    }
}

internal sealed class McpAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public McpAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ApiKeyAuthentication.BearerOf(Request);
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var services = Context.RequestServices;
        var clock = services.GetRequiredService<IModbotClock>();
        var callers = services.GetRequiredService<ApiCallers>();

        // One sentence for every refusal, as for keys: whether a token exists, expired or was
        // revoked is not something to tell whoever is holding it.
        if (ApiKeySecrets.LooksLikeKey(token))
        {
            var caller = await callers.ForKeyAsync(token, Context.RequestAborted);
            if (caller is null)
                return AuthenticateResult.Fail("The token is not valid.");

            return AuthenticateResult.Success(new AuthenticationTicket(
                ApiKeyAuthentication.CreatePrincipal(caller, clock.UtcNow), Scheme.Name));
        }

        if (!McpSecrets.LooksLikeAccessToken(token))
            return AuthenticateResult.Fail("The token is not valid.");

        var db = services.GetRequiredService<ModbotContext>();
        var hash = McpSecrets.Hash(token);
        var now = clock.UtcNow;

        var grant = await db.McpGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.AccessTokenHash == hash, Context.RequestAborted);

        if (grant is null || !grant.IsOpen(now) || now >= grant.AccessExpiresAt)
            return AuthenticateResult.Fail("The token is not valid.");

        var person = await callers.ForUserAsync(grant.UserId, Context.RequestAborted);
        if (person is null)
            return AuthenticateResult.Fail("The token is not valid.");

        // Written at most once a minute, like a key's last-used time.
        if (grant.LastUsedAt is null || now - grant.LastUsedAt.Value >= ApiCallers.LastUsedEvery)
        {
            await db.McpGrants
                .Where(g => g.Id == grant.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(g => g.LastUsedAt, now), Context.RequestAborted);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(
            McpAuthentication.CreatePrincipal(person, grant.Id, now), Scheme.Name));
    }

    /// <summary>
    /// The 401 the MCP specification asks for: <c>WWW-Authenticate</c> naming where the resource
    /// metadata is, which is how an AI app finds the sign-in without anyone typing anything.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var db = Context.RequestServices.GetRequiredService<ModbotContext>();
        var address = await McpAddress.ForAsync(Context, db, Context.RequestAborted);

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{address.ResourceMetadataUrl}\"";
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.Headers.WWWAuthenticate = "Bearer error=\"insufficient_scope\"";
        return Task.CompletedTask;
    }
}
