using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;

namespace Modbot.TestSupport;

/// <summary>
/// A database of its own, migrated and empty, inside the one PostgreSQL container of the test run.
/// </summary>
/// <remarks>
/// <para>
/// Real Postgres, not SQLite or InMemory: Modbot depends on table partitioning, jsonb, GIN
/// indexes and advisory locks, none of which a substitute provider implements faithfully.
/// A test that passes against a fake database proves nothing about the code that ships.
/// </para>
/// <para>
/// <strong>What a fixture isolates.</strong> Each instance of this class makes its own database
/// (a copy of the migrated template, see <see cref="PostgresServer"/>) and drops it when it is
/// disposed. How many tests share an instance is up to the collection it is declared on:
/// </para>
/// <list type="bullet">
/// <item><description>
/// On an ordinary collection definition, one instance serves every test in the assembly, which
/// run one after another: one database, as it always was.
/// </description></item>
/// <item><description>
/// On a definition marked <see cref="OneFixturePerClassAttribute"/>, every test class gets an
/// instance of its own and so a database of its own. Tests in one class still run one after
/// another against the same database, exactly as before; tests in different classes run at the
/// same time and cannot see each other's rows, sequences, partitions or locks (advisory locks
/// belong to a database). The one thing they still share is the process: a static field, an
/// environment variable, a fixed port or a fixed path would be shared between them, so none may
/// be used by a test that is not in a collection of its own.
/// </description></item>
/// </list>
/// <para>
/// The matching <c>[CollectionDefinition]</c> lives in each test assembly, not here: xUnit only
/// resolves a collection definition declared alongside the tests that use it.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgresServer? _server;

    /// <summary>The connection string of this fixture's database. Set once the fixture is initialised.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _server = await PostgresServer.GetAsync();

        var database = $"modbot_{Guid.NewGuid():N}";
        await _server.CreateDatabaseAsync(database);

        ConnectionString = _server.ConnectionStringFor(database);
    }

    public async ValueTask DisposeAsync()
    {
        // The container's data is in memory, and a class's database is not needed once its tests
        // have run. The container itself is stopped when the process ends.
        if (_server is not null && ConnectionString.Length > 0)
            await TestDatabases.DropAsync(_server.AdminConnectionString, ConnectionString);
    }

    public ModbotContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ModbotContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new ModbotContext(options);
    }
}
