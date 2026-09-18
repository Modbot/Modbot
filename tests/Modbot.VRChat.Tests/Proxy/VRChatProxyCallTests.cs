using System.Net;
using System.Text;
using Modbot.VRChat.Proxy;

namespace Modbot.VRChat.Tests.Proxy;

/// <summary>
/// VRChat proxy design: what of a caller's request reaches VRChat, and what of VRChat's answer
/// reaches the caller. The caller's key never goes out; the service account's session never
/// comes back.
/// </summary>
public class VRChatProxyCallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri Host = new("https://api.vrchat.cloud");

    private static VRChatProxyRequest Request(
        string method = "GET",
        string path = "api/1/users/usr_test",
        string query = "",
        byte[]? body = null,
        string? contentType = null,
        params (string Name, string Value)[] headers)
        => new(
            method, path, query,
            [.. headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value))],
            body, contentType);

    [Fact]
    public void ThePathAndQueryGoUnderVRChatsHost()
    {
        var destination = VRChatProxyCall.Destination(Host, "api/1/users/usr_test", "?n=10&offset=5");

        Assert.Equal("https://api.vrchat.cloud/api/1/users/usr_test?n=10&offset=5", destination.ToString());
    }

    /// <summary>A path that starts with two slashes is a path, never another host.</summary>
    [Fact]
    public void APathCannotNameAnotherHost()
    {
        var destination = VRChatProxyCall.Destination(Host, "//evil.example/api/1/users", "");

        Assert.Equal("api.vrchat.cloud", destination.Host);
        Assert.StartsWith("https://api.vrchat.cloud/", destination.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OnTheServiceAccount_TheCallersKeyAndCookiesNeverGoOut()
    {
        var request = Request(headers:
        [
            ("Authorization", "Bearer mbk_secret"),
            ("Cookie", "auth=mbk_secret"),
            ("Accept", "application/json"),
            ("X-Forwarded-For", "203.0.113.9"),
            ("User-Agent", "somebody-elses-library/1.0"),
        ]);

        using var message = VRChatProxyCall.Build(
            Host, request, "Modbot/1.0 test@example.com",
            new Dictionary<string, string> { ["X-Modbot-Developer-Contact-Email"] = "me@bin.moe" },
            VRChatProxyAccount.Service);

        Assert.Null(message.Headers.Authorization);
        Assert.False(message.Headers.Contains("Cookie"));
        Assert.False(message.Headers.Contains("X-Forwarded-For"));

        Assert.Equal("application/json", message.Headers.GetValues("Accept").Single());
        Assert.Equal("Modbot/1.0 test@example.com", message.Headers.GetValues("User-Agent").Single());
        Assert.Equal("me@bin.moe", message.Headers.GetValues("X-Modbot-Developer-Contact-Email").Single());
        Assert.Equal(HttpMethod.Get, message.Method);
    }

    [Fact]
    public void OnTheCallersOwnCookie_TheirCookiesGoOutAndTheirKeyStillDoesNot()
    {
        var request = Request(headers:
        [
            ("Authorization", "Bearer mbk_secret"),
            ("Cookie", "auth=authcookie_theirs; twoFactorAuth=abc"),
        ]);

        using var message = VRChatProxyCall.Build(Host, request, "Modbot/1.0", null, VRChatProxyAccount.Caller);

        Assert.Equal("auth=authcookie_theirs; twoFactorAuth=abc", message.Headers.GetValues("Cookie").Single());
        Assert.Null(message.Headers.Authorization);
    }

    [Fact]
    public async Task ABodyGoesOutWithItsContentType()
    {
        var body = Encoding.UTF8.GetBytes("""{"userId":"usr_test"}""");
        var request = Request("post", "api/1/groups/grp_test/bans", body: body, contentType: "application/json; charset=utf-8");

        using var message = VRChatProxyCall.Build(Host, request, "Modbot/1.0", null, VRChatProxyAccount.Service);

        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.NotNull(message.Content);
        Assert.Equal("application/json", message.Content.Headers.ContentType?.MediaType);
        Assert.Equal(body, await message.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task OnTheServiceAccount_TheSessionCookieVRChatSetsNeverComesBack()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":{"message":"no such user"}}""", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "auth=authcookie_service; Path=/; HttpOnly");
        response.Headers.TryAddWithoutValidation("ETag", "\"abc\"");
        response.Headers.TryAddWithoutValidation("CF-Ray", "1234-LHR");
        response.Headers.TryAddWithoutValidation("X-Powered-By", "Express");

        var answer = await VRChatProxyCall.ReadAsync(response, VRChatProxyAccount.Service, Ct);

        // VRChat's own status and body, as they came.
        Assert.Equal(404, answer.StatusCode);
        Assert.Contains("no such user", Encoding.UTF8.GetString(answer.Body), StringComparison.Ordinal);
        Assert.StartsWith("application/json", answer.ContentType, StringComparison.Ordinal);

        var names = answer.Headers.Select(h => h.Key).ToList();
        Assert.DoesNotContain("Set-Cookie", names, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Powered-By", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ETag", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CF-Ray", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnTheCallersOwnCookie_ASetCookieIsTheirsAndComesBack()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "auth=authcookie_theirs; Path=/");

        var answer = await VRChatProxyCall.ReadAsync(response, VRChatProxyAccount.Caller, Ct);

        Assert.Contains(answer.Headers, h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) && h.Value.StartsWith("auth=authcookie_theirs", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTextIsHandedToTheClassifier()
    {
        var json = new VRChatProxyResponse(403, [], Encoding.UTF8.GetBytes("""{"error":"blocked"}"""), "application/json");
        var image = new VRChatProxyResponse(200, [], [0xFF, 0xD8, 0xFF], "image/jpeg");
        var empty = new VRChatProxyResponse(204, [], [], null);

        Assert.Equal("""{"error":"blocked"}""", VRChatProxyCall.TextOf(json));
        Assert.Null(VRChatProxyCall.TextOf(image));
        Assert.Null(VRChatProxyCall.TextOf(empty));
    }
}
