using System.Net;
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
/// </remarks>
public sealed class FakeBluesky : HttpMessageHandler
{
    public const string Handle = "ourgroup.bsky.social";
    public const string Did = "did:plc:ewvi7nxzyoun6zhxrhs64oiz";
    public const string Server = "https://morel.us-east.host.bsky.network/";
    public const string AppPassword = "abcd-efgh-ijkl-mnop";

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

        Requests.Add(new Recorded(request.Method, uri, method, request.Headers.Authorization?.Parameter, body));

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

        var bearer = request.Headers.Authorization?.Parameter;

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
    public sealed record Recorded(HttpMethod HttpMethod, Uri Uri, string Method, string? Bearer, string? Body);
}
