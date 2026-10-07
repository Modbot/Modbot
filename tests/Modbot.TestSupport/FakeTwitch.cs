using System.Net;
using System.Text;
using System.Text.Json;

namespace Modbot.TestSupport;

/// <summary>
/// Twitch's token endpoint and API, answered from a script: the client credentials grant, Get Users
/// and Get Streams. Records every request with its method, address, headers and form body, so a test
/// can check exactly what was sent, and what was not.
/// </summary>
/// <remarks>
/// Any request it has no answer for is a 500 and is still recorded, so a test that expects "only
/// these calls" sees the stray one. A rate limit answers 429 with a <c>Ratelimit-Reset</c> Unix time
/// and is counted in <see cref="LimitedRequests"/>, so a test can show it was not sent again.
/// </remarks>
public sealed class FakeTwitch : HttpMessageHandler
{
    public const string ClientId = "abcdefghij0123456789klmnopqrst";
    public const string ClientSecret = "secret0123456789abcdefghijklmn";
    public const string Login = "ourgroup";
    public const string ChannelId = "141981764";
    public const string DisplayName = "OurGroup";

    private int _tokens;

    public List<Recorded> Requests { get; } = [];

    /// <summary>The token endpoint's answer. 200 gives a token.</summary>
    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    public string TokenError { get; set; } = """{"status":400,"message":"invalid client"}""";

    /// <summary>How many tokens were handed out. Each is <c>token-1</c>, <c>token-2</c> and so on.</summary>
    public int TokensMade => _tokens;

    /// <summary>The token a call must carry. Null means the latest one made.</summary>
    public string? AcceptedToken { get; set; }

    /// <summary>The next API call answers 401 once, as an expired or revoked token does.</summary>
    public bool RefuseTokenOnce { get; set; }

    /// <summary>The channel Get Users knows. Null means Twitch has no channel by that login.</summary>
    public bool ChannelExists { get; set; } = true;

    /// <summary>The stream Get Streams gives. Null means the channel is not live.</summary>
    public FakeStream? Live { get; set; }

    /// <summary>Every API call answers this instead, when set (a 429, a 500, a 403).</summary>
    public HttpStatusCode? ApiStatus { get; set; }

    /// <summary>A <c>Ratelimit-Reset</c> Unix time sent with a 429. Null sends none.</summary>
    public long? ResetAt { get; set; }

    /// <summary>How many calls were answered 429.</summary>
    public int LimitedRequests { get; private set; }

    public sealed record FakeStream(
        string Id,
        DateTimeOffset StartedAt,
        string Title = "Movie night in VRChat",
        string Category = "VRChat",
        int Viewers = 12,
        string Type = "live");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;

        Requests.Add(new Recorded(
            request.Method,
            uri,
            Form(body),
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Client-Id", out var ids) ? ids.FirstOrDefault() : null));

        if (request.Method == HttpMethod.Post && uri.Host == "id.twitch.tv" && uri.AbsolutePath == "/oauth2/token")
        {
            var form = Form(body);

            if (TokenStatus != HttpStatusCode.OK)
                return Json(TokenStatus, TokenError);

            if (form.GetValueOrDefault("client_id") != ClientId
                || form.GetValueOrDefault("client_secret") != ClientSecret
                || form.GetValueOrDefault("grant_type") != "client_credentials")
            {
                return Json(HttpStatusCode.BadRequest, TokenError);
            }

            _tokens++;
            return Json(HttpStatusCode.OK, $$"""{"access_token":"token-{{_tokens}}","expires_in":5011271,"token_type":"bearer"}""");
        }

        if (request.Method == HttpMethod.Get && uri.Host == "api.twitch.tv" && uri.AbsolutePath.StartsWith("/helix/", StringComparison.Ordinal))
        {
            if (RefuseTokenOnce)
            {
                RefuseTokenOnce = false;
                return Json(HttpStatusCode.Unauthorized, """{"error":"Unauthorized","status":401,"message":"Invalid OAuth token"}""");
            }

            var expected = "Bearer " + (AcceptedToken ?? "token-" + _tokens);
            if (request.Headers.Authorization?.ToString() != expected
                || !request.Headers.TryGetValues("Client-Id", out var sent)
                || sent.FirstOrDefault() != ClientId)
            {
                return Json(HttpStatusCode.Unauthorized, """{"error":"Unauthorized","status":401,"message":"Invalid OAuth token"}""");
            }

            if (ApiStatus is { } status)
            {
                if (status == HttpStatusCode.TooManyRequests)
                    LimitedRequests++;

                var response = Json(status, $$"""{"error":"Error","status":{{(int)status}},"message":"Scripted."}""");

                if (ResetAt is { } reset)
                    response.Headers.Add("Ratelimit-Reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture));

                return response;
            }

            var query = Query(uri);

            if (uri.AbsolutePath == "/helix/users")
            {
                object[] data = ChannelExists && query.GetValueOrDefault("login") == Login
                    ? [new { id = ChannelId, login = Login, display_name = DisplayName, broadcaster_type = string.Empty }]
                    : [];

                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data }));
            }

            if (uri.AbsolutePath == "/helix/streams")
            {
                object[] rows = Live is { } live && query.GetValueOrDefault("user_id") == ChannelId
                    ?
                    [
                        new
                        {
                            id = live.Id,
                            user_id = ChannelId,
                            user_login = Login,
                            game_name = live.Category,
                            type = live.Type,
                            title = live.Title,
                            viewer_count = live.Viewers,
                            started_at = live.StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                        },
                    ]
                    : [];

                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = rows }));
            }
        }

        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> Query(Uri uri)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return values;
    }

    private static Dictionary<string, string> Form(string? body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(body))
            return form;

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            form[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] =
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }

        return form;
    }

    public sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Form, string? Authorization, string? ClientId);
}
