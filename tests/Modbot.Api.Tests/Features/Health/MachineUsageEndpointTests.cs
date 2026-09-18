using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Health;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.Core.Machine;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Health;

/// <summary>
/// Who may read how hard the machine is working, and what comes back.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MachineUsageEndpointTests
{
    private readonly PostgresFixture _db;

    public MachineUsageEndpointTests(PostgresFixture db) => _db = db;

    private const string Path = "/api/health/machine";

    /// <summary>A sampler with two readings in it, so the endpoint has a window to hand back.</summary>
    private static MachineUsageSampler Filled()
    {
        var clock = new FakeClock();
        var sampler = new MachineUsageSampler(clock) { Processors = 2 };

        sampler.Take(new MachineCounters(TimeSpan.Zero, 300, 2_000, 0, 0));
        clock.Advance(TimeSpan.FromSeconds(10));
        sampler.Take(new MachineCounters(TimeSpan.FromSeconds(4), 400, 2_000, 10_000, 5_000));

        return sampler;
    }

    [Fact]
    public async Task ReadingItNeedsTheOperationalLogPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        // A moderator with every moderation permission and not this one is still refused: it is
        // Modbot's record about itself, like the rest of the health detail.
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers, ct);
        using var response = await host.GetAsync(Path, cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ItIsRefusedWithoutASignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        using var response = await host.Client.GetAsync(Path, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheWindowComesBackWithTheReadingsAndWhatTheyAreMeasuredAgainst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(
            _db, configure: services => services.AddSingleton(Filled()));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var usage = await host.GetJsonAsync<MachineUsage>(Path, cookie, ct);

        Assert.Equal(10, usage.SampleSeconds);
        Assert.Equal(30, usage.WindowMinutes);
        Assert.Equal(2, usage.Processors);
        Assert.Equal(2_000, usage.MemoryLimitBytes);

        var point = Assert.Single(usage.Points);

        // Four processor-seconds over ten seconds of two processors.
        Assert.Equal(20, point.ProcessorPercent!.Value, 6);
        Assert.Equal(400, point.MemoryBytes);
        Assert.Equal(1_000, point.DiskReadBytesPerSecond!.Value, 6);
        Assert.Equal(500, point.DiskWrittenBytesPerSecond!.Value, 6);
    }

    [Fact]
    public async Task AHostWithNoSamplerAnswersWithAnEmptyWindow()
    {
        var ct = TestContext.Current.CancellationToken;

        // The API is mapped by hosts that do not run the background services at all. That must be
        // an empty window, not a failure to resolve a service in the middle of a request.
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var usage = await host.GetJsonAsync<MachineUsage>(Path, cookie, ct);

        Assert.Empty(usage.Points);
        Assert.Null(usage.MemoryLimitBytes);
        Assert.Equal(host.Clock.UtcNow, usage.Now);
    }
}
