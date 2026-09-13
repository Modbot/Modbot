using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Time;
using Modbot.TestSupport;
using Modbot.VRChat.Pacing;
using Modbot.VRChat.Scheduling;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// Spec 4.2.1's rates reaching a producer that is already running.
/// </summary>
/// <remarks>
/// <para>
/// The producers are long-lived <c>BackgroundService</c>s, so "operator-configurable" is a claim
/// about a running process rather than about a startup argument. A setting that silently needed a
/// restart would be worse than one that said it could not be changed, because the operator who
/// changes it is usually the one who cannot afford to be wrong about whether it took.
/// </para>
/// <para>
/// The loops are stepped one tick at a time through <see cref="GatedDelayScheduler"/>. The
/// alternative — letting the loop run and sleeping in the test — would assert on whichever
/// iteration happened to be in flight.
/// </para>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class PacingReconfigurationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private IsolatedDatabase _database = null!;
    private ServiceProvider _services = null!;
    private FakeClock _clock = null!;
    private VRChatGate _gate = null!;

    public PacingReconfigurationTests(PostgresFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await IsolatedDatabase.CreateAsync(_fixture, Ct);
        _clock = new FakeClock();

        // Never called: no managed group is configured, so every pass reports NotConfigured
        // before it reaches the gate. It exists because the producers refuse to be built without
        // one, which is the right refusal.
        _gate = new VRChatGate(
            new FakeClientFactory(new FakeVRChat().AlwaysSignedInAs().Client),
            new FakeConnectionStore(),
            new LimiterHarness(LimiterHarness.Unpaced(), _clock).Limiter,
            _clock,
            new FakeMonotonicClock());

        var services = new ServiceCollection();

        services.AddDbContext<ModbotContext>(o => o.UseNpgsql(_database.ConnectionString));
        services.AddSingleton<IModbotClock>(_clock);
        services.AddSingleton<IVRChatGate>(_gate);
        services.AddSingleton(new SyncDiagnostics(_clock));
        services.AddScoped<IFactWriter>(p => new FactWriter(p.GetRequiredService<ModbotContext>(), _clock));
        services.AddScoped(p => new EventPartitionMaintainer(p.GetRequiredService<ModbotContext>(), _clock));

        services.AddScoped(p => new GroupAuditLogSync(
            p.GetRequiredService<IVRChatGate>(),
            p.GetRequiredService<IFactWriter>(),
            p.GetRequiredService<EventPartitionMaintainer>(),
            p.GetRequiredService<ModbotContext>(),
            _clock,
            p.GetRequiredService<SyncDiagnostics>(),
            p.GetRequiredService<ISyncPacingSource>().Snapshot.AuditLog));

        services.AddScoped(p => new GroupInfoSync(
            p.GetRequiredService<IVRChatGate>(),
            p.GetRequiredService<IFactWriter>(),
            p.GetRequiredService<EventPartitionMaintainer>(),
            p.GetRequiredService<ModbotContext>(),
            _clock));

        services.AddSingleton<ISyncPacingSource>(Pacing);

        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _gate.Dispose();
        await _database.DisposeAsync();
    }

    private FakeSyncPacing Pacing { get; } = new();

    /// <summary>
    /// Nothing configured: the producer idles at spec 4.2's five-minute maximum.
    /// </summary>
    /// <remarks>
    /// The control for the test below. Without it, "the interval changed" could just as well mean
    /// the interval was never what the defaults say.
    /// </remarks>
    [Fact]
    public async Task WithNothingConfigured_TheAuditLogProducerIdlesAtTheDefaultMaximum()
    {
        var delays = new GatedDelayScheduler();
        var service = AuditLog(delays);

        await service.StartAsync(Ct);
        try
        {
            var first = await delays.HoldNextAsync(Ct);

            // Spec 4.2's floor, plus up to 10% of jitter (spec 4.2.2). Jitter only ever adds.
            Assert.InRange(first, AuditLogSyncOptions.PacingFloor, AuditLogSyncOptions.PacingFloor * 1.1);

            delays.Release();

            var second = await delays.HoldNextAsync(Ct);
            Assert.InRange(second, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5) * 1.1);

            delays.Release();
        }
        finally
        {
            await service.StopAsync(Ct);
        }
    }

    /// <summary>
    /// A cadence changed while the producer is running applies to its very next wait.
    /// </summary>
    [Fact]
    public async Task AChangedCadenceAppliesToTheNextPollWithoutARestart()
    {
        var delays = new GatedDelayScheduler();
        var service = AuditLog(delays);

        await service.StartAsync(Ct);
        try
        {
            _ = await delays.HoldNextAsync(Ct);

            // Held, so the loop is provably parked: the operator's change lands between ticks
            // rather than racing one.
            Pacing.Set(new SyncPacingDocument { AuditLogMaxIntervalSeconds = 45 });
            delays.Release();

            var next = await delays.HoldNextAsync(Ct);

            // The quiet cadence would have been five minutes. It is 45 seconds because the loop
            // re-read the pacing before computing this wait.
            Assert.InRange(next, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45) * 1.1);
            Assert.Equal(TimeSpan.FromSeconds(45), service.Cadence.Options.MaxInterval);

            delays.Release();
        }
        finally
        {
            await service.StopAsync(Ct);
        }
    }

    /// <summary>
    /// And it is still clamped: a cadence faster than spec 4.2's pacing floor is raised to it.
    /// </summary>
    [Fact]
    public async Task ACadenceBelowThePacingFloorIsRaisedEvenWhenSetOnARunningProducer()
    {
        var delays = new GatedDelayScheduler();
        var service = AuditLog(delays);

        await service.StartAsync(Ct);
        try
        {
            _ = await delays.HoldNextAsync(Ct);

            Pacing.Set(new SyncPacingDocument
            {
                AuditLogMinIntervalSeconds = 0.1,
                AuditLogMaxIntervalSeconds = 0.2,
            });

            delays.Release();

            var next = await delays.HoldNextAsync(Ct);

            Assert.Equal(AuditLogSyncOptions.PacingFloor, service.Cadence.Options.MinInterval);
            Assert.InRange(next, AuditLogSyncOptions.PacingFloor, AuditLogSyncOptions.PacingFloor * 1.1);

            delays.Release();
        }
        finally
        {
            await service.StopAsync(Ct);
        }
    }

    /// <summary>
    /// The group-info producer, whose default five-minute interval makes a needed restart the
    /// least obvious and the most annoying.
    /// </summary>
    [Fact]
    public async Task TheGroupInfoIntervalCanBeChangedOnARunningProducer()
    {
        var delays = new GatedDelayScheduler();
        var elapsed = new FakeMonotonicClock();

        var service = new GroupInfoSyncService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _clock,
            _services.GetRequiredService<SyncDiagnostics>(),
            elapsed,
            options: null,
            Pacing,
            delays);

        await service.StartAsync(Ct);
        try
        {
            _ = await delays.HoldNextAsync(Ct);

            Pacing.Set(new SyncPacingDocument { GroupInfoIntervalSeconds = 30 });
            delays.Release();

            var next = await delays.HoldNextAsync(Ct);

            // A five-minute schedule's second tick lands at least 4½ minutes out (spec 4.2.2's
            // jitter is ±10%), and a thirty-second one cannot reach past about a minute however
            // the offset and the jitter fall — so the two are not confusable.
            Assert.Equal(TimeSpan.FromSeconds(30), service.Options.Interval);
            Assert.True(
                next < TimeSpan.FromSeconds(70),
                $"The producer waited {next}, which is the old five-minute schedule.");

            delays.Release();
        }
        finally
        {
            await service.StopAsync(Ct);
        }
    }

    private GroupAuditLogSyncService AuditLog(GatedDelayScheduler delays) => new(
        _services.GetRequiredService<IServiceScopeFactory>(),
        _clock,
        _services.GetRequiredService<SyncDiagnostics>(),
        options: null,
        Pacing,
        delays);
}
