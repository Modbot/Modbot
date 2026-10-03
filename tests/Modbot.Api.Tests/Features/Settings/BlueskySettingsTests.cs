using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Settings;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Bluesky (Bluesky design §3.1, posts design §4.2c): only an app password is taken, it is
/// kept encrypted and never returned, every change is audited, Check signs in at most once and never
/// posts, a refused app password is not tried again, and Posting needs a Check that passed.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class BlueskySettingsTests(PostgresFixture db)
{
    private const string Path = "/api/settings/bluesky";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTestHost Host, FakeBluesky Bluesky, string Cookie)> StartAsync()
    {
        await ApiTestHost.ResetDeploymentAsync(db, Ct);

        ApiTestHost? started = null;
        var bluesky = new FakeBluesky(() => started?.Clock.UtcNow ?? DateTimeOffset.UnixEpoch);

        started = await ApiTestHost.StartAsync(db, configure: services =>
            services.AddHttpClient(BlueskyClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => bluesky));

        var (_, cookie) = await started.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        return (started, bluesky, cookie);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static Task<HttpResponseMessage> SaveAsync(ApiTestHost host, string cookie, object body) =>
        host.SendJsonAsync(HttpMethod.Put, Path, body, cookie, Ct);

    private static async Task<JsonElement> CheckAsync(ApiTestHost host, string cookie) =>
        await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));

    private static async Task SetUpAsync(ApiTestHost host, string cookie) =>
        await OkAsync(await SaveAsync(host, cookie, new { handle = "@" + FakeBluesky.Handle, appPassword = FakeBluesky.AppPassword }));

    [Fact]
    public async Task TheAppPasswordIsStoredEncrypted_AndNeverReturned()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var saved = await SaveAsync(host, cookie, new { handle = "@" + FakeBluesky.Handle, appPassword = FakeBluesky.AppPassword });
        var savedText = await saved.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(FakeBluesky.AppPassword, savedText, StringComparison.Ordinal);

        var read = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        Assert.DoesNotContain(FakeBluesky.AppPassword, read.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(FakeBluesky.Handle, read.GetProperty("handle").GetString());
        Assert.True(read.GetProperty("appPasswordStored").GetBoolean());
        Assert.False(read.GetProperty("posting").GetBoolean());

        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        Assert.NotEqual(FakeBluesky.AppPassword, settings.BlueskyAppPasswordEncrypted);
        Assert.Equal(FakeBluesky.AppPassword, host.Services.GetRequiredService<ISecretProtector>().Unprotect(settings.BlueskyAppPasswordEncrypted));
    }

    [Theory]
    [InlineData("hunter2")]
    [InlineData("my-main-password-123")]
    [InlineData("abcd-efgh-ijkl")]
    public async Task AValueThatIsNotAnAppPasswordIsRefused(string password)
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var response = await SaveAsync(host, cookie, new { handle = FakeBluesky.Handle, appPassword = password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Use an app password.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());

        await using var context = db.NewContext();
        Assert.Null((await context.GetSettingsAsync(Ct)).BlueskyAppPasswordEncrypted);
    }

    [Fact]
    public async Task AHandleThatIsNotOneIsRefused()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var response = await SaveAsync(host, cookie, new { handle = "not a handle" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BlueskySettingsEndpoints.NotAHandle, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task CheckSignsInAndReadsTheProfile_AndNeverPosts()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);

        var body = await CheckAsync(host, cookie);

        var check = body.GetProperty("check");
        Assert.Null(check.GetProperty("problem").GetString());
        Assert.Equal(FakeBluesky.Handle, check.GetProperty("handle").GetString());
        Assert.Equal("Our group", check.GetProperty("displayName").GetString());
        Assert.True(check.GetProperty("automated").GetBoolean());
        Assert.True(body.GetProperty("canPost").GetBoolean());

        Assert.Equal(1, bluesky.SignIns);
        Assert.Equal(0, bluesky.Puts);
        Assert.DoesNotContain(bluesky.Requests, r => r.Method is "com.atproto.repo.putRecord"
            or "com.atproto.repo.createRecord"
            or "com.atproto.repo.deleteRecord"
            or "com.atproto.repo.uploadBlob");

        // Signed in with the account's lasting id, not the handle, so a changed handle still works.
        var signIn = bluesky.Requests.Single(r => r.Method == "com.atproto.server.createSession");
        Assert.Contains(FakeBluesky.Did, signIn.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAccountNotMarkedAutomatedSaysNo()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        bluesky.Automated = false;
        await SetUpAsync(host, cookie);

        var body = await CheckAsync(host, cookie);

        Assert.False(body.GetProperty("check").GetProperty("automated").GetBoolean());
    }

    [Fact]
    public async Task CheckUsesTheSavedSession_WithoutSigningInAgain()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);
        await CheckAsync(host, cookie);

        host.Clock.Advance(TimeSpan.FromMinutes(30));
        var body = await CheckAsync(host, cookie);

        Assert.Null(body.GetProperty("check").GetProperty("problem").GetString());
        Assert.Equal(1, bluesky.SignIns);
        Assert.Contains(bluesky.Requests, r => r.Method == "com.atproto.server.getSession");
    }

    [Fact]
    public async Task TheSessionTokensAreStoredEncrypted_AndNeverReturned()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);

        var body = await CheckAsync(host, cookie);

        Assert.DoesNotContain(bluesky.AccessJwt!, body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(bluesky.RefreshJwt!, body.GetRawText(), StringComparison.Ordinal);

        await using var context = db.NewContext();
        var stored = (await context.GetSettingsAsync(Ct)).BlueskySessionEncrypted;
        Assert.NotNull(stored);
        Assert.DoesNotContain(bluesky.RefreshJwt!, stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedAppPasswordIsSaid_AndNotTriedAgainUntilANewOneIsSaved()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;

        // Shaped like an app password, and not the account's: revoked, say.
        await OkAsync(await SaveAsync(host, cookie, new { handle = FakeBluesky.Handle, appPassword = "zzzz-zzzz-zzzz-zzzz" }));

        var first = await CheckAsync(host, cookie);
        Assert.Equal(BlueskyErrors.NotAccepted, first.GetProperty("check").GetProperty("problem").GetString());
        Assert.False(first.GetProperty("canPost").GetBoolean());

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        var again = await CheckAsync(host, cookie);
        Assert.Equal(BlueskyErrors.NotAccepted, again.GetProperty("check").GetProperty("problem").GetString());
        Assert.Equal(1, bluesky.SignIns);

        // A new app password is tried at once, inside the sign-in guard's ten minutes.
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await OkAsync(await SaveAsync(host, cookie, new { appPassword = FakeBluesky.AppPassword }));
        var after = await CheckAsync(host, cookie);

        Assert.Equal(2, bluesky.SignIns);
        Assert.Null(after.GetProperty("check").GetProperty("problem").GetString());
    }

    [Fact]
    public async Task SignInsAreHeldToOneEveryTenMinutes()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);
        await CheckAsync(host, cookie);

        // The session is lost on Bluesky's side; the refresh is refused, and a sign-in would be next.
        bluesky.EndAccessToken();
        await using (var context = db.NewContext())
        {
            await context.Settings.ExecuteUpdateAsync(u => u.SetProperty(s => s.BlueskySessionEncrypted, (string?)null), Ct);
        }

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        var held = await CheckAsync(host, cookie);

        Assert.Equal(BlueskyErrors.TooManySignIns, held.GetProperty("check").GetProperty("problem").GetString());
        Assert.Equal(host.Clock.UtcNow.AddMinutes(5), held.GetProperty("signInAfter").GetDateTimeOffset());
        Assert.Equal(1, bluesky.SignIns);
    }

    [Fact]
    public async Task ARateLimitStopsCheck_UntilItResets()
    {
        var (host, bluesky, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);
        bluesky.LimitedUntil = host.Clock.UtcNow.AddMinutes(40);

        var limited = await CheckAsync(host, cookie);
        var requests = bluesky.Requests.Count;

        Assert.Equal(bluesky.LimitedUntil, limited.GetProperty("limitedUntil").GetDateTimeOffset());

        // Pressed again before the reset: nothing is sent.
        await CheckAsync(host, cookie);
        Assert.Equal(requests, bluesky.Requests.Count);
    }

    [Fact]
    public async Task PostingNeedsACheckThatPassed_HereAndInPosts()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);

        var early = await SaveAsync(host, cookie, new { posting = true });
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Equal(BlueskySettingsEndpoints.CheckFirst, (await ApiTestHost.BodyOf(early, Ct)).GetProperty("error").GetString());

        var mirrored = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/posts", new { bluesky = true }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, mirrored.StatusCode);

        await CheckAsync(host, cookie);

        var on = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, "/api/settings/posts", new { bluesky = true }, cookie, Ct));
        Assert.True(on.GetProperty("bluesky").GetBoolean());

        var read = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));
        Assert.True(read.GetProperty("posting").GetBoolean());
    }

    [Fact]
    public async Task RemoveForgetsTheAccount_AndEveryChangeIsAuditedWithoutThePassword()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);
        await CheckAsync(host, cookie);

        var removed = await OkAsync(await host.SendJsonAsync(HttpMethod.Delete, Path, null, cookie, Ct));

        Assert.Null(removed.GetProperty("handle").GetString());
        Assert.False(removed.GetProperty("appPasswordStored").GetBoolean());
        Assert.False(removed.GetProperty("posting").GetBoolean());

        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            Assert.Null(settings.BlueskySessionEncrypted);
            Assert.Null(settings.BlueskyDid);
        }

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        var bluesky = facts.Select(ApiTestHost.DataOf).Where(d => d.GetProperty("setting").GetString() == "bluesky").ToList();

        Assert.Contains(bluesky, d => d.GetProperty("changed").TryGetProperty("blueskyAppPassword", out var secret)
            && secret.GetProperty("secret").GetBoolean());
        Assert.All(bluesky, d => Assert.DoesNotContain(FakeBluesky.AppPassword, d.GetRawText(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyManageSettingsMayReadOrChangeIt()
    {
        var (host, _, _) = await StartAsync();
        await using var running = host;
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewPosts | ModbotPermissions.ManagePosts, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, viewer, Ct)).StatusCode);
    }
}
