using System.Net;
using System.Text;

namespace Modbot.Api.Tests.Fakes;

/// <summary>
/// Discord's OAuth2 endpoints, answered from a script: the token exchange, <c>users/@me</c> and
/// revoke. Records every request with its form body so a test can check what was sent.
/// </summary>
public sealed class FakeDiscordOAuthHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, Dictionary<string, string> Form, string? Authorization)> Requests { get; } = [];

    public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;

    public string AccessToken { get; set; } = "discord-access-token";

    public string UserId { get; set; } = "424242424242";

    public string Username { get; set; } = "member_one";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                form[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            }
        }

        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((request.Method, path, form, request.Headers.Authorization?.ToString()));

        return path switch
        {
            "/api/v10/oauth2/token" when TokenStatus != HttpStatusCode.OK => new HttpResponseMessage(TokenStatus),
            "/api/v10/oauth2/token" => Json($$"""{"access_token":"{{AccessToken}}","token_type":"Bearer","expires_in":604800,"scope":"identify"}"""),
            "/api/v10/users/@me" when request.Headers.Authorization?.Parameter == AccessToken =>
                Json($$"""{"id":"{{UserId}}","username":"{{Username}}","global_name":"Member One"}"""),
            "/api/v10/users/@me" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "/api/v10/oauth2/token/revoke" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
