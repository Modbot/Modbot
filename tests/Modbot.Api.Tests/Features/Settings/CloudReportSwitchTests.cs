using System.Net;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Server → This install → "Send usage report to Modbot Cloud": on when Modbot is
/// installed, saved at once, recorded in the audit log, and out of reach without Change settings.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CloudReportSwitchTests
{
    private const string Path = "/api/settings/cloud";

    private readonly PostgresFixture _db;

    public CloudReportSwitchTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<System.Text.Json.JsonElement> GetAsync(ApiTestHost host, string cookie)
    {
        var response = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    [Fact]
    public async Task TheSwitchStartsOn()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = await GetAsync(host, cookie);

        Assert.True(body.GetProperty("reportOn").GetBoolean());
    }

    [Fact]
    public async Task TheSwitchSavesAndComesBackOn()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var off = await host.SendJsonAsync(HttpMethod.Put, Path, new { reportOn = false }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(off, Ct)).GetProperty("reportOn").GetBoolean());

        await using (var context = _db.NewContext())
            Assert.False((await context.GetSettingsAsync(Ct)).SendUsageReport);

        var again = await GetAsync(host, cookie);
        Assert.False(again.GetProperty("reportOn").GetBoolean());

        // A switch, not a one-way door.
        var on = await host.SendJsonAsync(HttpMethod.Put, Path, new { reportOn = true }, cookie, Ct);
        Assert.True((await ApiTestHost.BodyOf(on, Ct)).GetProperty("reportOn").GetBoolean());
    }

    [Fact]
    public async Task TurningTheSwitchIsRecordedInTheAuditLog_AndPressingItAgainIsNot()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await host.SendJsonAsync(HttpMethod.Put, Path, new { reportOn = false }, cookie, Ct);
        await host.SendJsonAsync(HttpMethod.Put, Path, new { reportOn = false }, cookie, Ct);

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);
        var payload = ApiTestHost.DataOf(Assert.Single(facts));

        Assert.Equal("sendUsageReport", payload.GetProperty("setting").GetString());
        Assert.True(payload.GetProperty("before").GetBoolean());
        Assert.False(payload.GetProperty("after").GetBoolean());
    }

    [Fact]
    public async Task TheSwitchNeedsChangeSettings()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var read = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);
        var write = await host.SendJsonAsync(HttpMethod.Put, Path, new { reportOn = false }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    /// <summary>
    /// What the operator confirms before AI goes on has to name everything the code sends
    /// (privacy policy, "Is any of that sent anywhere else?").
    /// </summary>
    [Fact]
    public void TheAiConfirmationListNamesEveryKindOfThingSent()
    {
        var features = AiSettingsEndpoints.AiSends.Select(s => s.Feature).ToList();

        Assert.Contains(features, f => f.StartsWith("Moderation rules", StringComparison.Ordinal));
        Assert.Contains(features, f => f.StartsWith("Conversation", StringComparison.Ordinal));
        Assert.Contains(features, f => f.StartsWith("Pictures", StringComparison.Ordinal));
        Assert.Contains(features, f => f.StartsWith("Opinion on a flag", StringComparison.Ordinal));
        Assert.Contains(features, f => f.StartsWith("Alerts", StringComparison.Ordinal));
        Assert.Contains(features, f => f.StartsWith("Insights", StringComparison.Ordinal));
        Assert.Contains(features, f => f.StartsWith("Chat", StringComparison.Ordinal));
        Assert.Contains(features, f => f.StartsWith("Test", StringComparison.Ordinal));

        var pictures = AiSettingsEndpoints.AiSends.Single(s => s.Feature.StartsWith("Pictures", StringComparison.Ordinal));

        // All six kinds of picture the moderation engine can hand over.
        Assert.Contains("avatar", pictures.Text, StringComparison.Ordinal);
        Assert.Contains("profile picture", pictures.Text, StringComparison.Ordinal);
        Assert.Contains("user icon", pictures.Text, StringComparison.Ordinal);
        Assert.Contains("profile banner", pictures.Text, StringComparison.Ordinal);
        Assert.Contains("message's pictures", pictures.Text, StringComparison.Ordinal);
    }
}
