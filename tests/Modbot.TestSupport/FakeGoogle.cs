using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modbot.TestSupport;

/// <summary>
/// Google's token endpoint and Calendar API, answered from a script. Records every request with its
/// method, address and form body, so a test can check exactly what was sent, and what was not.
/// </summary>
/// <remarks>
/// Any request it has no answer for is a 500 and is still recorded, so a test that expects "only
/// these calls" sees the stray one.
/// </remarks>
public sealed class FakeGoogle : HttpMessageHandler
{
    public List<Recorded> Requests { get; } = [];

    /// <summary>The token endpoint's answer. 200 gives <see cref="AccessToken"/>.</summary>
    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>The token endpoint's body when <see cref="TokenStatus"/> is not 200.</summary>
    public string TokenError { get; set; } = """{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""";

    public string AccessToken { get; set; } = "ya29.test-access-token";

    /// <summary><c>events.list</c>'s answer. 200 gives the three fields below.</summary>
    public HttpStatusCode CalendarStatus { get; set; } = HttpStatusCode.OK;

    public string CalendarError { get; set; } = """{"error":{"code":404,"message":"Not Found","errors":[{"reason":"notFound"}]}}""";

    public string CalendarName { get; set; } = "Group events";

    public string TimeZone { get; set; } = "Europe/London";

    public string AccessRole { get; set; } = "writer";

    /// <summary><c>acl.list</c>'s answer. 200 gives a public sharing entry with <see cref="PublicRole"/>.</summary>
    public HttpStatusCode AclStatus { get; set; } = HttpStatusCode.OK;

    public string AclError { get; set; } = """{"error":{"code":403,"message":"Forbidden","errors":[{"reason":"forbidden"}]}}""";

    /// <summary>The role of the sharing entry for everyone, or null for none.</summary>
    public string? PublicRole { get; set; } = "reader";

    /// <summary>Seconds, for <c>Retry-After</c> on a refusal. Null sends none.</summary>
    public int? RetryAfterSeconds { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var uri = request.RequestUri!;

        Requests.Add(new Recorded(request.Method, uri, Form(body), request.Headers.Authorization?.ToString()));

        if (request.Method == HttpMethod.Post && uri.Host == "oauth2.googleapis.com" && uri.AbsolutePath == "/token")
        {
            return TokenStatus == HttpStatusCode.OK
                ? Json(HttpStatusCode.OK, $$"""{"access_token":"{{AccessToken}}","expires_in":3599,"token_type":"Bearer"}""")
                : Json(TokenStatus, TokenError);
        }

        if (request.Method == HttpMethod.Get && uri.Host == "www.googleapis.com" && uri.AbsolutePath.StartsWith("/calendar/v3/calendars/", StringComparison.Ordinal))
        {
            if (request.Headers.Authorization?.Parameter != AccessToken)
                return Json(HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"Invalid Credentials","errors":[{"reason":"authError"}]}}""");

            if (uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
            {
                return CalendarStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { summary = CalendarName, timeZone = TimeZone, accessRole = AccessRole }))
                    : Json(CalendarStatus, CalendarError);
            }

            if (uri.AbsolutePath.EndsWith("/acl", StringComparison.Ordinal))
            {
                if (AclStatus != HttpStatusCode.OK)
                    return Json(AclStatus, AclError);

                var items = new List<object> { new { scope = new { type = "user" }, role = "owner" } };
                if (PublicRole is not null)
                    items.Add(new { scope = new { type = "default" }, role = PublicRole });

                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items }));
            }
        }

        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
    }

    private HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        if (status != HttpStatusCode.OK && RetryAfterSeconds is { } seconds)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));

        return response;
    }

    private static Dictionary<string, string> Form(string? body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(body) || body.StartsWith('{'))
            return form;

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            form[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }

        return form;
    }

    /// <param name="Form">The form fields of a token request; empty for anything else.</param>
    public sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Form, string? Authorization);

    /// <summary>
    /// A service account key file made for a test: a fresh 2048-bit RSA key, with the given
    /// <c>token_uri</c> so a test can show it is ignored.
    /// </summary>
    public static (string Json, RSA Key) KeyFile(
        string clientEmail = "modbot@test-project.iam.gserviceaccount.com",
        string keyId = "0123456789abcdef",
        string tokenUri = "https://oauth2.googleapis.com/token",
        string type = "service_account")
    {
        var rsa = RSA.Create(2048);

        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = type,
            ["project_id"] = "test-project",
            ["private_key_id"] = keyId,
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = clientEmail,
            ["client_id"] = "1234567890",
            ["auth_uri"] = "https://accounts.google.com/o/oauth2/auth",
            ["token_uri"] = tokenUri,
        });

        return (json, rsa);
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> that hands out clients over one handler.</summary>
public sealed class OneHandlerClients(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
