using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Twitch;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Twitch (Twitch design, steps 1 and 2): the client secret is kept encrypted and never
/// returned, a channel is read from what was typed, every change is audited, Check asks for a token
/// and reads and nothing else, a rate limit stops every call until Twitch's reset and is never sent
/// again, and the "live" post's sites are checked the way the Marketing tab checks a post.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class TwitchSettingsTests
{
    private const string Path = "/api/settings/twitch";

    private readonly PostgresFixture _db;

    public TwitchSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTestHost Host, FakeTwitch Twitch, string Cookie)> StartAsync()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var twitch = new FakeTwitch();
        var host = await ApiTestHost.StartAsync(_db, configure: services =>
            services.AddHttpClient(TwitchClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => twitch));

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        return (host, twitch, cookie);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task SetUpAsync(ApiTestHost host, string cookie, string channel = FakeTwitch.Login)
    {
        await OkAsync(await host.SendJsonAsync(
            HttpMethod.Put, Path, new { clientId = FakeTwitch.ClientId, clientSecret = FakeTwitch.ClientSecret, channel }, cookie, Ct));
    }

    [Fact]
    public async Task TheSecretIsStoredEncrypted_AndNeverReturned()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var saved = await host.SendJsonAsync(
            HttpMethod.Put, Path, new { clientId = FakeTwitch.ClientId, clientSecret = FakeTwitch.ClientSecret, channel = "OurGroup" }, cookie, Ct);
        var savedText = await saved.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(FakeTwitch.ClientSecret, savedText, StringComparison.Ordinal);

        var read = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);
        var readText = await read.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(FakeTwitch.ClientSecret, readText, StringComparison.Ordinal);

        var body = JsonDocument.Parse(readText).RootElement;
        Assert.True(body.GetProperty("secretStored").GetBoolean());
        Assert.Equal(FakeTwitch.ClientId, body.GetProperty("clientId").GetString());
        Assert.Equal("ourgroup", body.GetProperty("channel").GetString());

        await using var context = _db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        Assert.DoesNotContain(FakeTwitch.ClientSecret, settings.TwitchClientSecretEncrypted, StringComparison.Ordinal);
        Assert.Equal(FakeTwitch.ClientSecret, host.Services.GetRequiredService<ISecretProtector>().Unprotect(settings.TwitchClientSecretEncrypted));
    }

    [Fact]
    public async Task ThePostStartsWithTheBuiltInWordsAndNothingTicked()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));

        Assert.False(body.GetProperty("live").GetBoolean());
        Assert.Equal(3, body.GetProperty("postAfterMinutes").GetInt32());
        Assert.Equal(6, body.GetProperty("postEveryHours").GetInt32());
        Assert.Equal("We're live on Twitch", body.GetProperty("postTitle").GetString());
        Assert.Equal("{title}\n{link}", body.GetProperty("postText").GetString());

        var places = body.GetProperty("places");
        Assert.Equal(JsonValueKind.Null, places.GetProperty("discord").ValueKind);
        Assert.Equal(JsonValueKind.Null, places.GetProperty("vrChat").ValueKind);
        Assert.False(places.GetProperty("bluesky").GetBoolean());
    }

    [Theory]
    [InlineData("https://www.twitch.tv/OurGroup", "ourgroup")]
    [InlineData("@ourgroup", "ourgroup")]
    [InlineData("twitch.tv/ourgroup/schedule", "ourgroup")]
    public async Task AChannelIsTakenFromAPastedLink(string typed, string login)
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { channel = typed }, cookie, Ct));

        Assert.Equal(login, body.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task SomethingThatIsNotAChannelOrAnIdIsRefused_AllAtOnce()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var response = await host.SendJsonAsync(
            HttpMethod.Put, Path, new { clientId = "has space", channel = "https://example.com/x", postAfterMinutes = 0, postEveryHours = 1000 }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problems = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("This is not a client id.", problems);
        Assert.Contains("This is not a Twitch channel name.", problems);
        Assert.Equal(4, problems.Count);

        await using var context = _db.NewContext();
        Assert.Null((await context.GetSettingsAsync(Ct)).TwitchClientId);
    }

    [Fact]
    public async Task CheckAsksForATokenAndReads_AndWritesNothingToTwitch()
    {
        var (host, twitch, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));

        // One token request, then two reads: nothing is written to Twitch.
        Assert.Equal(3, twitch.Requests.Count);
        Assert.Equal(HttpMethod.Post, twitch.Requests[0].Method);
        Assert.Equal(new Uri("https://id.twitch.tv/oauth2/token"), twitch.Requests[0].Uri);
        Assert.Equal("client_credentials", twitch.Requests[0].Form["grant_type"]);
        Assert.All(twitch.Requests.Skip(1), r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("api.twitch.tv", r.Uri.Host);
            Assert.Equal(FakeTwitch.ClientId, r.ClientId);
            Assert.Equal("Bearer token-1", r.Authorization);
        });
        Assert.EndsWith("/helix/users", twitch.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("/helix/streams", twitch.Requests[2].Uri.AbsolutePath, StringComparison.Ordinal);

        var check = body.GetProperty("check");
        Assert.Equal(FakeTwitch.DisplayName, check.GetProperty("channelName").GetString());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("problem").ValueKind);
        Assert.True(body.GetProperty("canGoLive").GetBoolean());

        await using var context = _db.NewContext();
        Assert.Equal(FakeTwitch.ChannelId, (await context.GetSettingsAsync(Ct)).TwitchChannelId);
    }

    [Fact]
    public async Task ARefusedSecretSaysSo()
    {
        var (host, twitch, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);
        twitch.TokenStatus = HttpStatusCode.Forbidden;

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));

        Assert.Equal("Twitch did not accept the client id and secret.", body.GetProperty("check").GetProperty("problem").GetString());
        Assert.False(body.GetProperty("canGoLive").GetBoolean());
        Assert.Single(twitch.Requests);
    }

    [Fact]
    public async Task AChannelTwitchDoesNotKnowSaysSo()
    {
        var (host, twitch, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);
        twitch.ChannelExists = false;

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));

        Assert.Equal("Twitch has no channel with that name.", body.GetProperty("check").GetProperty("problem").GetString());
        Assert.False(body.GetProperty("canGoLive").GetBoolean());
    }

    /// <summary>CLAUDE.md: never retry a 429. A limit stops every call, Check included, until Twitch's reset.</summary>
    [Fact]
    public async Task ALimitStopsEveryCallUntilTwitchsReset_AndIsNotSentAgain()
    {
        var (host, twitch, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);
        twitch.ApiStatus = HttpStatusCode.TooManyRequests;
        twitch.ResetAt = host.Clock.UtcNow.AddMinutes(7).ToUnixTimeSeconds();

        var limited = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));
        Assert.Equal("Twitch is limiting Modbot.", limited.GetProperty("check").GetProperty("problem").GetString());
        Assert.Equal(host.Clock.UtcNow.AddMinutes(7), limited.GetProperty("limitedUntil").GetDateTimeOffset());

        // The 429 was answered once and not asked again.
        Assert.Equal(1, twitch.LimitedRequests);
        var sent = twitch.Requests.Count;

        // Pressed again inside the stop: nothing goes to Twitch.
        twitch.ApiStatus = null;
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));
        Assert.Equal(sent, twitch.Requests.Count);

        // Once it has passed, Check goes again.
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        var again = await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));
        Assert.True(twitch.Requests.Count > sent);
        Assert.Equal(JsonValueKind.Null, again.GetProperty("limitedUntil").ValueKind);
        Assert.Equal(JsonValueKind.Null, again.GetProperty("check").GetProperty("problem").ValueKind);
    }

    [Fact]
    public async Task CheckNeedsTheAppAndAChannel()
    {
        var (host, twitch, cookie) = await StartAsync();
        await using var running = host;

        var nothing = await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);

        await OkAsync(await host.SendJsonAsync(
            HttpMethod.Put, Path, new { clientId = FakeTwitch.ClientId, clientSecret = FakeTwitch.ClientSecret }, cookie, Ct));

        var noChannel = await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noChannel.StatusCode);
        Assert.Empty(twitch.Requests);
    }

    [Fact]
    public async Task TheLivePollGoesOnOnlyAfterACheckThatPassed()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);

        var early = await host.SendJsonAsync(HttpMethod.Put, Path, new { live = true }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Equal("Check Twitch first.", (await ApiTestHost.BodyOf(early, Ct)).GetProperty("error").GetString());

        await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));
        var on = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { live = true }, cookie, Ct));

        Assert.True(on.GetProperty("live").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, on.GetProperty("check").ValueKind);
    }

    [Fact]
    public async Task ChangingTheChannelClearsWhatCheckFound()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);
        await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { channel = "othergroup" }, cookie, Ct));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("check").ValueKind);
        Assert.False(body.GetProperty("canGoLive").GetBoolean());
        Assert.True(body.GetProperty("secretStored").GetBoolean());
    }

    [Fact]
    public async Task ForgetRemovesTheAppTheChannelAndWhatCheckFound_AndKeepsThePost()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;
        await SetUpAsync(host, cookie);
        await OkAsync(await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct));
        await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { live = true, postTitle = "Come and watch" }, cookie, Ct));

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Delete, Path, null, cookie, Ct));

        Assert.False(body.GetProperty("secretStored").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("clientId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("channel").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("check").ValueKind);
        Assert.False(body.GetProperty("live").GetBoolean());
        Assert.Equal("Come and watch", body.GetProperty("postTitle").GetString());

        await using var context = _db.NewContext();
        Assert.Null((await context.GetSettingsAsync(Ct)).TwitchClientSecretEncrypted);
    }

    [Fact]
    public async Task EveryChangeIsAudited_AndTheSecretOnlyAsChanged()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        await SetUpAsync(host, cookie);
        await OkAsync(await host.SendJsonAsync(HttpMethod.Delete, Path, null, cookie, Ct));

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        var twitch = facts.Select(ApiTestHost.DataOf).Where(d => d.GetProperty("setting").GetString() == "twitch").ToList();

        Assert.Equal(2, twitch.Count);
        Assert.All(twitch, d =>
        {
            Assert.True(d.GetProperty("changed").GetProperty("twitchClientSecret").GetProperty("secret").GetBoolean());
            Assert.DoesNotContain(FakeTwitch.ClientSecret, d.GetRawText(), StringComparison.Ordinal);
        });
        Assert.Contains(twitch, d => d.GetProperty("changed").GetProperty("twitchChannel").GetProperty("new").GetString() == FakeTwitch.Login);
    }

    // ── The "live" post's sites (step 2) ──────────────────────────────────────────────────

    [Fact]
    public async Task BlueskyCannotBeTickedBeforeItIsSetUp()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var response = await host.SendJsonAsync(HttpMethod.Put, Path, new { places = new { bluesky = true } }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problems = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("Bluesky is off.", problems);

        await using var context = _db.NewContext();
        Assert.False(TwitchPostPlaces.Parse((await context.GetSettingsAsync(Ct)).TwitchPostPlaces).AnyTicked);
    }

    [Fact]
    public async Task ADiscordChannelNotInTheServerCannotBeTicked()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var response = await host.SendJsonAsync(
            HttpMethod.Put, Path, new { places = new { discord = new { channelId = "999" } } }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problems = (await ApiTestHost.BodyOf(response, Ct)).GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("That channel is not in the Discord server.", problems);
    }

    [Fact]
    public async Task TheVRChatGroupCanBeTickedWithItsOwnChoices_AndIsAudited()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.ManagedGroupId = "grp_00000000-0000-0000-0000-000000000001";
            await context.SaveChangesAsync(Ct);
        }

        var body = await OkAsync(await host.SendJsonAsync(
            HttpMethod.Put, Path, new { places = new { vrChat = new { visibility = "public", notify = true } } }, cookie, Ct));

        var vrchat = body.GetProperty("places").GetProperty("vrChat");
        Assert.Equal("public", vrchat.GetProperty("visibility").GetString());
        Assert.True(vrchat.GetProperty("notify").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("places").GetProperty("discord").ValueKind);
        Assert.False(body.GetProperty("places").GetProperty("bluesky").GetBoolean());

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        Assert.Contains(facts.Select(ApiTestHost.DataOf), d =>
            d.GetProperty("changed").TryGetProperty("vrChatVisibility", out var visibility) && visibility.GetProperty("new").GetString() == "public");
    }

    [Fact]
    public async Task TheTemplateIsKept_AndEmptyGoesBackToTheBuiltInWords()
    {
        var (host, _, cookie) = await StartAsync();
        await using var running = host;

        var set = await OkAsync(await host.SendJsonAsync(
            HttpMethod.Put, Path, new { postTitle = "Watch {category}", postText = "{title} is on: {link}", postAfterMinutes = 5, postEveryHours = 12 }, cookie, Ct));

        Assert.Equal("Watch {category}", set.GetProperty("postTitle").GetString());
        Assert.Equal("{title} is on: {link}", set.GetProperty("postText").GetString());
        Assert.Equal(5, set.GetProperty("postAfterMinutes").GetInt32());
        Assert.Equal(12, set.GetProperty("postEveryHours").GetInt32());

        var reset = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { postTitle = "", postText = "" }, cookie, Ct));

        Assert.Equal("We're live on Twitch", reset.GetProperty("postTitle").GetString());
        Assert.Equal("{title}\n{link}", reset.GetProperty("postText").GetString());

        await using var context = _db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        Assert.Null(settings.TwitchPostTitle);
        Assert.Null(settings.TwitchPostText);
    }

    [Fact]
    public async Task SettingsNeedManageSettings()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, Path, new { }, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, Path + "/check", null, cookie, Ct)).StatusCode);
    }
}
