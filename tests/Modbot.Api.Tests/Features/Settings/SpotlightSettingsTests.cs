using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Twitch;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Spotlight (spotlight design §3): off to start with nothing ticked, bad input refused
/// all at once, the sites ticked checked the way the Marketing tab checks a post, every change
/// audited, and turning it on or moving the time never makes a draft for a time already past.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SpotlightSettingsTests
{
    private const string Path = "/api/settings/spotlight";

    private readonly PostgresFixture _db;

    public SpotlightSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTestHost Host, string Cookie)> StartAsync()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        return (host, cookie);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task<List<string?>> ProblemsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return [.. (await ApiTestHost.BodyOf(response, Ct)).GetProperty("problems").EnumerateArray().Select(p => p.GetString())];
    }

    [Fact]
    public async Task ItIsOffToStart_OnMondayAtNineInRandomMode_WithNothingTicked()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        var body = await OkAsync(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct));

        Assert.False(body.GetProperty("on").GetBoolean());
        Assert.Equal(1, body.GetProperty("day").GetInt32());
        Assert.Equal(9, body.GetProperty("hour").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("timeZone").ValueKind);
        Assert.Equal("random", body.GetProperty("mode").GetString());

        var places = body.GetProperty("places");
        Assert.Equal(JsonValueKind.Null, places.GetProperty("discord").ValueKind);
        Assert.Equal(JsonValueKind.Null, places.GetProperty("vrChat").ValueKind);
        Assert.False(places.GetProperty("bluesky").GetBoolean());
    }

    [Fact]
    public async Task TheChoicesAreSaved_AndFieldsLeftOutKeepWhatIsStored()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        var saved = await OkAsync(await host.SendJsonAsync(
            HttpMethod.Put, Path, new { on = true, day = 5, hour = 18, timeZone = "Europe/London", mode = "staff" }, cookie, Ct));

        Assert.True(saved.GetProperty("on").GetBoolean());
        Assert.Equal(5, saved.GetProperty("day").GetInt32());
        Assert.Equal(18, saved.GetProperty("hour").GetInt32());
        Assert.Equal("Europe/London", saved.GetProperty("timeZone").GetString());
        Assert.Equal("staff", saved.GetProperty("mode").GetString());

        var later = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { hour = 7 }, cookie, Ct));

        Assert.True(later.GetProperty("on").GetBoolean());
        Assert.Equal(5, later.GetProperty("day").GetInt32());
        Assert.Equal(7, later.GetProperty("hour").GetInt32());
        Assert.Equal("staff", later.GetProperty("mode").GetString());

        var cleared = await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { timeZone = "" }, cookie, Ct));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("timeZone").ValueKind);
    }

    [Fact]
    public async Task BadInputIsRefusedAllAtOnce_AndNothingIsSaved()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        var problems = await ProblemsAsync(await host.SendJsonAsync(
            HttpMethod.Put, Path, new { on = true, day = 7, hour = 24, timeZone = "Mars/Olympus", mode = "dice" }, cookie, Ct));

        Assert.Contains("The day must be between Sunday and Saturday.", problems);
        Assert.Contains("The hour must be between 0 and 23.", problems);
        Assert.Contains("That time zone is not known.", problems);
        Assert.Contains("Pick who chooses the member: Modbot or staff.", problems);
        Assert.Equal(4, problems.Count);

        await using var context = _db.NewContext();
        Assert.False((await context.GetSettingsAsync(Ct)).SpotlightOn);
    }

    [Fact]
    public async Task ADiscordChannelNotInTheServerCannotBeTicked()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        var problems = await ProblemsAsync(await host.SendJsonAsync(
            HttpMethod.Put, Path, new { places = new { discord = new { channelId = "999" } } }, cookie, Ct));

        Assert.Contains("That channel is not in the Discord server.", problems);

        await using var context = _db.NewContext();
        Assert.False(TwitchPostPlaces.Parse((await context.GetSettingsAsync(Ct)).SpotlightPlaces).AnyTicked);
    }

    [Fact]
    public async Task BlueskyCannotBeTickedBeforeItIsSetUp()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        var problems = await ProblemsAsync(await host.SendJsonAsync(
            HttpMethod.Put, Path, new { places = new { bluesky = true } }, cookie, Ct));

        Assert.Contains("Bluesky is off.", problems);
    }

    [Fact]
    public async Task TheVRChatGroupCanBeTicked_AndEveryChangeIsAudited()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.ManagedGroupId = "grp_00000000-0000-0000-0000-000000000001";
            await context.SaveChangesAsync(Ct);
        }

        var body = await OkAsync(await host.SendJsonAsync(
            HttpMethod.Put, Path, new { on = true, places = new { vrChat = new { visibility = "public", notify = true } } }, cookie, Ct));

        var vrchat = body.GetProperty("places").GetProperty("vrChat");
        Assert.Equal("public", vrchat.GetProperty("visibility").GetString());
        Assert.True(vrchat.GetProperty("notify").GetBoolean());

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        var spotlight = facts.Select(ApiTestHost.DataOf).Where(d => d.GetProperty("setting").GetString() == "spotlight").ToList();

        Assert.Single(spotlight);
        Assert.True(spotlight[0].GetProperty("changed").GetProperty("spotlightOn").GetProperty("new").GetBoolean());
    }

    [Fact]
    public async Task SavingTheSameThingAgainChangesAndAuditsNothing()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { hour = 12 }, cookie, Ct));
        await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { hour = 12 }, cookie, Ct));

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        Assert.Single(facts.Select(ApiTestHost.DataOf), d => d.GetProperty("setting").GetString() == "spotlight");
    }

    [Fact]
    public async Task TurningItOnMakesNoDraftForATimeAlreadyPast()
    {
        var (host, cookie) = await StartAsync();
        await using var running = host;

        await OkAsync(await host.SendJsonAsync(HttpMethod.Put, Path, new { on = true }, cookie, Ct));

        await using var context = _db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);

        // The moment the settings name, at or before now, is marked as dealt with, so the first
        // draft is the next one; and nothing was made by saving.
        Assert.NotNull(settings.SpotlightHandledThrough);
        Assert.Empty(await context.SpotlightWeeks.ToListAsync(Ct));
        Assert.Empty(await context.Posts.Where(p => p.Kind == PostKinds.Spotlight).ToListAsync(Ct));
    }

    [Fact]
    public async Task SettingsNeedManageSettings()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, Path, new { }, cookie, Ct)).StatusCode);
    }
}
