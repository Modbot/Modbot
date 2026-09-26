using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Data;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Evidence;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;
using Modbot.TestSupport;

namespace Modbot.Demo.Tests;

/// <summary>
/// The evidence store's startup probe, over a freshly seeded demo (evidence storage design §8.3-8.4).
/// </summary>
/// <remarks>
/// A demo's quick seed used to mint <c>Settings.EvidenceStoreId</c> before anything had written a
/// matching store marker, so the very first startup probe -- run moments after the seed, in
/// <c>EvidenceRegistration.LoadEvidenceSettingsAsync</c> -- always found an expected id and no
/// marker and raised the same "evidence store is not there" lock a real lost bucket would raise.
/// A demo never signs in, so the critical notification that lock raises (foundation §4.5.3) never
/// reaches anybody and the banner it leaves behind never clears on its own. These two tests are
/// the two ends of the fix: the fresh seed must not read as locked, and the history write that
/// follows a moment later must bring the store all the way to healthy.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class DemoEvidenceStartupTests
{
    private readonly PostgresFixture _fixture;

    public DemoEvidenceStartupTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AFreshDemoSeedDoesNotLockTheEvidenceStoreAtStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await DemoSeedHost.StartAsync(_fixture, ct);

        // The quick half of the seed, exactly as Program.cs runs it before the evidence settings
        // are ever loaded from the database.
        await seed.Seeder().SeedCoreAsync(ct);

        var settingsAfterSeed = await seed.Db.GetSettingsAsync(ct);
        Assert.Null(settingsAfterSeed.EvidenceStoreId);

        await using var services = BuildEvidenceServices();

        // The startup probe. On the old seed this found an expected id and no marker for it, and
        // locked -- raising the critical, every-channel alarm design §8.4 reserves for a real lost
        // store.
        var health = await services.LoadEvidenceSettingsAsync(ct);

        Assert.Equal(EvidenceStoreState.NotConfigured, health.State);
        Assert.False(health.ShouldAlarm);
    }

    [Fact]
    public async Task TheStoreBecomesHealthyOnceTheDemosEvidenceIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var seed = await DemoSeedHost.StartAsync(_fixture, ct);

        var plan = await seed.Seeder().SeedCoreAsync(ct);

        await using var services = BuildEvidenceServices();
        await services.LoadEvidenceSettingsAsync(ct);

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var db = provider.GetRequiredService<ModbotContext>();
        var store = provider.GetRequiredService<IEvidenceStore>();
        var metadata = provider.GetRequiredService<IEvidenceMetadata>();
        var options = provider.GetRequiredService<EvidenceOptions>();
        var monitor = provider.GetRequiredService<EvidenceStoreMonitor>();

        // The heavy half's evidence step, run exactly as DemoDataService.WriteHistoryAsync runs it:
        // write the marker, then re-check.
        await new DemoEvidence(db, store, metadata, options).WriteAsync(plan, ct);
        var health = await monitor.CheckAsync(ct);

        Assert.Equal(EvidenceStoreState.Healthy, health.State);
        Assert.False(health.ShouldAlarm);
        Assert.True(health.UploadsAllowed);
        Assert.NotNull(health.ExpectedStoreId);
        Assert.Equal(health.ExpectedStoreId, health.FoundStoreId);

        // The id DemoEvidence minted was saved, not just held in memory on the options object.
        await using var check = _fixture.NewContext();
        var settings = await check.GetSettingsAsync(ct);
        Assert.Equal(health.ExpectedStoreId, settings.EvidenceStoreId);
    }

    /// <summary>
    /// The evidence stack the way <c>Program.cs</c> composes it: registered at defaults, then made
    /// reconfigurable from <c>Settings</c> by <c>AddModbotEvidenceSettings</c>.
    /// </summary>
    private ServiceProvider BuildEvidenceServices()
    {
        var services = new ServiceCollection();

        services.AddDbContext<ModbotContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        services.AddSingleton<IModbotClock>(new FakeClock());

        services.AddSingleton<ISecretProtector>(provider =>
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            return AesGcmSecretProtector.CreateAsync(db).GetAwaiter().GetResult();
        });

        services.AddModbotEvidence();
        services.AddModbotEvidenceSettings();
        services.AddScoped<IEvidenceMetadata, DatabaseEvidenceMetadata>();

        return services.BuildServiceProvider();
    }
}
