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

    /// <summary>
    /// A store over its own scope.
    /// </summary>
    /// <remarks>
    /// The shipping store is scoped, because it holds a <c>ModbotContext</c>. Resolving one from
    /// the root provider and keeping it would share a change tracker across every test in the
    /// assembly, so each call gets its own -- which is also what a request does.
    /// </remarks>
    public IClientDeviceStore Devices => new DatabaseClientDeviceStore(NewContext());

    private ModbotContext NewContext()
        => Services.GetRequiredService<IServiceScopeFactory>().CreateScope()
            .ServiceProvider.GetRequiredService<ModbotContext>();

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
        builder.Logging.QuietForTests();

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
    public async Task<string> PairDeviceAsync(CancellationToken ct)
    {
        var token = DeviceTokens.NewToken();

        await Devices.AddDeviceAsync(
            new ClientDevice(
                Guid.NewGuid(),
                DeviceTokens.Hash(token),
                "2026.9.0",
                "windows",
                Guid.NewGuid(),
                Clock.UtcNow),
            ct);

        return token;
    }

    /// <summary>
    /// Pairs a device to a staff account linked to a VRChat account, the way a real moderator's
    /// client is paired. Facts that device reports about that VRChat account are the moderator's
    /// own, which is what starts a watch.
    /// </summary>
    public async Task<(string Token, Guid DeviceId, string VRChatUserId)> PairModeratorAsync(CancellationToken ct)
    {
        var moderator = await TestAccounts.CreateAsync(
            NewContext(), $"mod_{Guid.NewGuid():N}", TestAccounts.Password, ModbotPermissions.None, linked: true, ct);

        var token = DeviceTokens.NewToken();
        var deviceId = Guid.NewGuid();

        await Devices.AddDeviceAsync(
            new ClientDevice(deviceId, DeviceTokens.Hash(token), "2026.9.0", "windows", moderator.Id, Clock.UtcNow),
            ct);

        return (token, deviceId, moderator.VRChatUserId!);
    }

    public HttpRequestMessage WithToken(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Add("Authorization", $"Bearer {token}");

        return request;
    }

    /// <summary>
    /// Puts the deployment back to "nothing has ever been paired and nothing has been reported".
    /// </summary>
    /// <remarks>
    /// Devices and pairing codes live in real tables now rather than in memory per host, so they
    /// outlive a test the way they outlive a deploy. Without this, a test asserting on "the device
    /// that was just paired" sees every device every earlier test paired, and a test redeeming a
    /// fixed code finds it already spent -- both of which are the durable store working, not
    /// failing.
    /// </remarks>
    public static async Task ResetAsync(PostgresFixture db, CancellationToken ct)
    {
        await using var context = db.NewContext();
        await context.Events.ExecuteDeleteAsync(ct);
        await context.ClientDevices.ExecuteDeleteAsync(ct);
        await context.ClientPairingCodes.ExecuteDeleteAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
