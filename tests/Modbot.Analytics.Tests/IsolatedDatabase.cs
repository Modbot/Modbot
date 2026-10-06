using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.TestSupport;
using Npgsql;

namespace Modbot.Analytics.Tests;

/// <summary>
/// A private, migrated database inside the shared container.
/// </summary>
/// <remarks>
/// <para>
/// The fact-log tests can share one database because each writes facts under ids nobody else
/// uses. The daily totals and retention tests cannot: a daily total is an aggregate <em>over every fact in
/// the table</em>, and retention drops whole partitions out from under whatever else is running.
/// Either one, run against the shared database, would be reading and destroying other tests'
/// data.
/// </para>
/// <para>
/// Creating a database is far cheaper than starting a second container, and it is a copy of the
/// database the container migrated from empty once for the whole run, which is what proves the
/// migrations apply -- the only way they ever run in production.
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
