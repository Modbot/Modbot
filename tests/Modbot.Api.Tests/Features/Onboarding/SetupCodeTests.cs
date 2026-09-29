using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Onboarding;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// The setup code: what stops whoever reaches a fresh deployment first from owning it (first-run
/// setup code design).
/// </summary>
/// <remarks>
/// These send their own requests rather than going through <c>OnboardingTestContext.PostAsync</c>,
/// which adds the code for every other suite.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class SetupCodeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly object Administrator = new
    {
        username = "first",
        password = "a-long-enough-password",
        confirmPassword = "a-long-enough-password",
        email = "first@example.com",
    };

    private readonly PostgresFixture _db;

    public SetupCodeTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> SendAsync(
        ApiTestHost host, HttpMethod method, string path, object? body, string? code)
    {
        var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        if (code is not null)
            request.Headers.Add(SetupCode.Header, code);

        return host.Client.SendAsync(request, Ct);
    }

    private async Task<int> AccountsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Users.CountAsync(Ct);
    }

    [Fact]
    public async Task WithoutTheCode_TheFirstAccountIsRefused()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await SendAsync(host, HttpMethod.Post, "/api/onboarding/administrator", Administrator, code: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Wrong setup code.", (await response.ReadJsonAsync(Ct)).GetProperty("error").GetString());
        Assert.Equal(0, await AccountsAsync());
    }

    [Fact]
    public async Task WithTheWrongCode_TheFirstAccountIsRefused()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await SendAsync(host, HttpMethod.Post, "/api/onboarding/administrator", Administrator, "AAAA-AAAA-AAAA");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Wrong setup code.", (await response.ReadJsonAsync(Ct)).GetProperty("error").GetString());
        Assert.Equal(0, await AccountsAsync());
    }

    [Fact]
    public async Task WithTheRightCode_TheFirstAccountIsMade()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await SendAsync(host, HttpMethod.Post, "/api/onboarding/administrator", Administrator, host.SetupCode);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await AccountsAsync());
    }

    /// <summary>
    /// Read off a console and typed by hand: case, spaces and dashes do not matter.
    /// </summary>
    [Fact]
    public async Task TheCodeIsAcceptedWithoutDashesAndInLowerCase()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var typed = host.SetupCode!.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant();
        var response = await SendAsync(host, HttpMethod.Post, "/api/onboarding/administrator", Administrator, typed);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OnceTheFirstAccountExists_TheCodeIsDroppedAndRefused()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);
        var code = host.SetupCode;

        (await SendAsync(host, HttpMethod.Post, "/api/onboarding/administrator", Administrator, code))
            .EnsureSuccessStatusCode();

        Assert.Null(host.SetupCode);

        // The code is no stand-in for a session once there is somebody to sign in as.
        var withCode = await SendAsync(
            host, HttpMethod.Post, "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "hunter2" }, code);
        Assert.Equal(HttpStatusCode.Unauthorized, withCode.StatusCode);

        var second = await SendAsync(
            host, HttpMethod.Post, "/api/onboarding/administrator",
            new { username = "second", password = "a-long-enough-password", confirmPassword = "a-long-enough-password", email = "second@example.com" },
            code);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);

        var anonymous = await SendAsync(
            host, HttpMethod.Post, "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "hunter2" }, code: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Assert.Equal(1, await AccountsAsync());
    }

    /// <summary>
    /// Every wizard endpoint asks for it, not only step 1, so nothing is saved before the account
    /// exists -- the connection check used to store a proxy address for anybody who asked.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/onboarding/vrchat")]
    [InlineData("POST", "/api/onboarding/connection-test")]
    [InlineData("GET", "/api/onboarding/groups")]
    [InlineData("POST", "/api/onboarding/group")]
    [InlineData("POST", "/api/onboarding/integrations")]
    [InlineData("POST", "/api/onboarding/complete")]
    public async Task EveryWizardStepAsksForTheCode(string method, string path)
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await SendAsync(host, new HttpMethod(method), path, method == "POST" ? new { } : null, code: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutTheCode_TheConnectionCheckSavesNoProxy()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        await SendAsync(
            host, HttpMethod.Post, "/api/onboarding/connection-test",
            new { useProxy = true, proxyUrl = "http://proxy.example.com:11202", proxyUsername = "u", proxyPassword = "p" },
            code: null);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Null(settings.ProxyUrl);
    }

    /// <summary>
    /// The web app asks whether this deployment is set up before it knows whether to show the code
    /// field, so the question itself needs no code.
    /// </summary>
    [Fact]
    public async Task TheStatusEndpointNeedsNoCode()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await SendAsync(host, HttpMethod.Get, "/api/onboarding/status", null, code: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.ReadJsonAsync(Ct)).GetProperty("hasAdministrator").GetBoolean());
    }

    // ── The code itself.

    [Fact]
    public void ARandomCodeIsThreeGroupsOfFour()
    {
        var code = new SetupCode(null).Current;

        Assert.Matches("^[A-HJKMNP-Z2346-9]{4}-[A-HJKMNP-Z2346-9]{4}-[A-HJKMNP-Z2346-9]{4}$", code);
    }

    [Fact]
    public void EachProcessMakesItsOwn() =>
        Assert.NotEqual(new SetupCode(null).Current, new SetupCode(null).Current);

    [Fact]
    public void TheEnvironmentVariableSetsIt()
    {
        var code = new SetupCode("  my-own-code ");

        Assert.Equal("my-own-code", code.Current);
        Assert.True(code.Matches("MY OWN CODE"));
        Assert.False(code.Matches("my-own-cod"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void AValueWithNothingInItMeansARandomCode(string value)
    {
        var code = new SetupCode(value);

        Assert.Matches("^.{4}-.{4}-.{4}$", code.Current);
        Assert.False(code.Matches(""));
        Assert.False(code.Matches("-"));
    }

    [Fact]
    public void ADroppedCodeMatchesNothing()
    {
        var code = new SetupCode("abcd");
        code.Drop();

        Assert.Null(code.Current);
        Assert.False(code.Matches("abcd"));
    }
}
