using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modbot.Api.Auth;
using Modbot.Api.Features.Auth.Me;
using Modbot.Api.Features.Demo;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.Demo.Tests;

/// <summary>
/// The path that serves everyone as an administrator, and how firmly it is shut.
/// </summary>
/// <remarks>
/// This is the one that matters. If demo mode could ever be on over a deployment that holds real
/// data, whoever opened the URL would hold every permission Modbot has — so the test is not "does
/// the demo work", it is "is there any way at all to reach the administrator without both the
/// variable and an untouched database" (demo mode design §3).
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class DemoAdministratorPathTests
{
    private readonly PostgresFixture _fixture;

    public DemoAdministratorPathTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task InADemoEveryVisitorIsTheAdministrator()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await DemoSeedHost.StartAsync(_fixture, ct);
        await seed.Seeder().SeedCoreAsync(ct);

        await using var host = await StartAsync(Demo(on: true));

        var response = await host.Client.GetAsync("/api/auth/me", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Contains(DemoMode.AdministratorUsername, body, StringComparison.Ordinal);
        Assert.Contains(nameof(ModbotPermissions.Administrator), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheVariableNobodyIsAnybody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await DemoSeedHost.StartAsync(_fixture, ct);

        // Seeded data present, variable absent: still no session. The data is not the permission.
        await seed.Seeder().SeedCoreAsync(ct);

        await using var host = await StartAsync(Demo(on: false));

        var response = await host.Client.GetAsync("/api/auth/me", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnUndecidedDemoIsNotADemo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await DemoSeedHost.StartAsync(_fixture, ct);
        await seed.Seeder().SeedCoreAsync(ct);

        // MODBOT_DEMO is set but startup never decided. The claim is refused rather than assumed.
        await using var host = await StartAsync(new DemoMode { Requested = true });

        var response = await host.Client.GetAsync("/api/auth/me", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheDemoStatusSaysNoOnAnOrdinaryDeployment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await DemoSeedHost.StartAsync(_fixture, ct);

        await using var host = await StartAsync(Demo(on: false));

        var body = await host.Client.GetStringAsync("/api/demo", ct);

        Assert.Contains("\"on\":false", body.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResetIsNotThereOnAnOrdinaryDeployment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await DemoSeedHost.StartAsync(_fixture, ct);

        await using var host = await StartAsync(Demo(on: false));

        var response = await host.Client.PostAsync("/api/demo/reset", content: null, ct);

        // Unauthorised, because there is no session to make the request with. Never "done".
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SigningInIsTurnedAwayInADemo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await DemoSeedHost.StartAsync(_fixture, ct);
        await seed.Seeder().SeedCoreAsync(ct);

        await using var host = await StartAsync(Demo(on: true));

        var response = await host.Client.PostAsync("/api/auth/login", content: null, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            DemoRefusals.Message,
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }

    private static DemoMode Demo(bool on)
    {
        var demo = new DemoMode { Requested = on };
        demo.Decide(hasStaffAccount: false, onboardingComplete: false, holdsDemoData: false);
        return demo;
    }

    /// <summary>
    /// The smallest host that can answer "who am I": the auth stack, the demo endpoints and the
    /// refusals, over the real database.
    /// </summary>
    private async Task<Host> StartAsync(DemoMode demo)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        // A request per assertion, each narrating four lines plus its SQL, is a test run nobody
        // can read the result of.
        builder.Logging.QuietForTests();

        builder.Services.AddDbContext<ModbotContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        builder.Services.AddSingleton<IModbotClock>(new FakeClock());
        builder.Services.AddSingleton(demo);
        builder.Services.AddSingleton<DemoState>();

        builder.Services.AddSingleton<ISecretProtector>(services =>
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            return AesGcmSecretProtector.CreateAsync(db).GetAwaiter().GetResult();
        });

        builder.Services.AddModbotAuth();

        var app = builder.Build();

        app.UseDemoRefusals();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapMe();
        app.MapDemo();

        // Stands in for the real sign-in endpoint, which lives behind AddModbotApi. What is being
        // tested is that the refusal happens before anything here runs.
        app.MapPost("/api/auth/login", () => Results.Ok("signed in")).AllowAnonymous();

        await app.StartAsync();

        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost/");

        return new Host(app, client);
    }

    private sealed record Host(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
