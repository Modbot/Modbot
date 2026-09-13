using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// Shared plumbing for the onboarding suites: a reset deployment, a scripted gate, and one way of
/// sending a request with or without a session.
/// </summary>
internal static class OnboardingTestContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string AdminUsername = "owner";
    public const string AdminPassword = "a-long-enough-password";

    /// <summary>
    /// A host whose database is back to "freshly deployed": no staff account, no settings row.
    /// </summary>
    public static async Task<ApiTestHost> FreshAsync(
        PostgresFixture db, FakeVRChatGate? gate, CancellationToken ct)
    {
        await ApiTestHost.ResetDeploymentAsync(db, ct);
        return await ApiTestHost.StartAsync(db, gate);
    }

    /// <summary>
    /// A host that has been through step 1, plus the administrator's session cookie — the state
    /// every step after the first one actually runs in.
    /// </summary>
    /// <param name="linked">
    /// Whether the administrator's VRChat account is linked directly in the database, skipping
    /// the bio check. True for every step after the link; the link step's own tests pass false.
    /// </param>
    public static async Task<(ApiTestHost Host, string Cookie)> SetUpAsync(
        PostgresFixture db, FakeVRChatGate? gate, CancellationToken ct, bool linked = true)
    {
        var host = await FreshAsync(db, gate, ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new
            {
                username = AdminUsername,
                password = AdminPassword,
                confirmPassword = AdminPassword,
                email = AdminEmail,
            },
            cookie: null,
            ct);

        response.EnsureSuccessStatusCode();

        if (linked)
        {
            var id = (await response.ReadJsonAsync(ct)).GetProperty("id").GetGuid();
            await using var context = db.NewContext();
            await TestAccounts.LinkAsync(context, id, "usr_owner", ct);
        }

        return (host, ApiTestHost.SessionCookie(response));
    }

    public const string AdminEmail = "owner@example.com";

    public static Task<HttpResponseMessage> PostAsync(
        this ApiTestHost host, string path, object? body, string? cookie, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, Json), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);

        return host.Client.SendAsync(request, ct);
    }

    public static Task<HttpResponseMessage> GetAsync(
        this ApiTestHost host, string path, string? cookie, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);

        return host.Client.SendAsync(request, ct);
    }

    public static async Task<JsonElement> ReadJsonAsync(
        this HttpResponseMessage response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    // Qualified: a sibling `Features/Settings` test namespace shadows the unqualified name from
    // inside `Features.Onboarding`, exactly as `Modbot.Api.Features.Settings` already does in the
    // shipped code. Naming the entity outright is the fix that stays correct either way.
    public static async Task<Core.Data.Entities.Settings> ReadSettingsAsync(
        PostgresFixture db, CancellationToken ct)
    {
        await using var context = db.NewContext();
        return await context.GetSettingsAsync(ct);
    }
}
