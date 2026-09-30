using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Companion;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

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
public sealed class CompanionApiTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private CompanionApiTestHost(WebApplication app, HttpClient client, FakeClock clock)
    {
        _app = app;
        Client = client;
        Clock = clock;
    }

    public HttpClient Client { get; }

    public FakeClock Clock { get; }

    public IServiceProvider Services => _app.Services;

    /// <summary>The in-process server, for what <see cref="Client"/> cannot do -- opening a WebSocket.</summary>
    public TestServer Server => _app.GetTestServer();

    /// <summary>
    /// A store over its own scope.
    /// </summary>
    /// <remarks>
    /// The shipping store is scoped, because it holds a <c>ModbotContext</c>. Resolving one from
    /// the root provider and keeping it would share a change tracker across every test in the
    /// assembly, so each call gets its own -- which is also what a request does.
    /// </remarks>
    public ICompanionDeviceStore Devices => new DatabaseCompanionDeviceStore(NewContext());

    private ModbotContext NewContext()
        => Services.GetRequiredService<IServiceScopeFactory>().CreateScope()
            .ServiceProvider.GetRequiredService<ModbotContext>();

    /// <summary>
    /// The instant these tests pretend it is. Fixed, because the fact log is partitioned by month
    /// and a test that drifted into a month with no partition would fail for a reason that has
    /// nothing to do with what it was checking.
    /// </summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    /// <param name="configure">
    /// Last word on the container: a test substitutes the delay scheduler here, so a slowed
    /// pairing attempt is recorded rather than waited out.
    /// </param>
    public static async Task<CompanionApiTestHost> StartAsync(
        PostgresFixture db, Action<IServiceCollection>? configure = null)
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

        // What the shipping host's AddModbotVRChat gives the fact writer: the people a report
        // names count as seen the moment it is written, without the rest of that registration
        // (a gate, a limiter) that ingest never touches.
        builder.Services.AddScoped<ISightingRecorder, Modbot.VRChat.Users.FactSightings>();

        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseWebSockets();
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

        return new CompanionApiTestHost(app, client, clock);
    }

    /// <summary>Gives the deployment a managed group, without which nothing can pair.</summary>
    public async Task ConfigureGroupAsync(PostgresFixture db, string groupId, CancellationToken ct)
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(ct);
        settings.ManagedGroupId = groupId;
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Pairs a device directly, skipping the code exchange the pairing tests cover. Its owner is a
    /// fresh account holding "Pair a companion", because a device is refused without one.
    /// </summary>
    public async Task<string> PairDeviceAsync(CancellationToken ct)
    {
        var owner = await CreateOwnerAsync(ModbotPermissions.PairCompanion, ct);
        var (token, _) = await PairDeviceToAsync(owner.Id, ct);
        return token;
    }

    /// <summary>An account for a device to belong to, holding exactly these permissions.</summary>
    public Task<ModbotUser> CreateOwnerAsync(ModbotPermissions permissions, CancellationToken ct)
        => TestAccounts.CreateAsync(
            NewContext(), $"owner_{Guid.NewGuid():N}", TestAccounts.Password, permissions, linked: true, ct);

    /// <summary>
    /// Pairs a device to this account directly. <paramref name="lastSeenAt"/> and
    /// <paramref name="issuedAt"/> let a test make a device that has sat unused.
    /// </summary>
    public async Task<(string Token, Guid DeviceId)> PairDeviceToAsync(
        Guid ownerId,
        CancellationToken ct,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? lastSeenAt = null)
    {
        var token = DeviceTokens.NewToken();
        var deviceId = Guid.NewGuid();

        await Devices.AddDeviceAsync(
            new CompanionDevice(
                deviceId,
                DeviceTokens.Hash(token),
                "2026.9.0",
                "windows",
                ownerId,
                issuedAt ?? Clock.UtcNow,
                lastSeenAt),
            ct);

        return (token, deviceId);
    }

    /// <summary>
    /// Pairs a device to a staff account linked to a VRChat account, the way a real moderator's
    /// client is paired. Facts that device reports about that VRChat account are the moderator's
    /// own, which is what starts a watch.
    /// </summary>
    public async Task<(string Token, Guid DeviceId, string VRChatUserId)> PairModeratorAsync(CancellationToken ct)
    {
        var moderator = await CreateOwnerAsync(ModbotPermissions.PairCompanion, ct);
        var (token, deviceId) = await PairDeviceToAsync(moderator.Id, ct);

        return (token, deviceId, moderator.VRChatUserId!);
    }

    /// <summary>A fresh context of its own, for a test that changes an account directly.</summary>
    public ModbotContext Database => NewContext();

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
        await context.CompanionDevices.ExecuteDeleteAsync(ct);
        await context.CompanionPairingCodes.ExecuteDeleteAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
