using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Net;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Core.Bluesky;

/// <summary>
/// What a sign-in with Bluesky keeps beside its tokens, to renew them: the sign-in server, Modbot's
/// client id there, the sign-in's own DPoP key, and when the access token ends. Secret (the key):
/// never logged, never returned by the API.
/// </summary>
/// <param name="Issuer">The sign-in server, as its own document names itself.</param>
/// <param name="TokenEndpoint">Where tokens are renewed.</param>
/// <param name="ClientId">Modbot's client id the sign-in was made with: its client document's address.</param>
/// <param name="DpopKey">The sign-in's DPoP key, a private JSON Web Key. The same key for the whole sign-in.</param>
/// <param name="ExpiresAt">When the access token ends, by the server's <c>expires_in</c>.</param>
/// <param name="Scope">What the sign-in server granted.</param>
public sealed record BlueskyOAuthGrant(
    string Issuer,
    string TokenEndpoint,
    string ClientId,
    string DpopKey,
    DateTimeOffset ExpiresAt,
    string Scope)
{
    // The key is a secret; keep it out of any ToString.
    public override string ToString() => $"BlueskyOAuthGrant({Issuer}, {Scope})";
}

/// <summary>A sign-in with Bluesky that was started and is waiting for Bluesky to send the browser back.</summary>
/// <param name="State">The value that must come back with the browser.</param>
/// <param name="Verifier">The PKCE verifier; its hash went to Bluesky.</param>
/// <param name="DpopKey">The sign-in's DPoP key, kept for the whole sign-in after.</param>
/// <param name="Issuer">The sign-in server; the callback's <c>iss</c> must be this.</param>
/// <param name="TokenEndpoint">Where the code is traded for tokens.</param>
/// <param name="ClientId">Modbot's client id at the start.</param>
/// <param name="RedirectUri">Where Bluesky sends the browser back.</param>
/// <param name="Did">The account found from the handle; the tokens must be for this one.</param>
/// <param name="Handle">The handle, checked both ways.</param>
/// <param name="Server">The account's own server.</param>
/// <param name="Scope">What was asked for.</param>
/// <param name="StartedBy">The Modbot account that started it; only that account may finish it.</param>
/// <param name="StartedAt">When it started: it is good for <see cref="BlueskyOAuth.PendingLifetime"/>.</param>
public sealed record BlueskyOAuthPending(
    string State,
    string Verifier,
    string DpopKey,
    string Issuer,
    string TokenEndpoint,
    string ClientId,
    string RedirectUri,
    string Did,
    string Handle,
    string Server,
    string Scope,
    Guid StartedBy,
    DateTimeOffset StartedAt)
{
    // The state, verifier and key are secrets; keep them out of any ToString.
    public override string ToString() => $"BlueskyOAuthPending({Did}, {StartedAt:O})";
}

/// <summary>How a sign-in with Bluesky ended, as the callback says it to the page.</summary>
public enum BlueskyOAuthEnd
{
    /// <summary>Signed in, as the account that was asked for.</summary>
    SignedIn,

    /// <summary>The person said no on Bluesky's page, or went back.</summary>
    Cancelled,

    /// <summary>No sign-in was waiting, the state did not match, or it took longer than ten minutes.</summary>
    Expired,

    /// <summary>The browser coming back is not signed in to Modbot as the person who started it.</summary>
    OtherPerson,

    /// <summary>Bluesky signed in another account than the one the handle names.</summary>
    WrongAccount,

    /// <summary>Bluesky refused the sign-in.</summary>
    Refused,

    /// <summary>Bluesky did not answer.</summary>
    Unreachable,

    /// <summary>Bluesky is limiting Modbot.</summary>
    Limited,
}

/// <summary>What finishing a sign-in with Bluesky came to.</summary>
public sealed record BlueskyOAuthFinish(
    BlueskyOAuthEnd End,
    BlueskyTokens? Tokens = null,
    BlueskyOAuthPending? Pending = null,
    BlueskyFailure? Failure = null);

/// <summary>
/// Signing in with Bluesky's own sign-in page (OAuth, posts design §4.2c, step 3b), for an install
/// with a public https address: Modbot is a confidential client of the account's sign-in server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Modbot's client document</strong> is served at <see cref="MetadataPath"/> on the public
/// address, which is also its client id; Bluesky's server fetches it. It lists Modbot's own signing
/// key's public half; the private half is kept encrypted (<c>settings.bluesky_oauth_key_encrypted</c>)
/// and signs each <c>private_key_jwt</c> client assertion. Offered only when the public address is
/// https with no port, because atproto takes no other client id (Bluesky design fact 4).
/// </para>
/// <para>
/// <strong>The sign-in</strong>: the account's server names its sign-in server
/// (<c>/.well-known/oauth-protected-resource</c>), whose own document must name itself the same; the
/// request goes by PAR with PKCE (S256), a state, and a DPoP proof made with a new key kept for the
/// whole sign-in. The callback must bring back the state and the sign-in server's <c>iss</c>, be made by
/// the same Modbot account, and get tokens whose <c>sub</c> is the DID the handle was found to be.
/// </para>
/// <para>
/// <strong>Scopes.</strong> The narrowest Bluesky's permission spec has for posting
/// (<see cref="NarrowScope"/>: posts, made, replaced and deleted, and image blobs). A sign-in server
/// that answers <c>invalid_scope</c> to it is asked once more with <see cref="WideScope"/>, which is
/// what an app password can do. Whether bsky.social takes the narrow one is not checked yet.
/// </para>
/// <para>
/// <strong>Nonces.</strong> A server that answers <c>use_dpop_nonce</c> gets the same call once more
/// with the nonce it gave. That is the protocol, not a retry: a 429 is never sent again (CLAUDE.md).
/// Every call is on the guarded client (<see cref="BlueskyClient.HttpClientName"/>), because the
/// account's owner chooses the servers.
/// </para>
/// </remarks>
public sealed class BlueskyOAuth(IHttpClientFactory http, ISecretProtector protector, IModbotClock clock)
{
    /// <summary>Where Modbot's client document is served, on the public address. Also the client id.</summary>
    public const string MetadataPath = "/oauth/bluesky/client-metadata.json";

    /// <summary>Where Bluesky sends the browser back.</summary>
    public const string CallbackPath = "/api/bluesky/callback";

    /// <summary>
    /// The narrowest scopes that post: <c>putRecord</c> needs both create and update on the post
    /// collection, a delete needs delete, and the card picture needs an image blob.
    /// </summary>
    public const string NarrowScope = "atproto repo:app.bsky.feed.post?action=create&action=update&action=delete blob:image/*";

    /// <summary>What an app password can do: asked for only when the narrow scopes are refused.</summary>
    public const string WideScope = "atproto transition:generic";

    /// <summary>How long a started sign-in waits for Bluesky to send the browser back.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long a client assertion is good for.</summary>
    public static readonly TimeSpan AssertionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>The access token's life when the server does not say: the spec's recommended five minutes.</summary>
    public static readonly TimeSpan DefaultAccessLifetime = TimeSpan.FromMinutes(5);

    private const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly BlueskyNonces _nonces = new();
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    /// <summary>
    /// The public address Modbot can be a Bluesky client at: https, the default port, a host name (not
    /// an address, not localhost), no path. Null when there is none, and then sign-in with Bluesky is
    /// not offered.
    /// </summary>
    public static string? OriginFor(string? publicAddress)
    {
        if (string.IsNullOrWhiteSpace(publicAddress) || !Uri.TryCreate(publicAddress.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.HostNameType != UriHostNameType.Dns
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath is not ("/" or "")
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Whether sign-in with Bluesky is offered for this public address.</summary>
    public static bool Offered(string? publicAddress) => OriginFor(publicAddress) is not null;

    /// <summary>Modbot's client id for this public address: its client document's address. Null when not offered.</summary>
    public static string? ClientIdFor(string? publicAddress) => OriginFor(publicAddress) is { } origin ? origin + MetadataPath : null;

    /// <summary>Where Bluesky sends the browser back, for this public address. Null when not offered.</summary>
    public static string? RedirectFor(string? publicAddress) => OriginFor(publicAddress) is { } origin ? origin + CallbackPath : null;

    /// <summary>
    /// Modbot's client document (atproto OAuth client metadata) for this public address: a web
    /// confidential client, <c>private_key_jwt</c> with ES256, DPoP-bound tokens, the one redirect, and
    /// the public half of Modbot's signing key. Null when not offered.
    /// </summary>
    public static JsonObject? Metadata(string? publicAddress, string clientKey)
    {
        if (OriginFor(publicAddress) is not { } origin || BlueskyKeys.PublicJwk(clientKey, withUse: true) is not { } key)
            return null;

        return new JsonObject
        {
            ["client_id"] = origin + MetadataPath,
            ["client_name"] = "Modbot",
            ["client_uri"] = origin,
            ["application_type"] = "web",
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["redirect_uris"] = new JsonArray(origin + CallbackPath),
            // Both: the narrow one is asked for first, the wide one only if it is refused.
            ["scope"] = NarrowScope + " transition:generic",
            ["token_endpoint_auth_method"] = "private_key_jwt",
            ["token_endpoint_auth_signing_alg"] = BlueskyKeys.Algorithm,
            ["dpop_bound_access_tokens"] = true,
            ["jwks"] = new JsonObject { ["keys"] = new JsonArray(key) },
        };
    }

    /// <summary>
    /// Modbot's own signing key: the stored one, or a new one made and stored now. One that cannot be
    /// read (the database's key changed) is replaced; sign-ins made with the old one then end, and
    /// someone signs in again.
    /// </summary>
    public async Task<string> ClientKeyAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (await StoredKeyAsync(db, ct).ConfigureAwait(false) is { } kept)
            return kept;

        await _keyLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (await StoredKeyAsync(db, ct).ConfigureAwait(false) is { } meanwhile)
                return meanwhile;

            var key = BlueskyKeys.NewPrivateJwk();
            var sealedKey = protector.Protect(key);

            await db.Settings
                .Where(s => s.Id == 1)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyOAuthKeyEncrypted, sealedKey), ct)
                .ConfigureAwait(false);

            return key;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    /// <summary>
    /// Starts a sign-in for <paramref name="account"/> (found from its handle both ways): finds its
    /// sign-in server, pushes the request (PAR), keeps what the callback needs, and answers the address
    /// to send the browser to.
    /// </summary>
    public async Task<BlueskyResult<Uri>> StartAsync(
        ModbotContext db, string publicAddress, BlueskyAccount account, Guid startedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(account);

        if (ClientIdFor(publicAddress) is not { } clientId || RedirectFor(publicAddress) is not { } redirect)
            return BlueskyResult<Uri>.Failed(new BlueskyFailure(BlueskyProblem.Other, 0, Message: "Bluesky sign-in needs a public https address."));

        var server = await SignInServerAsync(account.Server, ct).ConfigureAwait(false);
        if (server.Value is not { } found)
            return BlueskyResult<Uri>.Failed(server.Failure!);

        var clientKey = await ClientKeyAsync(db, ct).ConfigureAwait(false);
        var state = BlueskyKeys.Random();
        var verifier = BlueskyKeys.Random();
        var dpopKey = BlueskyKeys.NewPrivateJwk();

        Dictionary<string, string> Form(string scope) => new(StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect,
            ["scope"] = scope,
            ["state"] = state,
            ["code_challenge"] = BlueskyKeys.Sha256(verifier),
            ["code_challenge_method"] = "S256",
            ["login_hint"] = account.Handle,
            ["client_assertion_type"] = AssertionType,
            ["client_assertion"] = Assertion(clientKey, clientId, found.Issuer),
        };

        var scope = NarrowScope;
        var pushed = await PostFormAsync(found.Par, dpopKey, () => Form(scope), null, ct).ConfigureAwait(false);

        if (pushed.Failure is { Error: "invalid_scope" })
        {
            scope = WideScope;
            pushed = await PostFormAsync(found.Par, dpopKey, () => Form(scope), null, ct).ConfigureAwait(false);
        }

        if (pushed.Value is not { } answer)
            return BlueskyResult<Uri>.Failed(pushed.Failure!);

        if (answer["request_uri"] is not JsonValue r || !r.TryGetValue<string>(out var requestUri) || requestUri.Length == 0)
            return BlueskyResult<Uri>.Failed(new BlueskyFailure(BlueskyProblem.Other, 200, Message: "Bluesky's answer had no request."));

        var pending = new BlueskyOAuthPending(
            state, verifier, dpopKey, found.Issuer, found.Token.ToString(), clientId, redirect,
            account.Did, account.Handle, account.Server.ToString(), scope, startedBy, clock.UtcNow);

        var sealedPending = protector.Protect(JsonSerializer.Serialize(pending, JsonOptions));

        await db.Settings
            .Where(s => s.Id == 1)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyOAuthPendingEncrypted, sealedPending), ct)
            .ConfigureAwait(false);

        var address = found.Authorize.GetLeftPart(UriPartial.Path)
            + "?client_id=" + Uri.EscapeDataString(clientId)
            + "&request_uri=" + Uri.EscapeDataString(requestUri);

        return BlueskyResult<Uri>.Ok(new Uri(address));
    }

    /// <summary>
    /// Finishes a sign-in Bluesky sent the browser back from. The waiting sign-in is taken whatever
    /// happens: it is good once. Writes nothing else; the caller keeps the tokens.
    /// </summary>
    /// <param name="person">The Modbot account the browser coming back is signed in as.</param>
    public async Task<BlueskyOAuthFinish> FinishAsync(
        ModbotContext db, string? code, string? state, string? issuer, string? error, Guid? person, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var stored = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.BlueskyOAuthPendingEncrypted)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (stored is not null)
        {
            await db.Settings
                .Where(s => s.Id == 1 && s.BlueskyOAuthPendingEncrypted == stored)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskyOAuthPendingEncrypted, (string?)null), ct)
                .ConfigureAwait(false);
        }

        var pending = ReadPending(stored);
        var now = clock.UtcNow;

        if (pending is null || string.IsNullOrEmpty(state) || !Same(pending.State, state)
            || now < pending.StartedAt || now - pending.StartedAt >= PendingLifetime)
        {
            return new BlueskyOAuthFinish(BlueskyOAuthEnd.Expired);
        }

        if (!string.IsNullOrEmpty(error))
            return new BlueskyOAuthFinish(BlueskyOAuthEnd.Cancelled, Pending: pending);

        if (person != pending.StartedBy)
            return new BlueskyOAuthFinish(BlueskyOAuthEnd.OtherPerson, Pending: pending);

        // The sign-in server says who it is; it must be the one the request went to (RFC 9207).
        if (!string.Equals(issuer, pending.Issuer, StringComparison.Ordinal) || string.IsNullOrEmpty(code))
            return new BlueskyOAuthFinish(BlueskyOAuthEnd.Refused, Pending: pending);

        var clientKey = await StoredKeyAsync(db, ct).ConfigureAwait(false);
        if (clientKey is null)
            return new BlueskyOAuthFinish(BlueskyOAuthEnd.Refused, Pending: pending);

        var traded = await PostFormAsync(
            new Uri(pending.TokenEndpoint),
            pending.DpopKey,
            () => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = pending.RedirectUri,
                ["code_verifier"] = pending.Verifier,
                ["client_id"] = pending.ClientId,
                ["client_assertion_type"] = AssertionType,
                ["client_assertion"] = Assertion(clientKey, pending.ClientId, pending.Issuer),
            },
            null,
            ct).ConfigureAwait(false);

        if (traded.Value is not { } body)
        {
            var failure = traded.Failure!;
            var end = failure.StopsTheLane ? BlueskyOAuthEnd.Limited
                : failure.Unclear ? BlueskyOAuthEnd.Unreachable
                : BlueskyOAuthEnd.Refused;

            return new BlueskyOAuthFinish(end, Pending: pending, Failure: failure);
        }

        var grant = new BlueskyOAuthGrant(pending.Issuer, pending.TokenEndpoint, pending.ClientId, pending.DpopKey, now, pending.Scope);
        if (TokensOf(body, grant, refreshBefore: null, pending.Handle, now) is not { } tokens)
            return new BlueskyOAuthFinish(BlueskyOAuthEnd.Refused, Pending: pending);

        // The tokens must be for the account the handle named (atproto OAuth: "critical").
        if (!string.Equals(tokens.Did, pending.Did, StringComparison.Ordinal))
            return new BlueskyOAuthFinish(BlueskyOAuthEnd.WrongAccount, Pending: pending);

        return new BlueskyOAuthFinish(BlueskyOAuthEnd.SignedIn, tokens, pending);
    }

    /// <summary>
    /// Renews a sign-in's tokens: the refresh token is spent and a new one comes back, so the caller
    /// writes the answer before it uses it. The same DPoP key, the same client id.
    /// </summary>
    public async Task<BlueskyResult<BlueskyTokens>> RefreshAsync(ModbotContext db, BlueskyTokens tokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);

        if (tokens.OAuth is not { } grant || tokens.RefreshJwt.Length == 0)
            return BlueskyResult<BlueskyTokens>.Failed(new BlueskyFailure(BlueskyProblem.TokenRefused, 0));

        var clientKey = await StoredKeyAsync(db, ct).ConfigureAwait(false);
        if (clientKey is null)
            return BlueskyResult<BlueskyTokens>.Failed(new BlueskyFailure(BlueskyProblem.TokenRefused, 0, "invalid_client"));

        if (!Uri.TryCreate(grant.TokenEndpoint, UriKind.Absolute, out var endpoint) || Endpoint(grant.TokenEndpoint) is null)
            return BlueskyResult<BlueskyTokens>.Failed(new BlueskyFailure(BlueskyProblem.TokenRefused, 0));

        var answer = await PostFormAsync(
            endpoint,
            grant.DpopKey,
            () => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshJwt,
                ["client_id"] = grant.ClientId,
                ["client_assertion_type"] = AssertionType,
                ["client_assertion"] = Assertion(clientKey, grant.ClientId, grant.Issuer),
            },
            null,
            ct).ConfigureAwait(false);

        if (answer.Value is not { } body)
            return BlueskyResult<BlueskyTokens>.Failed(answer.Failure!);

        var now = clock.UtcNow;

        return TokensOf(body, grant, tokens.RefreshJwt, tokens.Handle, now) is { } fresh && fresh.Did == tokens.Did
            ? BlueskyResult<BlueskyTokens>.Ok(fresh)
            : BlueskyResult<BlueskyTokens>.Failed(new BlueskyFailure(BlueskyProblem.TokenRefused, 200));
    }

    /// <summary>The waiting sign-in as stored, or null when there is none or it cannot be read.</summary>
    public BlueskyOAuthPending? ReadPending(string? encrypted)
    {
        try
        {
            return protector.Unprotect(encrypted) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<BlueskyOAuthPending>(json, JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The client assertion (<c>private_key_jwt</c>): Modbot's client id as <c>iss</c> and <c>sub</c>,
    /// the sign-in server as <c>aud</c>, a fresh <c>jti</c>, and <c>iat</c>/<c>exp</c> from Modbot's
    /// clock. The key is named by its <c>kid</c>, which the server binds the sign-in to.
    /// </summary>
    public string Assertion(string clientKey, string clientId, string issuer)
    {
        var now = clock.UtcNow;

        return BlueskyKeys.Sign(
            clientKey,
            new JsonObject { ["typ"] = "JWT", ["kid"] = BlueskyKeys.KidOf(clientKey) },
            new JsonObject
            {
                ["iss"] = clientId,
                ["sub"] = clientId,
                ["aud"] = issuer,
                ["jti"] = BlueskyKeys.Random(16),
                ["iat"] = now.ToUnixTimeSeconds(),
                ["exp"] = (now + AssertionLifetime).ToUnixTimeSeconds(),
            });
    }

    /// <summary>
    /// An endpoint Modbot will send to: https on port 443, a public host, no user name or fragment.
    /// Null otherwise. The guarded client holds to the same, this says it before anything is sent.
    /// </summary>
    public static Uri? Endpoint(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return null;

        return uri.Scheme == Uri.UriSchemeHttps
            && uri.Port == 443
            && uri.UserInfo.Length == 0
            && uri.Fragment.Length == 0
            && !PublicAddresses.IsBlockedHost(uri.Host)
                ? uri
                : null;
    }

    /// <summary>The sign-in server's own document, as far as Modbot uses it.</summary>
    private sealed record SignInServer(string Issuer, Uri Par, Uri Authorize, Uri Token);

    /// <summary>
    /// The sign-in server for an account server: the account server's
    /// <c>/.well-known/oauth-protected-resource</c> names it, and its own
    /// <c>/.well-known/oauth-authorization-server</c> must name itself the same.
    /// </summary>
    private async Task<BlueskyResult<SignInServer>> SignInServerAsync(Uri accountServer, CancellationToken ct)
    {
        var resource = await GetJsonAsync(new Uri(accountServer, ".well-known/oauth-protected-resource"), ct).ConfigureAwait(false);
        if (resource.Value is not { } named)
            return BlueskyResult<SignInServer>.Failed(resource.Failure!);

        var issuer = named["authorization_servers"] is JsonArray servers && servers.Count > 0
            && servers[0] is JsonValue first && first.TryGetValue<string>(out var text)
                ? text
                : null;

        // An issuer is an origin: https, port 443, no path.
        if (BlueskyIdentity.ServerAddress(issuer) is not { } issuerAddress
            || !string.Equals(issuerAddress.GetLeftPart(UriPartial.Authority), issuer, StringComparison.Ordinal))
        {
            return BlueskyResult<SignInServer>.Failed(new BlueskyFailure(BlueskyProblem.NotFound, 0, Message: "The account's server names no sign-in server."));
        }

        var document = await GetJsonAsync(new Uri(issuerAddress, ".well-known/oauth-authorization-server"), ct).ConfigureAwait(false);
        if (document.Value is not { } meta)
            return BlueskyResult<SignInServer>.Failed(document.Failure!);

        string? Read(string name) => meta[name] is JsonValue value && value.TryGetValue<string>(out var read) ? read : null;

        if (!string.Equals(Read("issuer"), issuer, StringComparison.Ordinal)
            || Endpoint(Read("pushed_authorization_request_endpoint")) is not { } par
            || Endpoint(Read("authorization_endpoint")) is not { } authorize
            || Endpoint(Read("token_endpoint")) is not { } token)
        {
            return BlueskyResult<SignInServer>.Failed(new BlueskyFailure(BlueskyProblem.Other, 200, Message: "Bluesky's sign-in server did not describe itself."));
        }

        return BlueskyResult<SignInServer>.Ok(new SignInServer(issuer!, par, authorize, token));
    }

    /// <summary>
    /// A token answer read: a DPoP token for an account (<c>sub</c>), with the <c>atproto</c> scope.
    /// A renewal that brings no new refresh token keeps the old one.
    /// </summary>
    private static BlueskyTokens? TokensOf(JsonObject body, BlueskyOAuthGrant grant, string? refreshBefore, string? handle, DateTimeOffset now)
    {
        string? Read(string name) => body[name] is JsonValue value && value.TryGetValue<string>(out var read) ? read : null;

        var access = Read("access_token");
        var refresh = Read("refresh_token") ?? refreshBefore;
        var sub = Read("sub");
        var type = Read("token_type");
        var scope = Read("scope") ?? grant.Scope;

        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || !BlueskyIdentity.IsDid(sub)
            || !string.Equals(type, "DPoP", StringComparison.OrdinalIgnoreCase)
            || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("atproto", StringComparer.Ordinal))
        {
            return null;
        }

        var lasts = body["expires_in"] is JsonValue e && e.TryGetValue<long>(out var seconds) && seconds > 0 && seconds < 86_400
            ? TimeSpan.FromSeconds(seconds)
            : DefaultAccessLifetime;

        return new BlueskyTokens(access, refresh, sub!, handle, grant with { ExpiresAt = now + lasts, Scope = scope });
    }

    private async Task<string?> StoredKeyAsync(ModbotContext db, CancellationToken ct)
    {
        var stored = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.BlueskyOAuthKeyEncrypted)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        try
        {
            var key = protector.Unprotect(stored);
            return BlueskyKeys.KidOf(key) is not null ? key : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private async Task<BlueskyResult<JsonObject>> GetJsonAsync(Uri address, CancellationToken ct)
    {
        try
        {
            using var response = await http.CreateClient(BlueskyClient.HttpClientName).GetAsync(address, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return BlueskyResult<JsonObject>.Failed(BlueskyErrors.FromXrpc(response.StatusCode, text, response.Headers));

            return JsonNode.Parse(text) is JsonObject value
                ? BlueskyResult<JsonObject>.Ok(value)
                : BlueskyResult<JsonObject>.Failed(new BlueskyFailure(BlueskyProblem.Other, (int)response.StatusCode));
        }
        catch (HttpRequestException)
        {
            return BlueskyResult<JsonObject>.Failed(BlueskyErrors.NoAnswer());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return BlueskyResult<JsonObject>.Failed(BlueskyErrors.NoAnswer());
        }
        catch (JsonException)
        {
            return BlueskyResult<JsonObject>.Failed(BlueskyErrors.NoAnswer());
        }
    }

    /// <summary>
    /// A form post to the sign-in server with a DPoP proof. Asked for a nonce, the call is made once
    /// more with it, with a fresh form (a new assertion <c>jti</c>). A 429 is never sent again.
    /// </summary>
    private async Task<BlueskyResult<JsonObject>> PostFormAsync(
        Uri address, string dpopKey, Func<Dictionary<string, string>> form, string? accessToken, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = new FormUrlEncodedContent(form()) };
                var nonce = _nonces.For(address);
                request.Headers.Add(BlueskyDpop.Header, BlueskyDpop.Proof(dpopKey, HttpMethod.Post, address, nonce, accessToken, clock.UtcNow));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await http.CreateClient(BlueskyClient.HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var given = _nonces.Keep(address, response);

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt == 0 && given is not null && given != nonce && BlueskyDpop.AsksForNonce(response, text))
                        continue;

                    return BlueskyResult<JsonObject>.Failed(FailureOf(response.StatusCode, text, response.Headers));
                }

                return JsonNode.Parse(text) is JsonObject value
                    ? BlueskyResult<JsonObject>.Ok(value)
                    : BlueskyResult<JsonObject>.Failed(new BlueskyFailure(BlueskyProblem.Other, (int)response.StatusCode));
            }
            catch (HttpRequestException)
            {
                return BlueskyResult<JsonObject>.Failed(BlueskyErrors.NoAnswer());
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return BlueskyResult<JsonObject>.Failed(BlueskyErrors.NoAnswer());
            }
            catch (JsonException)
            {
                return BlueskyResult<JsonObject>.Failed(BlueskyErrors.NoAnswer());
            }
        }
    }

    /// <summary>
    /// A sign-in server's refusal (RFC 6749 §5.2: <c>error</c>, <c>error_description</c>). A 429 is a
    /// rate limit and a 5xx no answer, as from an account server; anything else is the sign-in refused.
    /// </summary>
    private static BlueskyFailure FailureOf(HttpStatusCode status, string? text, HttpResponseHeaders headers)
    {
        var code = (int)status;
        if (code == 429 || code >= 500)
            return BlueskyErrors.FromXrpc(status, text, headers);

        string? error = null, description = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(text) && JsonNode.Parse(text) is JsonObject body)
            {
                error = body["error"] is JsonValue e && e.TryGetValue<string>(out var word) ? word : null;
                description = body["error_description"] is JsonValue d && d.TryGetValue<string>(out var words) ? words : null;
            }
        }
        catch (JsonException)
        {
        }

        return new BlueskyFailure(BlueskyProblem.TokenRefused, code, error, description);
    }

    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
