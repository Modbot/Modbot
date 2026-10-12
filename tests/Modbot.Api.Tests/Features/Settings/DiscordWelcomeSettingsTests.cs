using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Discord → Welcome steps: the rules and default steps round-trip in order, a bad text,
/// link, count or missing channel is refused, and the start time follows the switch.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordWelcomeSettingsTests
{
    private const string Path = "/api/settings/discord-welcome";

    private readonly PostgresFixture _db;

    public DiscordWelcomeSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Rule(string roleId = "801", string text = "Read the rules", string? link = "https://example.com/rules")
        => new { roleId, text, link };

    private static object Step(string text = "Say hello", string? link = null)
        => new { text, link };

    private static object Update(
        bool enabled = false,
        string via = "dm",
        string? channelId = null,
        IEnumerable<object>? rules = null,
        IEnumerable<object>? defaults = null)
        => new { enabled, via, channelId, rules = (rules ?? []).ToList(), defaults = (defaults ?? []).ToList() };

    [Fact]
    public async Task WithoutChangeSettings_ItIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, Path, Update(), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task RulesAndDefaults_ComeBackInTheOrderTheyWereSent()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(
            HttpMethod.Put,
            Path,
            Update(
                enabled: true,
                via: "both",
                channelId: "555",
                rules: [Rule("801", "First"), Rule("802", "Second", null), Rule("803", "Third")],
                defaults: [Step("Default one", "https://example.com/one"), Step("Default two")]),
            cookie,
            Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal("both", body.GetProperty("via").GetString());
        Assert.Equal("555", body.GetProperty("channelId").GetString());

        var rules = body.GetProperty("rules");
        Assert.Equal(3, rules.GetArrayLength());
        Assert.Equal("801", rules[0].GetProperty("roleId").GetString());
        Assert.Equal("First", rules[0].GetProperty("text").GetString());
        Assert.Equal("https://example.com/rules", rules[0].GetProperty("link").GetString());
        Assert.Equal("802", rules[1].GetProperty("roleId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, rules[1].GetProperty("link").ValueKind);
        Assert.Equal("803", rules[2].GetProperty("roleId").GetString());

        var defaults = body.GetProperty("defaults");
        Assert.Equal(2, defaults.GetArrayLength());
        Assert.Equal("Default one", defaults[0].GetProperty("text").GetString());
        Assert.Equal("https://example.com/one", defaults[0].GetProperty("link").GetString());
        Assert.Equal("Default two", defaults[1].GetProperty("text").GetString());

        // A second save replaces every step.
        await host.SendJsonAsync(HttpMethod.Put, Path, Update(rules: [Rule("900", "Only")]), cookie, Ct);
        body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.Equal(1, body.GetProperty("rules").GetArrayLength());
        Assert.Equal("900", body.GetProperty("rules")[0].GetProperty("roleId").GetString());
        Assert.Equal(0, body.GetProperty("defaults").GetArrayLength());
    }

    [Theory]
    [InlineData("http://example.com/rules")]
    [InlineData("example.com/rules")]
    [InlineData("javascript:alert(1)")]
    public async Task ALinkThatIsNotHttps_IsRefused(string link)
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var inRule = await host.SendJsonAsync(HttpMethod.Put, Path, Update(rules: [Rule(link: link)]), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, inRule.StatusCode);

        var inDefault = await host.SendJsonAsync(HttpMethod.Put, Path, Update(defaults: [Step(link: link)]), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, inDefault.StatusCode);
    }

    [Fact]
    public async Task TextOverEightyCharacters_OrWithANewline_IsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var eighty = new string('a', 80);
        var ok = await host.SendJsonAsync(HttpMethod.Put, Path, Update(rules: [Rule(text: eighty)]), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var tooLong = await host.SendJsonAsync(HttpMethod.Put, Path, Update(rules: [Rule(text: eighty + "a")]), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var twoLines = await host.SendJsonAsync(HttpMethod.Put, Path, Update(defaults: [Step("one\ntwo")]), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, twoLines.StatusCode);

        var empty = await host.SendJsonAsync(HttpMethod.Put, Path, Update(defaults: [Step("   ")]), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task MoreThan25Rules_OrThreeDefaults_IsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var twentyFive = Enumerable.Range(1, 25).Select(i => Rule(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var ok = await host.SendJsonAsync(HttpMethod.Put, Path, Update(rules: twentyFive), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var twentySix = Enumerable.Range(1, 26).Select(i => Rule(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var tooManyRules = await host.SendJsonAsync(HttpMethod.Put, Path, Update(rules: twentySix), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyRules.StatusCode);

        var three = await host.SendJsonAsync(HttpMethod.Put, Path, Update(defaults: [Step("a"), Step("b"), Step("c")]), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, three.StatusCode);

        var four = await host.SendJsonAsync(HttpMethod.Put, Path, Update(defaults: [Step("a"), Step("b"), Step("c"), Step("d")]), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, four.StatusCode);
    }

    [Fact]
    public async Task SwitchingOnWithNoSteps_IsRefused_ButSavingItOffIsNot()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var on = await host.SendJsonAsync(HttpMethod.Put, Path, Update(enabled: true), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, on.StatusCode);

        var off = await host.SendJsonAsync(HttpMethod.Put, Path, Update(enabled: false), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("both")]
    public async Task TheChannelIsRequired_WhenTheWelcomeGoesToAChannel(string via)
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var without = await host.SendJsonAsync(
            HttpMethod.Put, Path, Update(via: via, defaults: [Step()]), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);

        var with = await host.SendJsonAsync(
            HttpMethod.Put, Path, Update(via: via, channelId: "555", defaults: [Step()]), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
    }

    [Fact]
    public async Task ADirectMessageNeedsNoChannel_AndAnUnknownWayIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var dm = await host.SendJsonAsync(HttpMethod.Put, Path, Update(via: "dm", defaults: [Step()]), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, dm.StatusCode);

        var unknown = await host.SendJsonAsync(HttpMethod.Put, Path, Update(via: "carrier-pigeon", defaults: [Step()]), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task TheStartTimeIsSetWhenSwitchedOn_KeptWhileOn_AndClearedWhenSwitchedOff()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        async Task<DateTimeOffset?> StartedAtAsync()
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            return (await db.GetSettingsAsync(Ct)).DiscordWelcomeStartedAt;
        }

        Assert.Null(await StartedAtAsync());

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, Update(enabled: true, defaults: [Step()]), cookie, Ct)).StatusCode);
        var started = await StartedAtAsync();
        Assert.NotNull(started);

        // Saving again while it stays on does not move the start.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, Update(enabled: true, defaults: [Step("Changed")]), cookie, Ct)).StatusCode);
        Assert.Equal(started, await StartedAtAsync());

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, Update(enabled: false, defaults: [Step()]), cookie, Ct)).StatusCode);
        Assert.Null(await StartedAtAsync());
    }
}
