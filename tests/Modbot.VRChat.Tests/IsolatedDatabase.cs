using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.TestSupport;
using Npgsql;

namespace Modbot.VRChat.Tests;

/// <summary>
/// A private, migrated database inside the shared container.
/// </summary>
/// <remarks>
/// <para>
/// The sync tests cannot share one. Each producer's cursor lives on the <c>settings</c> singleton
/// row, so two tests running against the same database would be handing each other a watermark
/// mid-poll -- and the resulting failures would be intermittent and blame the producer.
/// </para>
/// <para>
/// Duplicated from the analytics suite rather than shared, for the reason
/// <see cref="PostgresCollection"/> is: fixtures resolve within the assembly that declares them,
/// and a shared helper here would mean a test-support project that knows about every suite.
/// </para>
/// </remarks>
public sealed class IsolatedDatabase : IAsyncDisposable
{
    private readonly string _connectionString;

    private readonly string _adminConnectionString;

    private IsolatedDatabase(string connectionString, string adminConnectionString)
    {
        _connectionString = connectionString;
        _adminConnectionString = adminConnectionString;
    }

    public static async Task<IsolatedDatabase> CreateAsync(PostgresFixture fixture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        // A copy of the database the container migrated once, which is what migrating an empty one
        // here would produce, without every migration run again for every test.
        var connectionString = await fixture.CreateMigratedDatabaseAsync(ct);

        return new IsolatedDatabase(connectionString, fixture.ConnectionString);
    }

    /// <summary>For the tests that need to build a container around this database.</summary>
    public string ConnectionString => _connectionString;

    public ModbotContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ModbotContext>()
            .UseNpgsql(_connectionString)
            .Options;

        return new ModbotContext(options);
    }

    /// <summary>
    /// Drops the database. Every test makes its own, so a pool left open per test runs the shared
    /// server out of connections part way through the suite ("too many clients already", which
    /// surfaces as whichever tests run last failing in setup), and the container's data is in
    /// memory, so a database left behind per test would fill it. The pool's idle connections are
    /// closed and the drop forces out any other (<see cref="TestDatabases.DropAsync"/>).
    /// </summary>
    public async ValueTask DisposeAsync()
        => await TestDatabases.DropAsync(_adminConnectionString, _connectionString);
}
