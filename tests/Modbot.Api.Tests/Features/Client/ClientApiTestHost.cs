using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Client;
using Modbot.Api.Features.Client.Devices;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Client;

/// <summary>
/// A real pipeline with the client endpoints mapped, over the real database.
/// </summary>
/// <remarks>
/// <para>Its own host rather than the shared one, because <c>MapClientApi</c> is not yet wired
/// into <c>ApiSurface</c> — the feature is complete and the one line that turns it on belongs to
/// whoever owns that file.</para>
/// <para>Real PostgreSQL, because deduplication is the load-bearing property of ingest and it is
/// implemented with an advisory lock and a range check over a partitioned table. A substitute
/// provider implements none of that, so a test against one would pass while proving nothing about
/// the thing most likely to be silently wrong.</para>
/// </remarks>
public sealed class ClientApiTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ClientApiTestHost(WebApplication app, HttpClient client, FakeClock clock)
    {
        _app = app;
        Client = client;
        Clock = clock;
    }

    public HttpClient Client { get; }

    public FakeClock Clock { get; }

    public IServiceProvider Services => _app.Services;

    public IClientDeviceStore Devices => Services.GetRequiredService<IClientDeviceStore>();

    /// <summary>
    /// The instant these tests pretend it is. Fixed, because the fact log is partitioned by month
    /// and a test that drifted into a month with no partition would fail for a reason that has
    /// nothing to do with what it was checking.
    /// </summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    public static async Task<ClientApiTestHost> StartAsync(PostgresFixture db)
    {
        ArgumentNullException.ThrowIfNull(db);

        var clock = new FakeClock(Now);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddDbContext<ModbotContext>(o => o.UseNpgsql(db.ConnectionString));
        builder.Services.AddSingleton<IModbotClock>(clock);
        builder.Services.AddModbotAnalytics();
        builder.Services.AddModbotAuth();
        builder.Services.AddClientApi();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapClientApi();

        await app.StartAsync();

        // The fact log is a partitioned table and EF cannot express declarative partitioning, so
        // the months these tests write into have to exist before anything inserts.
        using (var scope = app.Services.CreateScope())
        {
            await new EventPartitionMaintainer(
                scope.ServiceProvider.GetRequiredService<ModbotContext>(), clock).EnsureAsync();
        }

        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost/");

        return new ClientApiTestHost(app, client, clock);
    }

    /// <summary>Gives the deployment a managed group, without which nothing can pair.</summary>
    public async Task ConfigureGroupAsync(PostgresFixture db, string groupId, CancellationToken ct)
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(ct);
        settings.ManagedGroupId = groupId;
        await context.SaveChangesAsync(ct);
    }

    /// <summary>Pairs a device directly, skipping the code exchange the pairing tests cover.</summary>
    public async Task<string> PairDeviceAsync(string deviceName, CancellationToken ct)
    {
        var token = DeviceTokens.NewToken();

        await Devices.AddDeviceAsync(
            new ClientDevice(
                Guid.NewGuid(),
                DeviceTokens.Hash(token),
                deviceName,
                "2026.9.0",
                "windows",
                Guid.NewGuid(),
                Clock.UtcNow),
            ct);

        return token;
    }

    public HttpRequestMessage WithToken(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Add("Authorization", $"Bearer {token}");

        return request;
    }

    /// <summary>Clears the fact log so one test's presence does not become another's roster.</summary>
    public static async Task ClearFactsAsync(PostgresFixture db, CancellationToken ct)
    {
        await using var context = db.NewContext();
        await context.Events.ExecuteDeleteAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
