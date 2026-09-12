using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Testcontainers.PostgreSql;

namespace Modbot.Core.Tests.Data;

/// <summary>
/// One real PostgreSQL container shared by every database test in the assembly.
/// </summary>
/// <remarks>
/// Real Postgres, not SQLite or InMemory: Modbot depends on table partitioning, jsonb, GIN
/// indexes and advisory locks, none of which a substitute provider implements faithfully.
/// A test that passes against a fake database proves nothing about the code that ships.
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

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
