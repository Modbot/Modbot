using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Chat;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Mcp;

/// <summary>What an AI app sends to register itself (RFC 7591). Field names are the RFC's.</summary>
public sealed record McpClientRegistration(
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris,
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("client_uri")] string? ClientUri,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod,
    [property: JsonPropertyName("grant_types")] IReadOnlyList<string>? GrantTypes,
    [property: JsonPropertyName("response_types")] IReadOnlyList<string>? ResponseTypes,
    [property: JsonPropertyName("scope")] string? Scope);

/// <summary>What the sign-in page needs to show, for one sign-in an AI app started.</summary>
/// <param name="Tools">The tools the person will be able to use through the app: the ones they are offered.</param>
public sealed record McpAuthorizeView(
    string ClientName,
    string? ClientUri,
    string RedirectHost,
    IReadOnlyList<McpAuthorizeTool> Tools);

public sealed record McpAuthorizeTool(string Name, string Label, IReadOnlyList<string> Needs);

/// <summary>The person's answer on the sign-in page, with the request it answers.</summary>
public sealed record McpAuthorizeDecision(
    string ClientId,
    string? RedirectUri,
    string? State,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? Scope,
    string? Resource,
    bool Approve);

/// <param name="RedirectTo">Where the browser goes next: back to the app, with a code or a refusal.</param>
public sealed record McpAuthorizeOutcome(string RedirectTo);

/// <summary>
/// Modbot as the sign-in for its own MCP server (MCP server design): the two metadata documents,
/// registration, authorize, token and revoke.
/// </summary>
/// <remarks>
/// <para>
/// Built for exactly one purpose -- an AI app asking to act as a Modbot moderator on
/// <c>/mcp</c> -- and no wider. There is one scope. The apps that matter (Claude.ai, ChatGPT,
/// Claude Code, Cursor) read the metadata, register themselves, send the person to
/// <c>/mcp/authorize</c>, and trade the code for tokens with PKCE, so a person types nothing but
/// the address.
/// </para>
/// <para>
/// The sign-in page itself is the web app's <c>/connect</c> page. <c>/mcp/authorize</c> checks
/// the request and sends the browser there; the page asks <c>GET /api/mcp/authorize</c> what to
/// show and answers with <c>POST /api/mcp/authorize</c>, which makes the code and says where to go.
/// </para>
/// <para>
/// Everything under <c>/mcp</c> and <c>/.well-known/oauth-*</c> is left out of the OpenAPI
/// document: their shapes are the RFCs', and the docs page describes them.
/// </para>
/// </remarks>
public static class McpOAuthEndpoints
{
    public const int MaxRedirectUris = 10;
    public const int MaxClientNameLength = 128;

    private static readonly string[] GrantTypes = ["authorization_code", "refresh_token"];
    private static readonly string[] ResponseTypes = ["code"];
    private static readonly string[] AuthMethods = ["none", "client_secret_basic", "client_secret_post"];

    public static IEndpointRouteBuilder MapMcpOAuth(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // ── Metadata ───────────────────────────────────────────────────────────────────────

        app.MapGet("/.well-known/oauth-authorization-server", ServerMetadataAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet("/.well-known/oauth-protected-resource", ResourceMetadataAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet("/.well-known/oauth-protected-resource/mcp", ResourceMetadataAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        // ── The flow ───────────────────────────────────────────────────────────────────────

        app.MapPost("/mcp/register", RegisterAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet("/mcp/authorize", AuthorizeAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapPost("/mcp/token", TokenAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapPost("/mcp/revoke", RevokeAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();

        // ── The sign-in page's two calls ───────────────────────────────────────────────────

        var page = app.MapGroup("/api/mcp/authorize")
            .WithTags("MCP")
            .RequiresFlag(ModbotPermissions.UseAiChat);

        page.MapGet("", DescribeAsync)
            .WithName("DescribeMcpSignIn")
            .WithSummary("What an AI app is asking for, for the sign-in page")
            .Produces<McpAuthorizeView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        page.MapPost("", DecideAsync)
            .WithName("AnswerMcpSignIn")
            .WithSummary("Allow or refuse an AI app's sign-in")
            .Produces<McpAuthorizeOutcome>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    // ── Metadata ───────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> ServerMetadataAsync(HttpContext http, [FromServices] ModbotContext db, CancellationToken ct)
    {
        var address = await McpAddress.ForAsync(http, db, ct);

        return Results.Json(new
        {
            issuer = address.Origin,
            authorization_endpoint = address.AuthorizeUrl,
            token_endpoint = address.TokenUrl,
            registration_endpoint = address.RegisterUrl,
            revocation_endpoint = address.RevokeUrl,
            response_types_supported = ResponseTypes,
            grant_types_supported = GrantTypes,
            code_challenge_methods_supported = new[] { "S256" },
            client_id_metadata_document_supported = true,
            token_endpoint_auth_methods_supported = AuthMethods,
            revocation_endpoint_auth_methods_supported = AuthMethods,
            scopes_supported = new[] { McpSecrets.Scope },
        });
    }

    private static async Task<IResult> ResourceMetadataAsync(HttpContext http, [FromServices] ModbotContext db, CancellationToken ct)
    {
        var address = await McpAddress.ForAsync(http, db, ct);

        return Results.Json(new
        {
            resource = address.ServerUrl,
            authorization_servers = new[] { address.Origin },
            scopes_supported = new[] { McpSecrets.Scope },
            bearer_methods_supported = new[] { "header" },
            resource_name = "Modbot",
        });
    }

    // ── Registration ───────────────────────────────────────────────────────────────────────

    private static async Task<IResult> RegisterAsync(
        HttpContext http,
        [FromBody] McpClientRegistration body,
        [FromServices] ModbotContext db,
        [FromServices] IModbotClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var now = clock.UtcNow;

        var uris = (body.RedirectUris ?? []).Select(u => u?.Trim() ?? string.Empty).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (uris.Count == 0)
            return OAuthError("invalid_redirect_uri", "redirect_uris is required.");
        if (uris.Count > MaxRedirectUris)
            return OAuthError("invalid_redirect_uri", $"At most {MaxRedirectUris} redirect URIs.");

        foreach (var uri in uris)
        {
            if (RedirectUriProblem(uri) is { } problem)
                return OAuthError("invalid_redirect_uri", problem);
        }

        var method = string.IsNullOrWhiteSpace(body.TokenEndpointAuthMethod) ? "client_secret_basic" : body.TokenEndpointAuthMethod.Trim();
        if (!AuthMethods.Contains(method, StringComparer.Ordinal))
            return OAuthError("invalid_client_metadata", "token_endpoint_auth_method must be none, client_secret_basic or client_secret_post.");

        if (body.GrantTypes is { Count: > 0 } grants && grants.Any(g => !GrantTypes.Contains(g, StringComparer.Ordinal)))
            return OAuthError("invalid_client_metadata", "Only authorization_code and refresh_token are supported.");

        if (body.ResponseTypes is { Count: > 0 } responses && responses.Any(r => !ResponseTypes.Contains(r, StringComparer.Ordinal)))
            return OAuthError("invalid_client_metadata", "Only the code response type is supported.");

        var name = (body.ClientName ?? string.Empty).Trim();
        if (name.Length == 0)
            name = Uri.TryCreate(uris[0], UriKind.Absolute, out var first) && first.Host.Length > 0 ? first.Host : "AI app";
        if (name.Length > MaxClientNameLength)
            name = name[..MaxClientNameLength];

        var clientUri = Uri.TryCreate(body.ClientUri?.Trim(), UriKind.Absolute, out var site) && site.Scheme == Uri.UriSchemeHttps
            ? site.AbsoluteUri
            : null;

        // Anyone can register, so registrations nobody signed in through must not pile up.
        var stale = now - McpSecrets.UnusedClientLife;
        await db.McpClients
            .Where(c => c.CreatedAt < stale && !db.McpGrants.Any(g => g.ClientId == c.Id))
            .ExecuteDeleteAsync(ct);
        await db.McpAuthorizationCodes
            .Where(c => c.ExpiresAt < stale)
            .ExecuteDeleteAsync(ct);

        var secret = method == "none" ? null : McpSecrets.NewClientSecret();

        var client = new McpClient
        {
            Name = name,
            SecretHash = secret is null ? null : McpSecrets.Hash(secret),
            RedirectUris = JsonSerializer.Serialize(uris),
            ClientUri = clientUri,
            CreatedAt = now,
        };

        db.McpClients.Add(client);
        await db.SaveChangesAsync(ct);

        var answer = new JsonObject
        {
            ["client_id"] = client.Id.ToString(),
            ["client_id_issued_at"] = now.ToUnixTimeSeconds(),
            ["client_name"] = client.Name,
            ["redirect_uris"] = new JsonArray([.. uris.Select(u => JsonValue.Create(u))]),
            ["token_endpoint_auth_method"] = method,
            ["grant_types"] = new JsonArray([.. GrantTypes.Select(g => JsonValue.Create(g))]),
            ["response_types"] = new JsonArray([.. ResponseTypes.Select(r => JsonValue.Create(r))]),
            ["scope"] = McpSecrets.Scope,
        };

        if (clientUri is not null)
            answer["client_uri"] = clientUri;

        if (secret is not null)
        {
            answer["client_secret"] = secret;
            answer["client_secret_expires_at"] = 0;
        }

        return Results.Json(answer, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// Why an address may not be sent a code, or null. Web addresses must be https; a loopback
    /// address may be plain http, which is how a local tool such as Claude Code or Cursor
    /// listens for its code; an app's own scheme (<c>cursor://</c>) is allowed too.
    /// </summary>
    public static string? RedirectUriProblem(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return $"'{uri}' is not an absolute URI.";

        if (!string.IsNullOrEmpty(parsed.Fragment))
            return "A redirect URI cannot have a fragment.";

        if (parsed.Scheme == Uri.UriSchemeHttps)
            return null;

        if (parsed.Scheme == Uri.UriSchemeHttp)
            return parsed.IsLoopback ? null : "An http redirect URI must be a loopback address.";

        return parsed.Scheme is "javascript" or "data" or "file" or "vbscript"
            ? $"'{parsed.Scheme}' is not allowed as a redirect scheme."
            : null;
    }

    // ── Authorize ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where the app sends the browser. Checks what can be checked before anyone signs in, then
    /// sends the browser on to the web app's sign-in page with the same query.
    /// </summary>
    /// <remarks>
    /// A bad client or a redirect address the client never registered is answered here, in
    /// words, and never by redirecting: sending a code to an address an attacker chose is the
    /// one mistake an authorization endpoint must not make (RFC 6749 §4.1.2.1).
    /// </remarks>
    private static async Task<IResult> AuthorizeAsync(HttpContext http, [FromServices] ModbotContext db, CancellationToken ct)
    {
        var query = http.Request.Query;
        var check = await CheckRequestAsync(
            db, http,
            query["client_id"], query["redirect_uri"], query["response_type"],
            query["code_challenge"], query["code_challenge_method"], query["resource"], ct);

        if (check.Fatal is not null)
            return Results.Text(check.Fatal, statusCode: StatusCodes.Status400BadRequest);

        if (check.Error is not null)
            return Results.Redirect(Back(check.RedirectUri!, query["state"], error: check.Error, description: check.ErrorDescription));

        return Results.Redirect("/connect" + http.Request.QueryString);
    }

    private static async Task<IResult> DescribeAsync(
        HttpContext http,
        [FromServices] ModbotContext db,
        [FromServices] ChatToolRegistry registry,
        CancellationToken ct)
    {
        var query = http.Request.Query;
        var check = await CheckRequestAsync(
            db, http,
            query["client_id"], query["redirect_uri"], query["response_type"],
            query["code_challenge"], query["code_challenge_method"], query["resource"], ct);

        if (check.Fatal is not null)
            return Results.BadRequest(new { error = check.Fatal });
        if (check.Error is not null)
            return Results.BadRequest(new { error = check.ErrorDescription ?? check.Error });

        var switches = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.AiChatToolSwitches).FirstOrDefaultAsync(ct);
        var offered = registry.OfferedTo(ModbotAuth.PermissionsOf(http.User), ChatToolRegistry.ParseSwitches(switches));

        return Results.Ok(new McpAuthorizeView(
            check.Client!.Name,
            check.Client.ClientUri,
            new Uri(check.RedirectUri!).Host is { Length: > 0 } host ? host : check.RedirectUri!,
            [.. offered.Select(t => new McpAuthorizeTool(t.Name, t.Label, NeedsOf(t.Needs)))]));
    }

    private static async Task<IResult> DecideAsync(
        HttpContext http,
        [FromBody] McpAuthorizeDecision body,
        [FromServices] ModbotContext db,
        [FromServices] IModbotClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var check = await CheckRequestAsync(
            db, http,
            body.ClientId, body.RedirectUri, "code", body.CodeChallenge, body.CodeChallengeMethod, body.Resource, ct);

        if (check.Fatal is not null)
            return Results.BadRequest(new { error = check.Fatal });
        if (check.Error is not null)
            return Results.BadRequest(new { error = check.ErrorDescription ?? check.Error });

        if (!body.Approve)
            return Results.Ok(new McpAuthorizeOutcome(Back(check.RedirectUri!, body.State, error: "access_denied", description: null)));

        var now = clock.UtcNow;
        var code = McpSecrets.NewCode();

        db.McpAuthorizationCodes.Add(new McpAuthorizationCode
        {
            CodeHash = McpSecrets.Hash(code),
            ClientId = check.Client!.Id,
            UserId = ModbotAuth.UserIdOf(http.User)!.Value,
            RedirectUri = check.RedirectUri!,
            CodeChallenge = body.CodeChallenge!,
            Resource = check.Resource,
            Scope = McpSecrets.Scope,
            ExpiresAt = now + McpSecrets.CodeLife,
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new McpAuthorizeOutcome(Back(check.RedirectUri!, body.State, code: code)));
    }

    private sealed record RequestCheck(
        McpClient? Client,
        string? RedirectUri,
        string? Resource,
        string? Fatal,
        string? Error,
        string? ErrorDescription);

    /// <summary>
    /// Checks an authorization request. <c>Fatal</c> is a problem with the client or the
    /// redirect address, which is answered in place; <c>Error</c> is anything else, which is
    /// sent back to the app at its address.
    /// </summary>
    private static async Task<RequestCheck> CheckRequestAsync(
        ModbotContext db,
        HttpContext http,
        string? clientId,
        string? redirectUri,
        string? responseType,
        string? codeChallenge,
        string? codeChallengeMethod,
        string? resource,
        CancellationToken ct)
    {
        var client = await McpClientDocuments.ResolveAsync(
            clientId, db, http.RequestServices.GetRequiredService<IHttpClientFactory>(), http.RequestServices.GetRequiredService<IModbotClock>(), ct);
        if (client is null)
            return new RequestCheck(null, null, null, "Unknown client.", null, null);

        var registered = RedirectUrisOf(client);
        redirectUri = redirectUri?.Trim();

        if (string.IsNullOrEmpty(redirectUri))
        {
            if (registered.Count != 1)
                return new RequestCheck(client, null, null, "redirect_uri is required.", null, null);

            redirectUri = registered[0];
        }
        else if (!registered.Contains(redirectUri, StringComparer.Ordinal))
        {
            return new RequestCheck(client, null, null, "That redirect_uri is not registered for this client.", null, null);
        }

        if (responseType != "code")
            return new RequestCheck(client, redirectUri, null, null, "unsupported_response_type", "response_type must be code.");

        if (string.IsNullOrEmpty(codeChallenge) || codeChallenge.Length is < 43 or > 128)
            return new RequestCheck(client, redirectUri, null, null, "invalid_request", "code_challenge is required.");

        if (codeChallengeMethod != "S256")
            return new RequestCheck(client, redirectUri, null, null, "invalid_request", "code_challenge_method must be S256.");

        if (!string.IsNullOrEmpty(resource))
        {
            var address = await McpAddress.ForAsync(http, db, ct);
            if (!SameResource(resource, address.ServerUrl))
                return new RequestCheck(client, redirectUri, null, null, "invalid_target", "resource must be this Modbot's MCP server address.");
        }

        return new RequestCheck(client, redirectUri, string.IsNullOrEmpty(resource) ? null : resource, null, null, null);
    }

    /// <summary>The same server, allowing for a trailing slash.</summary>
    private static bool SameResource(string given, string ours)
        => string.Equals(given.TrimEnd('/'), ours.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string Back(string redirectUri, string? state, string? code = null, string? error = null, string? description = null)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (code is not null)
            parameters["code"] = code;
        if (error is not null)
            parameters["error"] = error;
        if (description is not null)
            parameters["error_description"] = description;
        if (!string.IsNullOrEmpty(state))
            parameters["state"] = state;

        return QueryHelpers.AddQueryString(redirectUri, parameters);
    }

    // ── Token ──────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> TokenAsync(
        HttpContext http,
        [FromServices] ModbotContext db,
        [FromServices] AccountFacts facts,
        [FromServices] IModbotClock clock,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (!http.Request.HasFormContentType)
            return OAuthError("invalid_request", "Send the request as application/x-www-form-urlencoded.");

        var form = await http.Request.ReadFormAsync(ct);
        var (client, clientError) = await ClientOfAsync(http, form, db, ct);
        if (client is null)
            return clientError!;

        var now = clock.UtcNow;

        switch (form["grant_type"].ToString())
        {
            case "authorization_code":
            {
                var codeHash = McpSecrets.Hash(form["code"].ToString());
                var code = await db.McpAuthorizationCodes.FirstOrDefaultAsync(c => c.CodeHash == codeHash, ct);

                if (code is null || code.UsedAt is not null || now >= code.ExpiresAt || code.ClientId != client.Id)
                    return OAuthError("invalid_grant", "The code is not valid.");

                var redirect = form["redirect_uri"].ToString();
                if (!string.Equals(redirect, code.RedirectUri, StringComparison.Ordinal))
                    return OAuthError("invalid_grant", "redirect_uri does not match.");

                if (!McpSecrets.VerifierMatches(form["code_verifier"].ToString(), code.CodeChallenge))
                    return OAuthError("invalid_grant", "code_verifier does not match.");

                if (form["resource"].ToString() is { Length: > 0 } resource)
                {
                    var address = await McpAddress.ForAsync(http, db, ct);
                    if (!SameResource(resource, address.ServerUrl))
                        return OAuthError("invalid_target", "resource must be this Modbot's MCP server address.");
                }

                var access = McpSecrets.NewAccessToken();
                var refresh = McpSecrets.NewRefreshToken();

                var grant = new McpGrant
                {
                    ClientId = client.Id,
                    UserId = code.UserId,
                    Scope = code.Scope,
                    AccessTokenHash = McpSecrets.Hash(access),
                    AccessExpiresAt = now + McpSecrets.AccessTokenLife,
                    RefreshTokenHash = McpSecrets.Hash(refresh),
                    RefreshExpiresAt = now + McpSecrets.RefreshTokenLife,
                    CreatedAt = now,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                code.UsedAt = now;
                db.McpGrants.Add(grant);
                await db.SaveChangesAsync(ct);

                var username = await db.Users.AsNoTracking().Where(u => u.Id == code.UserId).Select(u => u.Username).FirstOrDefaultAsync(ct) ?? string.Empty;

                await facts.RecordAsync(
                    FactType.McpConnected,
                    grant.Id.ToString(),
                    new Actor(code.UserId, username),
                    new JsonObject { ["client"] = client.Name, ["clientId"] = client.Id.ToString() },
                    ct);

                await transaction.CommitAsync(ct);

                return Tokens(access, refresh, grant.Scope);
            }

            case "refresh_token":
            {
                var refreshHash = McpSecrets.Hash(form["refresh_token"].ToString());
                var grant = await db.McpGrants.FirstOrDefaultAsync(g => g.RefreshTokenHash == refreshHash, ct);

                if (grant is null || !grant.IsOpen(now) || grant.ClientId != client.Id)
                    return OAuthError("invalid_grant", "The refresh token is not valid.");

                // Rotated on every use: the old refresh token is dead the moment this answers.
                var access = McpSecrets.NewAccessToken();
                var refresh = McpSecrets.NewRefreshToken();

                grant.AccessTokenHash = McpSecrets.Hash(access);
                grant.AccessExpiresAt = now + McpSecrets.AccessTokenLife;
                grant.RefreshTokenHash = McpSecrets.Hash(refresh);
                grant.RefreshExpiresAt = now + McpSecrets.RefreshTokenLife;
                await db.SaveChangesAsync(ct);

                return Tokens(access, refresh, grant.Scope);
            }

            default:
                return OAuthError("unsupported_grant_type", "grant_type must be authorization_code or refresh_token.");
        }
    }

    private static IResult Tokens(string access, string refresh, string scope) => Results.Json(new
    {
        access_token = access,
        token_type = "Bearer",
        expires_in = (int)McpSecrets.AccessTokenLife.TotalSeconds,
        refresh_token = refresh,
        scope,
    });

    // ── Revoke ─────────────────────────────────────────────────────────────────────────────

    /// <summary>RFC 7009. Always 200 for a well-formed request: whether the token existed is not the caller's business.</summary>
    private static async Task<IResult> RevokeAsync(
        HttpContext http,
        [FromServices] ModbotContext db,
        [FromServices] AccountFacts facts,
        [FromServices] IModbotClock clock,
        CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
            return OAuthError("invalid_request", "Send the request as application/x-www-form-urlencoded.");

        var form = await http.Request.ReadFormAsync(ct);
        var (client, clientError) = await ClientOfAsync(http, form, db, ct);
        if (client is null)
            return clientError!;

        var token = form["token"].ToString();
        if (token.Length == 0)
            return Results.Ok();

        var hash = McpSecrets.Hash(token);
        var grant = await db.McpGrants.Include(g => g.Client)
            .FirstOrDefaultAsync(g => g.AccessTokenHash == hash || g.RefreshTokenHash == hash, ct);

        if (grant is null || grant.ClientId != client.Id || grant.RevokedAt is not null)
            return Results.Ok();

        var now = clock.UtcNow;
        var username = await db.Users.AsNoTracking().Where(u => u.Id == grant.UserId).Select(u => u.Username).FirstOrDefaultAsync(ct) ?? string.Empty;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        grant.RevokedAt = now;
        await db.SaveChangesAsync(ct);

        await facts.RecordAsync(
            FactType.McpDisconnected,
            grant.Id.ToString(),
            new Actor(grant.UserId, username),
            new JsonObject { ["client"] = grant.Client?.Name, ["by"] = "app" },
            ct);

        await transaction.CommitAsync(ct);

        return Results.Ok();
    }

    // ── Client authentication ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The client behind a token or revoke request: from a Basic header or the form's
    /// <c>client_id</c> and <c>client_secret</c>. A client registered with a secret must show
    /// it; a public client shows only its id.
    /// </summary>
    private static async Task<(McpClient? Client, IResult? Error)> ClientOfAsync(HttpContext http, IFormCollection form, ModbotContext db, CancellationToken ct)
    {
        string? id = null;
        string? secret = null;

        var header = http.Request.Headers.Authorization.ToString();
        if (AuthenticationHeaderValue.TryParse(header, out var basic)
            && string.Equals(basic.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            && basic.Parameter is not null)
        {
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(basic.Parameter));
                var colon = pair.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    id = Uri.UnescapeDataString(pair[..colon]);
                    secret = Uri.UnescapeDataString(pair[(colon + 1)..]);
                }
            }
            catch (FormatException)
            {
                // Not base64: treated as no header.
            }
        }

        id ??= form["client_id"].ToString();
        secret ??= form["client_secret"].ToString() is { Length: > 0 } s ? s : null;

        var client = await McpClientDocuments.ResolveAsync(
            id, db, http.RequestServices.GetRequiredService<IHttpClientFactory>(), http.RequestServices.GetRequiredService<IModbotClock>(), ct);
        if (client is null)
            return (null, OAuthError("invalid_client", "Unknown client.", StatusCodes.Status401Unauthorized));

        if (client.SecretHash is not null && !McpSecrets.HashMatches(secret, client.SecretHash))
            return (null, OAuthError("invalid_client", "The client secret is not valid.", StatusCodes.Status401Unauthorized));

        return (client, null);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private static IResult OAuthError(string error, string description, int status = StatusCodes.Status400BadRequest)
        => Results.Json(new { error, error_description = description }, statusCode: status);

    public static IReadOnlyList<string> RedirectUrisOf(McpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            return JsonSerializer.Deserialize<List<string>>(client.RedirectUris) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> NeedsOf(ModbotPermissions needs) =>
        [.. PermissionCatalog.All.Where(p => p.Value != 0 && needs.HasFlag((ModbotPermissions)p.Value)).Select(p => p.Label)];
}
