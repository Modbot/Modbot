using System.Net;
using System.Net.Http.Json;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Auto-invites: its own permission, off by default, and a five-minute floor the screen
/// cannot talk its way under (auto-invites design §4.1, §9).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AutoInviteSettingsTests(PostgresFixture db)
{
    private const string Path = "/api/settings/auto-invites";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A PUT that was accepted, with the view it answered.</summary>
    private static async Task<AutoInviteView> SaveAsync(
        ReadSurfaceTestHost host, object body, string cookie)
    {
        var response = await host.PutJsonAsync(Path, body, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var view = await response.Content.ReadFromJsonAsync<AutoInviteView>(Ct);
        Assert.NotNull(view);
        return view;
    }

    private static object Body(int minutes = 5, int againAfterDays = 30, object? rules = null) => new
    {
        enabled = true,
        minutesInInstance = minutes,
        inviteAgainAfterDays = againAfterDays,
        rules = rules ?? new { kind = "allOf", rules = Array.Empty<object>() },
    };

    [Fact]
    public async Task ChangeSettingsIsNotEnough_ItHasItsOwnPermission()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Path, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync(Path, Body(), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task ItIsOffToStartWith()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageAutoInvites, Ct);
        var view = await host.GetJsonAsync<AutoInviteView>(Path, cookie, Ct);

        Assert.False(view.Enabled);
        Assert.Equal(5, view.MinutesInInstance);
        Assert.Equal(5, view.MinimumMinutesInInstance);
        Assert.Equal(30, view.InviteAgainAfterDays);
        Assert.Equal(0, view.InvitesSent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task FewerThanFiveMinutesIsRefused(int minutes)
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageAutoInvites, Ct);
        var response = await host.PutJsonAsync(Path, Body(minutes: minutes), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Refused, not quietly raised: a screen that accepted 2 and stored 5 would be lying.
        var view = await host.GetJsonAsync<AutoInviteView>(Path, cookie, Ct);
        Assert.False(view.Enabled);
        Assert.Equal(5, view.MinutesInInstance);
    }

    [Fact]
    public async Task FiveMinutesAndMoreAreAccepted()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageAutoInvites, Ct);

        var view = await SaveAsync(host, Body(minutes: 20), cookie);

        Assert.True(view.Enabled);
        Assert.Equal(20, view.MinutesInInstance);
    }

    [Fact]
    public async Task ARuleTreeTheServerCannotReadIsRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageAutoInvites, Ct);

        var response = await host.PutJsonAsync(
            Path,
            Body(rules: new { kind = "hasNiceHair" }),
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ATrustRankRuleIsSavedAndReadBack()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageAutoInvites, Ct);

        var rules = new
        {
            kind = "allOf",
            rules = new object[]
            {
                new { kind = "trustRankAtLeast", id = "TrustedUser" },
                new { kind = "age18Plus" },
            },
        };

        var view = await SaveAsync(host, Body(rules: rules), cookie);

        Assert.Equal("allOf", view.Rules["kind"]!.GetValue<string>());
        Assert.Contains("trustRankAtLeast", view.Rules.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("TrustedUser", view.Rules.ToJsonString(), StringComparison.Ordinal);
    }
}
