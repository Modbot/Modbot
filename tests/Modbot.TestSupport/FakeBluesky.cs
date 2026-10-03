using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Modbot.TestSupport;

/// <summary>
/// Bluesky answered from a script: the public handle lookup, plc.directory, and one account's own
/// server (sign-in, refresh, session, profile, blobs and post records). Records every request, so a
/// test can check exactly what was sent, and what was not.
/// </summary>
/// <remarks>
/// <para>
/// Posts are kept by record key the way the reference server does (Bluesky design fact 20): a
/// <c>putRecord</c> with <c>swapRecord: null</c> at a free key makes the post; at a used key it
/// answers <c>InvalidSwap</c>, unless the record is the very same, which answers as done.
/// </para>
/// <para>
/// Any request it has no answer for is a 500 and is still recorded, so a test that expects "only
/// these calls" sees the stray one.
/// </para>
/// <para>
/// <strong>Bluesky's sign-in (OAuth, step 3b)</strong>: the account's server names
/// <see cref="AuthServer"/>, which serves its own document, PAR, and the token address. Both ask for
/// DPoP proofs with the nonce they gave last (<see cref="AuthNonce"/>, <see cref="ServerNonce"/>),
/// answering <c>use_dpop_nonce</c> to a proof without it, as Bluesky does. <see cref="Approve"/> plays
/// the person saying yes on Bluesky's page and answers the code. Refresh tokens are spent when used.
/// </para>
/// </remarks>
public sealed class FakeBluesky : HttpMessageHandler
{
    public const string Handle = "ourgroup.bsky.social";
    public const string Did = "did:plc:ewvi7nxzyoun6zhxrhs64oiz";
    public const string Server = "https://morel.us-east.host.bsky.network/";
    public const string AppPassword = "abcd-efgh-ijkl-mnop";

    /// <summary>The sign-in server the account's server names, as its issuer.</summary>
    public const string AuthServer = "https://bsky.social";

    public const string ParEndpoint = AuthServer + "/oauth/par";
    public const string AuthorizeEndpoint = AuthServer + "/oauth/authorize";
    public const string TokenEndpoint = AuthServer + "/oauth/token";

    private int _tokens;

    public FakeBluesky(Func<DateTimeOffset> now) => Now = now;

    /// <summary>The clock the tokens' <c>exp</c> are made from: the test's own.</summary>
    public Func<DateTimeOffset> Now { get; }

    public List<Recorded> Requests { get; } = [];

    /// <summary>The posts on the account, by record key, as they were put.</summary>
    public Dictionary<string, JsonObject> Posts { get; } = new(StringComparer.Ordinal);

    /// <summary>The blobs uploaded, by size.</summary>
    public List<int> Blobs { get; } = [];

    /// <summary>The access token handed out last; every call needing one must carry it.</summary>
    public string? AccessJwt { get; private set; }

    /// <summary>The refresh token handed out last. An older one is refused, as Bluesky does.</summary>
    public string? RefreshJwt { get; private set; }

    /// <summary>How long a new access token lasts.</summary>
    public TimeSpan AccessLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>The account's profile: its display name, and whether it carries the <c>bot</c> self-label.</summary>
    public string? DisplayName { get; set; } = "Our group";

    public bool Automated { get; set; } = true;

    /// <summary>The sign-in's answer: 200 for the right app password, else this status with <c>AuthenticationRequired</c>.</summary>
    public HttpStatusCode? SignInStatus { get; set; }

    /// <summary>A scripted answer to <c>putRecord</c>, before the store is touched. Null for the store's own.</summary>
    public Func<string, JsonObject, HttpResponseMessage?>? PutAnswer { get; set; }

    /// <summary>
    /// The put is stored on Bluesky's side, and the answer never comes back (a lost answer). Once:
    /// turned off after it happens.
    /// </summary>
    public bool DropNextPutAnswer { get; set; }

    /// <summary>A scripted answer to <c>getRecord</c> of a post. Null for the store's own.</summary>
    public Func<string, HttpResponseMessage?>? GetAnswer { get; set; }

    /// <summary>Every request to the account's server answers 429 with this reset time, while set.</summary>
    public DateTimeOffset? LimitedUntil { get; set; }

    public int SignIns { get; private set; }

    public int Refreshes { get; private set; }

    public int Puts { get; private set; }

    // --- Sign-in with Bluesky (OAuth) ---

    /// <summary>The nonce the sign-in server wants in DPoP proofs now. Changing it makes the next proof stale.</summary>
    public string AuthNonce { get; set; } = "auth-nonce-1";

    /// <summary>The nonce the account's server wants in DPoP proofs now.</summary>
    public string ServerNonce { get; set; } = "server-nonce-1";

    /// <summary>The sign-in server answers <c>invalid_scope</c> to anything narrower than <c>transition:generic</c>.</summary>
    public bool RefuseNarrowScope { get; set; }

    /// <summary>Changes the sign-in server's own document before it is sent: an endpoint moved elsewhere, say.</summary>
    public Action<JsonObject>? ChangeAuthServerDocument { get; set; }

    /// <summary>The token address answers 429 with this reset time, while set.</summary>
    public DateTimeOffset? TokenLimitedUntil { get; set; }

    /// <summary>The account the token answer names as <c>sub</c>. Another DID plays Bluesky signing in someone else.</summary>
    public string TokenSub { get; set; } = Did;

    /// <summary>How long a new OAuth access token lasts, by <c>expires_in</c>.</summary>
    public TimeSpan OAuthAccessLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Every pushed request, as its form fields.</summary>
    public List<Dictionary<string, string>> Pars { get; } = [];

    /// <summary>Every token request, as its form fields.</summary>
    public List<Dictionary<string, string>> TokenRequests { get; } = [];

    /// <summary>The refresh token handed out last for OAuth. An older one is refused.</summary>
    public string? OAuthRefresh { get; private set; }

    /// <summary>The DPoP key's thumbprint the tokens are bound to; a proof with another key is refused.</summary>
    public string? BoundKey { get; private set; }

    public int OAuthRefreshes { get; private set; }

    private readonly Dictionary<string, Dictionary<string, string>> _codes = new(StringComparer.Ordinal);
    private int _requests;

    /// <summary>The DID document plc.directory gives.</summary>
    public static JsonObject Document(string handle = Handle, string server = Server, string did = Did) => new()
    {
        ["id"] = did,
        ["alsoKnownAs"] = new JsonArray($"at://{handle}"),
        ["service"] = new JsonArray(new JsonObject
        {
            ["id"] = "#atproto_pds",
            ["type"] = "AtprotoPersonalDataServer",
            ["serviceEndpoint"] = server.TrimEnd('/'),
        }),
    };

    /// <summary>A token shaped like Bluesky's: a JWT whose middle says when it ends. Not signed.</summary>
    public static string Jwt(string name, DateTimeOffset exp)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"alg":"HS256"}""")}.{Part($$"""{"exp":{{exp.ToUnixTimeSeconds()}},"jti":"{{name}}"}""")}.sig-{name}";
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;
        var method = uri.AbsolutePath.StartsWith("/xrpc/", StringComparison.Ordinal) ? uri.AbsolutePath["/xrpc/".Length..] : uri.AbsolutePath;

        var dpop = request.Headers.TryGetValues("DPoP", out var proofs) ? proofs.FirstOrDefault() : null;
        Requests.Add(new Recorded(request.Method, uri, method, request.Headers.Authorization?.Parameter, body, request.Headers.Authorization?.Scheme, dpop));

        if (uri.GetLeftPart(UriPartial.Authority) == AuthServer)
            return SignInServer(request, uri, body, dpop);

        if (uri.Host == "public.api.bsky.app" && method == "com.atproto.identity.resolveHandle")
        {
            return Query(uri, "handle") == Handle
                ? Json(HttpStatusCode.OK, new JsonObject { ["did"] = Did })
                : Json(HttpStatusCode.BadRequest, Error("InvalidRequest", "Unable to resolve handle"));
        }

        if (uri.Host == "plc.directory")
        {
            return uri.AbsolutePath == "/" + Did
                ? Json(HttpStatusCode.OK, Document())
                : Json(HttpStatusCode.NotFound, Error("NotFound", "DID not registered"));
        }

        if (uri.GetLeftPart(UriPartial.Authority) + "/" != Server)
            return Json(HttpStatusCode.InternalServerError, Error("InternalServerError", "No script for this address"));

        if (LimitedUntil is { } until)
        {
            var limited = Json(HttpStatusCode.TooManyRequests, Error("RateLimitExceeded", "Rate Limit Exceeded"));
            limited.Headers.Add("ratelimit-reset", until.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return limited;
        }

        if (uri.AbsolutePath == "/.well-known/oauth-protected-resource")
        {
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["resource"] = Server.TrimEnd('/'),
                ["authorization_servers"] = new JsonArray(AuthServer),
            });
        }

        var bearer = request.Headers.Authorization?.Parameter;

        // A DPoP token needs a proof for this very call, with this server's nonce and the token's hash.
        if (string.Equals(request.Headers.Authorization?.Scheme, "DPoP", StringComparison.Ordinal))
        {
            var claims = ProofClaims(dpop);

            if (claims is null || claims["htm"]?.GetValue<string>() != request.Method.Method
                || claims["htu"]?.GetValue<string>() != uri.GetLeftPart(UriPartial.Path)
                || claims["ath"]?.GetValue<string>() != Sha256(bearer ?? string.Empty)
                || KeyOf(dpop) != BoundKey)
            {
                return Json(HttpStatusCode.Unauthorized, Error("InvalidToken", "Bad DPoP proof"));
            }

            if (claims["nonce"]?.GetValue<string>() != ServerNonce)
            {
                var stale = Json(HttpStatusCode.Unauthorized, Error("use_dpop_nonce", "Resource server requires nonce in DPoP proof"));
                stale.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("DPoP", "error=\"use_dpop_nonce\""));
                stale.Headers.Add("DPoP-Nonce", ServerNonce);
                return stale;
            }
        }

        switch (method)
        {
            case "com.atproto.server.createSession":
            {
                SignIns++;
                var sent = JsonNode.Parse(body ?? "{}");
                var right = sent?["identifier"]?.GetValue<string>() is Did or Handle && sent?["password"]?.GetValue<string>() == AppPassword;

                if (SignInStatus is { } status || !right)
                    return Json(SignInStatus ?? HttpStatusCode.Unauthorized, Error("AuthenticationRequired", "Invalid identifier or password"));

                return Json(HttpStatusCode.OK, NewTokens());
            }

            case "com.atproto.server.refreshSession":
                Refreshes++;
                return bearer is not null && bearer == RefreshJwt
                    ? Json(HttpStatusCode.OK, NewTokens())
                    : Json(HttpStatusCode.BadRequest, Error("ExpiredToken", "Token has expired"));

            case "com.atproto.server.getSession":
                return Authorised(bearer) ?? Json(HttpStatusCode.OK, new JsonObject { ["did"] = Did, ["handle"] = Handle });

            case "com.atproto.repo.getRecord":
                return GetRecord(uri);

            case "com.atproto.repo.uploadBlob":
            {
                if (Authorised(bearer) is { } refused)
                    return refused;

                var size = request.Content is null ? 0 : (await request.Content.ReadAsByteArrayAsync(cancellationToken)).Length;
                Blobs.Add(size);
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["blob"] = new JsonObject
                    {
                        ["$type"] = "blob",
                        ["ref"] = new JsonObject { ["$link"] = $"bafkrei-blob-{size}" },
                        ["mimeType"] = request.Content?.Headers.ContentType?.MediaType,
                        ["size"] = size,
                    },
                });
            }

            case "com.atproto.repo.putRecord":
                return PutRecord(bearer, body);

            case "com.atproto.repo.deleteRecord":
            {
                if (Authorised(bearer) is { } refused)
                    return refused;

                var key = JsonNode.Parse(body ?? "{}")?["rkey"]?.GetValue<string>() ?? string.Empty;
                Posts.Remove(key);
                return Json(HttpStatusCode.OK, new JsonObject());
            }
        }

        return Json(HttpStatusCode.InternalServerError, Error("InternalServerError", "No script for this call"));
    }

    private HttpResponseMessage PutRecord(string? bearer, string? body)
    {
        if (Authorised(bearer) is { } refused)
            return refused;

        Puts++;

        var sent = JsonNode.Parse(body ?? "{}") as JsonObject ?? new JsonObject();
        var key = sent["rkey"]?.GetValue<string>() ?? string.Empty;
        var record = sent["record"] as JsonObject ?? new JsonObject();

        if (PutAnswer?.Invoke(key, record) is { } scripted)
            return scripted;

        // swapRecord: null means "only if nothing is there" (fact 20). Left out, it would replace.
        if (!sent.ContainsKey("swapRecord") || sent["swapRecord"] is not null)
            return Json(HttpStatusCode.BadRequest, Error("InvalidRequest", "The test expects swapRecord: null"));

        if (Posts.TryGetValue(key, out var existing))
        {
            if (!JsonNode.DeepEquals(existing, record))
                return Json(HttpStatusCode.BadRequest, Error("InvalidSwap", "Record was at bafy-old"));
        }
        else
        {
            Posts[key] = (JsonObject)record.DeepClone();
        }

        if (DropNextPutAnswer)
        {
            DropNextPutAnswer = false;
            throw new HttpRequestException("The answer was lost.");
        }

        return Json(HttpStatusCode.OK, new JsonObject { ["uri"] = $"at://{Did}/app.bsky.feed.post/{key}", ["cid"] = $"bafy-{key}" });
    }

    private HttpResponseMessage GetRecord(Uri uri)
    {
        var collection = Query(uri, "collection");
        var key = Query(uri, "rkey") ?? string.Empty;

        if (collection == "app.bsky.actor.profile")
        {
            var value = new JsonObject { ["$type"] = "app.bsky.actor.profile", ["displayName"] = DisplayName };
            if (Automated)
                value["labels"] = new JsonObject { ["$type"] = "com.atproto.label.defs#selfLabels", ["values"] = new JsonArray(new JsonObject { ["val"] = "bot" }) };

            return Json(HttpStatusCode.OK, new JsonObject { ["uri"] = $"at://{Did}/app.bsky.actor.profile/self", ["cid"] = "bafy-profile", ["value"] = value });
        }

        if (GetAnswer?.Invoke(key) is { } scripted)
            return scripted;

        return Posts.TryGetValue(key, out var post)
            ? Json(HttpStatusCode.OK, new JsonObject { ["uri"] = $"at://{Did}/app.bsky.feed.post/{key}", ["cid"] = $"bafy-{key}", ["value"] = post.DeepClone() })
            : Json(HttpStatusCode.BadRequest, Error("RecordNotFound", $"Could not locate record: at://{Did}/app.bsky.feed.post/{key}"));
    }

    /// <summary>Null when <paramref name="bearer"/> is the access token handed out last and has not ended.</summary>
    private HttpResponseMessage? Authorised(string? bearer)
    {
        if (bearer is null || bearer != AccessJwt)
            return Json(HttpStatusCode.Unauthorized, Error("InvalidToken", "Token could not be verified"));

        return null;
    }

    private JsonObject NewTokens()
    {
        _tokens++;
        AccessJwt = Jwt($"access-{_tokens}", Now() + AccessLifetime);
        RefreshJwt = Jwt($"refresh-{_tokens}", Now() + TimeSpan.FromDays(90));

        return new JsonObject { ["accessJwt"] = AccessJwt, ["refreshJwt"] = RefreshJwt, ["did"] = Did, ["handle"] = Handle };
    }

    /// <summary>
    /// The person says yes on Bluesky's sign-in page for the last pushed request: the code Bluesky
    /// sends the browser back with, and the state it carries.
    /// </summary>
    public (string Code, string State) Approve()
    {
        var par = Pars[^1];
        var code = $"code-{Pars.Count}";
        _codes[code] = par;
        return (code, par["state"]);
    }

    /// <summary>The claims of a JWT, read without checking it: a DPoP proof, a client assertion.</summary>
    public static JsonObject? ClaimsOf(string? jwt) => Part(jwt, 1);

    /// <summary>The header of a JWT, read without checking it.</summary>
    public static JsonObject? HeaderOf(string? jwt) => Part(jwt, 0);

    /// <summary>
    /// Whether <paramref name="jwt"/> is signed by the ES256 public key <paramref name="publicJwk"/>:
    /// the client document's key for a client assertion, the proof's own header key for DPoP.
    /// </summary>
    public static bool SignedBy(string jwt, JsonObject publicJwk)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
            return false;

        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Base64Url.DecodeFromChars(publicJwk["x"]!.GetValue<string>()),
                Y = Base64Url.DecodeFromChars(publicJwk["y"]!.GetValue<string>()),
            },
        });

        return key.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static JsonObject? Part(string? jwt, int index)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 })
            return null;

        try
        {
            return JsonNode.Parse(Base64Url.DecodeFromChars(parts[index])) as JsonObject;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>A DPoP proof's claims, when it is shaped like one and signed by the key in its own header.</summary>
    private static JsonObject? ProofClaims(string? proof)
    {
        if (proof is null || HeaderOf(proof) is not { } header || header["typ"]?.GetValue<string>() != "dpop+jwt"
            || header["alg"]?.GetValue<string>() != "ES256" || header["jwk"] is not JsonObject jwk || jwk.ContainsKey("d")
            || !SignedBy(proof, jwk))
        {
            return null;
        }

        return ClaimsOf(proof);
    }

    private static string? KeyOf(string? proof) =>
        HeaderOf(proof)?["jwk"] is JsonObject jwk ? jwk["x"]?.GetValue<string>() + "." + jwk["y"]?.GetValue<string>() : null;

    private static string Sha256(string text) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(text)));

    private static Dictionary<string, string> Form(string? body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in (body ?? string.Empty).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            form[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }

        return form;
    }

    /// <summary>The sign-in server: its document, PAR and the token address, each asking for a nonce first.</summary>
    private HttpResponseMessage SignInServer(HttpRequestMessage request, Uri uri, string? body, string? dpop)
    {
        if (uri.AbsolutePath == "/.well-known/oauth-authorization-server")
        {
            var document = new JsonObject
            {
                ["issuer"] = AuthServer,
                ["pushed_authorization_request_endpoint"] = ParEndpoint,
                ["authorization_endpoint"] = AuthorizeEndpoint,
                ["token_endpoint"] = TokenEndpoint,
                ["require_pushed_authorization_requests"] = true,
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none", "private_key_jwt"),
                ["dpop_signing_alg_values_supported"] = new JsonArray("ES256"),
                ["client_id_metadata_document_supported"] = true,
            };

            ChangeAuthServerDocument?.Invoke(document);
            return Json(HttpStatusCode.OK, document);
        }

        if (request.Method != HttpMethod.Post || (uri.AbsoluteUri != ParEndpoint && uri.AbsoluteUri != TokenEndpoint))
            return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "not_found" });

        var claims = ProofClaims(dpop);
        if (claims is null || claims["htu"]?.GetValue<string>() != uri.AbsoluteUri || claims["htm"]?.GetValue<string>() != "POST")
            return WithNonce(Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_dpop_proof" }));

        if (claims["nonce"]?.GetValue<string>() != AuthNonce)
            return WithNonce(Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "use_dpop_nonce", ["error_description"] = "Authorization server requires nonce in DPoP proof" }));

        var form = Form(body);

        var assertion = form.GetValueOrDefault("client_assertion");
        var said = ClaimsOf(assertion);
        if (form.GetValueOrDefault("client_assertion_type") != "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"
            || said is null || said["aud"]?.GetValue<string>() != AuthServer
            || said["iss"]?.GetValue<string>() != form.GetValueOrDefault("client_id")
            || said["sub"]?.GetValue<string>() != form.GetValueOrDefault("client_id")
            || HeaderOf(assertion)?["kid"] is null)
        {
            return WithNonce(Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_client" }));
        }

        if (uri.AbsoluteUri == ParEndpoint)
        {
            Pars.Add(form);

            var scope = form.GetValueOrDefault("scope") ?? string.Empty;
            if (RefuseNarrowScope && !scope.Contains("transition:generic", StringComparison.Ordinal))
                return WithNonce(Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_scope", ["error_description"] = "Unsupported scope" }));

            return WithNonce(Json(HttpStatusCode.Created, new JsonObject { ["request_uri"] = $"urn:ietf:params:oauth:request_uri:req-{Pars.Count}", ["expires_in"] = 299 }));
        }

        TokenRequests.Add(form);

        if (TokenLimitedUntil is { } until)
        {
            var limited = WithNonce(Json(HttpStatusCode.TooManyRequests, new JsonObject { ["error"] = "RateLimitExceeded" }));
            limited.Headers.Add("ratelimit-reset", until.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return limited;
        }

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
            {
                if (form.GetValueOrDefault("code") is not { } code || !_codes.Remove(code, out var par)
                    || form.GetValueOrDefault("redirect_uri") != par["redirect_uri"]
                    || Sha256(form.GetValueOrDefault("code_verifier") ?? string.Empty) != par["code_challenge"])
                {
                    return WithNonce(Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" }));
                }

                BoundKey = KeyOf(dpop);
                return WithNonce(Json(HttpStatusCode.OK, NewOAuthTokens(par["scope"])));
            }

            case "refresh_token":
            {
                OAuthRefreshes++;

                if (form.GetValueOrDefault("refresh_token") is not { } refresh || refresh != OAuthRefresh || KeyOf(dpop) != BoundKey)
                    return WithNonce(Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "Invalid refresh token" }));

                return WithNonce(Json(HttpStatusCode.OK, NewOAuthTokens("atproto transition:generic")));
            }
        }

        return WithNonce(Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "unsupported_grant_type" }));
    }

    private HttpResponseMessage WithNonce(HttpResponseMessage response)
    {
        response.Headers.Add("DPoP-Nonce", AuthNonce);
        return response;
    }

    private JsonObject NewOAuthTokens(string scope)
    {
        _requests++;
        AccessJwt = Jwt($"oauth-access-{_requests}", Now() + OAuthAccessLifetime);
        OAuthRefresh = $"oauth-refresh-{_requests}";

        return new JsonObject
        {
            ["access_token"] = AccessJwt,
            ["token_type"] = "DPoP",
            ["refresh_token"] = OAuthRefresh,
            ["scope"] = scope,
            ["expires_in"] = (int)OAuthAccessLifetime.TotalSeconds,
            ["sub"] = TokenSub,
        };
    }

    /// <summary>An access token that has ended, for a test that wants the next call refused.</summary>
    public void EndAccessToken() => AccessJwt = "ended";

    public static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    public static JsonObject Error(string error, string message) => new() { ["error"] = error, ["message"] = message };

    private static string? Query(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == name)
                return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return null;
    }

    /// <param name="Method">The XRPC method, or the path for a plain address.</param>
    /// <param name="Bearer">The token sent, if any.</param>
    /// <param name="Scheme">How it was sent: <c>Bearer</c>, or <c>DPoP</c> for a sign-in with Bluesky.</param>
    /// <param name="Dpop">The DPoP proof sent with it, if any.</param>
    public sealed record Recorded(HttpMethod HttpMethod, Uri Uri, string Method, string? Bearer, string? Body, string? Scheme = null, string? Dpop = null);
}
