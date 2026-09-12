using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Testcontainers.PostgreSql;

namespace Modbot.TestSupport;

/// <summary>
/// One real PostgreSQL container shared by every database test in the assembly.
/// </summary>
/// <remarks>
/// <para>
/// Real Postgres, not SQLite or InMemory: Modbot depends on table partitioning, jsonb, GIN
/// indexes and advisory locks, none of which a substitute provider implements faithfully.
/// A test that passes against a fake database proves nothing about the code that ships.
/// </para>
/// <para>
/// The matching <c>[CollectionDefinition]</c> lives in each test assembly, not here: xUnit only
/// resolves a collection definition declared alongside the tests that use it.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public ModbotContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ModbotContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new ModbotContext(options);
    }
}
