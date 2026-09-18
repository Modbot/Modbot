using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Cloud;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// The one checkbox on the two screens where somebody makes their own account (server info and
/// account email design §6).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class UpdatesOptInTests
{
    private readonly PostgresFixture _db;

    public UpdatesOptInTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fresh deployment whose subscriber is the fake, so nothing reaches a network.</summary>
    private async Task<(ApiTestHost Host, FakeUpdatesSubscriber Cloud)> FreshAsync(
        bool available = true, bool throws = false)
    {
        var cloud = new FakeUpdatesSubscriber { Available = available, Throws = throws };

        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var host = await ApiTestHost.StartAsync(
            _db,
            gate: null,
            configure: services => services.AddScoped<IUpdatesSubscriber>(_ => cloud));

        return (host, cloud);
    }

    private static object Administrator(bool subscribe) => new
    {
        username = "bin",
        password = "a-long-enough-password",
        confirmPassword = "a-long-enough-password",
        email = "Bin@Example.com",
        subscribeToUpdates = subscribe,
    };

    [Fact]
    public async Task TickedAndCloudOn_TheAddressGoesToCloud()
    {
        var (host, cloud) = await FreshAsync();
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/administrator", Administrator(subscribe: true), cookie: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Lower-cased: what Cloud is given is the stored form, not what was typed.
        Assert.Equal(["bin@example.com"], cloud.Asked);
    }

    [Fact]
    public async Task Unticked_NothingGoesAnywhere()
    {
        var (host, cloud) = await FreshAsync();
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/administrator", Administrator(subscribe: false), cookie: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(cloud.Asked);
    }

    [Fact]
    public async Task CloudOff_NothingGoesAnywhereEvenIfTheRequestSaysItWasTicked()
    {
        var (host, cloud) = await FreshAsync(available: false);
        await using var _host = host;

        // The browser hides the checkbox, but the request is a request and anyone can send one.
        var response = await host.PostAsync(
            "/api/onboarding/administrator", Administrator(subscribe: true), cookie: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(cloud.Asked);
    }

    [Fact]
    public async Task CloudOff_TheStatusTellsTheBrowserNotToShowTheCheckbox()
    {
        var (host, _) = await FreshAsync(available: false);
        await using var _host = host;

        var status = await (await host.GetAsync("/api/onboarding/status", cookie: null, Ct))
            .ReadJsonAsync(Ct);

        Assert.False(status.GetProperty("canSubscribeToUpdates").GetBoolean());
    }

    [Fact]
    public async Task CloudOn_TheStatusSaysTheCheckboxCanBeShown()
    {
        var (host, _) = await FreshAsync();
        await using var _host = host;

        var status = await (await host.GetAsync("/api/onboarding/status", cookie: null, Ct))
            .ReadJsonAsync(Ct);

        Assert.True(status.GetProperty("canSubscribeToUpdates").GetBoolean());
    }

    [Fact]
    public async Task ACloudThatIsNotThere_DoesNotCostAnybodyTheirAccount()
    {
        var (host, cloud) = await FreshAsync(throws: true);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/administrator", Administrator(subscribe: true), cookie: null, Ct);

        // The account is made, the person is signed in, and the failure is the log's business.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        Assert.Equal("bin", body.GetProperty("username").GetString());

        var cookie = ApiTestHost.SessionCookie(response);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/auth/me", cookie, Ct)).StatusCode);

        Assert.Equal(["bin@example.com"], cloud.Asked);
    }

    [Fact]
    public async Task WhatThePersonAgreedToIsRecorded_WithoutTheAddress()
    {
        var (host, _) = await FreshAsync();
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/administrator", Administrator(subscribe: true), cookie: null, Ct);

        var id = (await response.ReadJsonAsync(Ct)).GetProperty("id").GetString()!;

        var fact = Assert.Single(await host.FactsAsync(FactType.UpdatesSubscribed, id, Ct));

        // The account already holds the address, and the audit log is read by more people than
        // the account page is.
        Assert.DoesNotContain("bin@example.com", fact.Data, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotTicking_LeavesNoFact()
    {
        var (host, _) = await FreshAsync();
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/administrator", Administrator(subscribe: false), cookie: null, Ct);

        var id = (await response.ReadJsonAsync(Ct)).GetProperty("id").GetString()!;

        Assert.Empty(await host.FactsAsync(FactType.UpdatesSubscribed, id, Ct));
    }

    [Fact]
    public async Task AnInviteOffersTheSameCheckbox()
    {
        var (host, cloud) = await FreshAsync();
        await using var _host = host;

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var invite = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(
                HttpMethod.Post, "/api/invites", new { roleIds = new[] { BuiltInRoles.ViewerId } }, cookie, Ct),
            Ct);

        var path = "/api" + invite.GetProperty("path").GetString()!;

        var view = await ApiTestHost.BodyOf(await host.Client.GetAsync(path, Ct), Ct);
        Assert.True(view.GetProperty("canSubscribeToUpdates").GetBoolean());

        var accepted = await host.SendJsonAsync(
            HttpMethod.Post,
            path,
            new
            {
                username = $"u_{Guid.NewGuid():N}",
                password = "a-long-enough-password",
                email = "Joiner@Example.com",
                subscribeToUpdates = true,
            },
            null,
            Ct);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(["joiner@example.com"], cloud.Asked);
    }
}
