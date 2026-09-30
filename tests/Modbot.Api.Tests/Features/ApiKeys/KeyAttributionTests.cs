using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.ApiKeys;

/// <summary>
/// The wrapper that records which API key made a fact (API keys design §3.7) only works when it
/// is in front of the fact writer every endpoint resolves. These tests are what notices if that
/// stops being true.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class KeyAttributionTests
{
    private readonly PostgresFixture _db;

    public KeyAttributionTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheApiHost_ResolvesTheFactWriter_AsTheKeyAttributingOne()
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        using var scope = host.Services.CreateScope();

        Assert.IsType<KeyAttributingFactWriter>(scope.ServiceProvider.GetRequiredService<IFactWriter>());
    }

    [Fact]
    public void TheServer_RegistersAnalyticsBeforeTheApi()
    {
        // The real host is booted only against a database, so this reads the order it registers
        // in. A reorder would also fail at startup, because AddModbotApi throws when it finds no
        // fact writer -- this says why before anyone has to run the host to find out.
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Modbot.Server", "Program.cs"));

        var analytics = program.IndexOf("AddModbotAnalytics()", StringComparison.Ordinal);
        var api = program.IndexOf("AddModbotApi()", StringComparison.Ordinal);

        Assert.True(analytics >= 0 && api >= 0, "Program.cs no longer registers both.");
        Assert.True(analytics < api, "AddModbotAnalytics must come before AddModbotApi.");
    }

    [Fact]
    public void WithNoFactWriterRegistered_ItThrowsRatherThanDoNothing()
    {
        var services = new ServiceCollection();

        var problem = Assert.Throws<InvalidOperationException>(() => services.AddKeyAttribution());
        Assert.Contains("AddModbotAnalytics", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriterItWraps_IsMadeAndDisposedByTheContainer_AndItIsWrappedOnce()
    {
        var services = new ServiceCollection();
        services.AddScoped<IFactWriter, DisposableWriter>();
        services.AddKeyAttribution();
        services.AddKeyAttribution();

        using var provider = services.BuildServiceProvider();

        DisposableWriter inner;
        using (var scope = provider.CreateScope())
        {
            Assert.IsType<KeyAttributingFactWriter>(scope.ServiceProvider.GetRequiredService<IFactWriter>());
            inner = scope.ServiceProvider.GetRequiredKeyedService<IFactWriter>("modbot:fact-writer:inner") is DisposableWriter d
                ? d
                : throw new InvalidOperationException("The wrapped writer is not the one registered.");

            Assert.False(inner.Disposed);
        }

        Assert.True(inner.Disposed);
    }

    private sealed class DisposableWriter : IFactWriter, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;

        public Task<FactWriteResult> WriteAsync(FactRecord fact, CancellationToken ct = default)
            => Task.FromResult(new FactWriteResult(1, false));

        public Task<IReadOnlyList<FactWriteResult>> WriteManyAsync(IEnumerable<FactRecord> facts, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FactWriteResult>>([]);

        public Task<long?> AlreadyRecordedAsync(FactRecord fact, TimeSpan within, CancellationToken ct = default)
            => Task.FromResult<long?>(null);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Modbot.slnx.");
    }
}
