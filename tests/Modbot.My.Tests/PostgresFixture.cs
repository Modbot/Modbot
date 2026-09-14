using Microsoft.EntityFrameworkCore;
using Modbot.My.Data;
using Testcontainers.PostgreSql;

namespace Modbot.My.Tests;

/// <summary>
/// One real PostgreSQL container for the whole assembly, migrated once.
/// </summary>
/// <remarks>
/// Its own fixture rather than Modbot.TestSupport's, which migrates Modbot's main database and
/// would pull all of Modbot.Core into a project that must not depend on it.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public MyContext NewContext() =>
        new(new DbContextOptionsBuilder<MyContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Empties both tables. Tests in the collection run one at a time.</summary>
    public async Task ResetAsync()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE registered_instance, register_page_instance");
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
