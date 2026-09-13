using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.TestSupport;
using Modbot.VRChat.Session;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// The administrator's contact email: required at onboarding, and what the gate puts in the
/// User-Agent it sends VRChat.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AdministratorEmailTests
{
    private readonly PostgresFixture _db;

    public AdministratorEmailTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFirstAdministratorMustGiveAnEmail()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "bin", password = "a-long-enough-password" },
            cookie: null,
            Ct);

        // VRChat wants a person to write to before it blocks, and that person is whoever set
        // this deployment up. A deployment must not come up without one.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheOperatorContactIsTheAdministratorsEmail()
    {
        var (host, _) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        // Resolved the way the VRChat client factory resolves it: through the interface the gate
        // registration declares, which the accounts layer must have replaced.
        var contact = host.Services.GetRequiredService<IOperatorContact>();

        Assert.IsType<AdministratorContact>(contact);
        Assert.Equal(OnboardingTestContext.AdminEmail, contact.Email);
    }

    [Fact]
    public async Task ChangingTheAdministratorsEmail_IsSeenWithoutWaitingForTheCache()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var contact = host.Services.GetRequiredService<IOperatorContact>();
        Assert.Equal(OnboardingTestContext.AdminEmail, contact.Email);

        var response = await host.Client.SendAsync(
            Put(host, "/api/auth/contact", new { email = "new-owner@example.com" }, cookie), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The clock has not moved, so a cache that was not invalidated would still say the old
        // address for another minute.
        Assert.Equal("new-owner@example.com", contact.Email);
    }

    private static HttpRequestMessage Put(ApiTestHost host, string path, object body, string cookie)
    {
        var request = host.Authenticated(HttpMethod.Put, path, cookie);
        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        return request;
    }
}
