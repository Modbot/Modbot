using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Discord → Commands (Discord commands design §3.8): one switch per command, the
/// defaults a missing name takes, and only what differs from a default being stored. This holds what
/// the "Members can use /me" test held.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordCommandsSettingsTests
{
    private const string Path = "/api/settings/discord-commands";

    private readonly PostgresFixture _db;

    public DiscordCommandsSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool IsOn(JsonElement body, string name)
        => body.GetProperty("commands").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == name)
            .GetProperty("on").GetBoolean();

    private static object Choose(params (string Name, bool On)[] choices)
        => new { commands = choices.ToDictionary(c => c.Name, c => c.On) };

    private async Task<string> StoredAsync(ApiTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, Ct))?.DiscordCommands
            ?? DiscordCommandSwitches.Empty;
    }

    [Fact]
    public async Task WithoutChangeSettings_ItIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, Path, Choose(("me", true)), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task EveryCommandIsListed_WithItsDefault_MeBeingOffAndStaffCommandsOn()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);

        var listed = body.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(DiscordCommandSwitches.All.Select(c => c.Name), listed);

        Assert.False(IsOn(body, "me"));
        Assert.True(IsOn(body, "lookup"));
        Assert.True(IsOn(body, "recent"));
        Assert.True(IsOn(body, "Look up in Modbot"));
    }

    [Fact]
    public async Task TurningMeOn_IsSaved_RecordedAndStoredSparsely()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var saved = await host.SendJsonAsync(HttpMethod.Put, Path, Choose(("me", true), ("lookup", true)), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.True(IsOn(after, "me"));

        // Only the one that differs from its default is stored: lookup was sent as on, which it is.
        Assert.Equal("{\"me\":true}", await StoredAsync(host));

        var entry = (await host.FactsAsync(FactType.SettingsChanged, "settings", Ct))
            .Select(ApiTestHost.DataOf)
            .Last(data => data.GetProperty("setting").GetString() == "discordCommands");
        var changed = entry.GetProperty("changed");
        Assert.False(changed.GetProperty("me").GetProperty("old").GetBoolean());
        Assert.True(changed.GetProperty("me").GetProperty("new").GetBoolean());
        Assert.False(changed.TryGetProperty("lookup", out _));

        // A save that leaves out a command keeps what it has.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, Choose(("recent", false)), cookie, Ct)).StatusCode);
        var kept = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.True(IsOn(kept, "me"));
        Assert.False(IsOn(kept, "recent"));

        // Put back for the next test, which shares the settings row.
        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, Path, Choose(("me", false), ("recent", true)), cookie, Ct)).StatusCode);
        Assert.Equal("{}", await StoredAsync(host));
    }

    [Fact]
    public async Task ANameThatIsNotACommand_IsRefused_AndNothingIsSaved()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Path, Choose(("me", true), ("nonsense", true)), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nonsense", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal("{}", await StoredAsync(host));
    }

    [Fact]
    public async Task InADemo_NothingCanBeTurnedOn_AndEverythingReadsAsOff()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var demo = new DemoMode { Requested = true };
        demo.Decide(hasStaffAccount: false, onboardingComplete: false, holdsDemoData: true);

        await using var host = await ApiTestHost.StartAsync(_db, configure: services => services.AddSingleton(demo));
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, Path, Choose(("me", true)), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("demo", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal("{}", await StoredAsync(host));

        var body = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct), Ct);
        Assert.All(body.GetProperty("commands").EnumerateArray(), c => Assert.False(c.GetProperty("on").GetBoolean()));
    }
}
