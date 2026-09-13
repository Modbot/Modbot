using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.TestSupport;
using Modbot.VRChat.Pacing;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Pacing;

/// <summary>
/// The column, read back through the provider the producers and the limiter actually use.
/// </summary>
/// <remarks>
/// Against real PostgreSQL, because the point of the column is that it is <c>jsonb</c> in a real
/// database: a provider tested against a substitute would prove that a string round-trips through
/// a dictionary, which is not the thing that can break.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class SyncPacingProviderTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    private IsolatedDatabase _database = null!;
    private ServiceProvider _services = null!;

    public SyncPacingProviderTests(PostgresFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await IsolatedDatabase.CreateAsync(_fixture, Ct);

        var services = new ServiceCollection();
        services.AddDbContext<ModbotContext>(o => o.UseNpgsql(_database.ConnectionString));
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _database.DisposeAsync();
    }

    /// <summary>
    /// A deployment that has never opened the settings screen runs spec 4.2's pacing.
    /// </summary>
    [Fact]
    public async Task WithNoRowAtAll_TheDefaultsAreInForce()
    {
        var provider = Provider(new FakeClock());
        var pacing = await provider.CurrentAsync(Ct);

        Assert.Equal(0.5, pacing.EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);
        Assert.Equal(AuditLogSyncOptions.PacingFloor, pacing.AuditLog.MinInterval);
        Assert.Equal(RateLimitOptions.DefaultFraction, pacing.BudgetFraction);
    }

    [Fact]
    public async Task AStoredDocumentIsRead()
    {
        await StoreAsync(new SyncPacingDocument
        {
            BudgetFraction = 0.3,
            GroupInfoIntervalSeconds = 1800,
        });

        var pacing = await Provider(new FakeClock()).CurrentAsync(Ct);

        Assert.Equal(0.3, pacing.BudgetFraction);
        Assert.Equal(TimeSpan.FromMinutes(30), pacing.GroupInfo.Interval);
    }

    /// <summary>
    /// A row edited outside the API is picked up on its own, within the cache window.
    /// </summary>
    /// <remarks>
    /// The supported path publishes its change directly, so this bounds the unsupported one — a
    /// hand-edited row, or a second process. Without it "configurable" would quietly mean
    /// "configurable through this one endpoint, in this one process".
    /// </remarks>
    [Fact]
    public async Task AChangeWrittenBehindTheProvidersBackIsNoticedWhenTheCacheExpires()
    {
        var clock = new FakeClock();
        var provider = Provider(clock);

        Assert.Equal(0.5, (await provider.CurrentAsync(Ct)).EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);

        await StoreAsync(new SyncPacingDocument
        {
            ClassCeilingsPerSecond = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [VRChatEndpointClass.GroupsMembers] = 0.1,
            },
        });

        // Still cached.
        Assert.Equal(0.5, (await provider.CurrentAsync(Ct)).EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);

        clock.Advance(SyncPacingProvider.CacheFor + TimeSpan.FromSeconds(1));

        Assert.Equal(0.06, (await provider.CurrentAsync(Ct)).EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);
    }

    /// <summary>A change published by the endpoint does not wait for the cache.</summary>
    [Fact]
    public async Task APublishedChangeIsInForceImmediately()
    {
        var provider = Provider(new FakeClock());
        _ = await provider.CurrentAsync(Ct);

        var before = provider.Version;
        provider.Publish(SyncPacingJson.Write(new SyncPacingDocument { BudgetFraction = 0.25 }));

        Assert.Equal(0.25, provider.Snapshot.BudgetFraction);
        Assert.True(provider.Version > before);
        Assert.Equal(0.25, (await provider.CurrentAsync(Ct)).BudgetFraction);
    }

    /// <summary>Publishing the same document again is not a change, so nothing reconfigures.</summary>
    [Fact]
    public async Task RewritingTheSameDocumentDoesNotBumpTheVersion()
    {
        var json = SyncPacingJson.Write(new SyncPacingDocument { BudgetFraction = 0.25 });

        var provider = Provider(new FakeClock());
        _ = await provider.CurrentAsync(Ct);

        provider.Publish(json);
        var version = provider.Version;
        provider.Publish(json);

        Assert.Equal(version, provider.Version);
    }

    /// <summary>
    /// A rate stored above the cap — by hand, or by an older build — is still capped on read.
    /// </summary>
    [Fact]
    public async Task ARowThatWasNotWrittenThroughTheApiIsStillClamped()
    {
        await using (var context = _database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.SyncPacing =
                """{"classCeilingsPerSecond":{"groups.members":9000}}""";

            await context.SaveChangesAsync(Ct);
        }

        var pacing = await Provider(new FakeClock()).CurrentAsync(Ct);

        Assert.Equal(0.5, pacing.EffectiveRatePerSecond(VRChatEndpointClass.GroupsMembers), 6);
    }

    /// <summary>
    /// An unreadable blob leaves the pacing alone rather than stopping the producers.
    /// </summary>
    /// <remarks>
    /// The column is <c>jsonb</c>, so Postgres will not accept a syntactically broken value — but
    /// it will happily accept well-formed JSON of the wrong shape, which is what an older or
    /// newer build writes.
    /// </remarks>
    [Fact]
    public async Task AWellFormedDocumentOfTheWrongShapeResolvesToTheDefaults()
    {
        await using (var context = _database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.SyncPacing = """{"somethingElseEntirely":[1,2,3]}""";
            await context.SaveChangesAsync(Ct);
        }

        var pacing = await Provider(new FakeClock()).CurrentAsync(Ct);

        Assert.Equal(SyncPacing.Defaults.AuditLog, pacing.AuditLog);
        Assert.Equal(RateLimitOptions.DefaultFraction, pacing.BudgetFraction);
    }

    private SyncPacingProvider Provider(FakeClock clock) =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), clock);

    private async Task StoreAsync(SyncPacingDocument document)
    {
        await using var context = _database.NewContext();

        var settings = await context.GetSettingsAsync(Ct);
        settings.SyncPacing = SyncPacingJson.Write(document);

        await context.SaveChangesAsync(Ct);
    }
}
