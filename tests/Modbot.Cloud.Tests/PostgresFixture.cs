using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modbot.Cloud.Tests;

/// <summary>
/// One real PostgreSQL container holding Cloud's two databases, each migrated on its own.
/// </summary>
/// <remarks>
/// Two databases in one container rather than two containers: they are separate databases to
/// PostgreSQL, with separate catalogues and separate migration histories, which is everything the
/// split promises, and one container starts in half the time.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public string EngineConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var server = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        await using (var connection = new NpgsqlConnection(server.ConnectionString))
        {
            await connection.OpenAsync();
            // One statement per command: CREATE DATABASE refuses to run inside the implicit
            // transaction a multi-statement command gets.
            foreach (var database in new[] { "cloud_main", "cloud_engine" })
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
                await create.ExecuteNonQueryAsync();
            }
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = "cloud_main" }.ConnectionString;
        EngineConnectionString = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = "cloud_engine" }.ConnectionString;

        await using var cloud = NewCloudContext();
        await cloud.Database.MigrateAsync();

        await using var engine = NewEngineContext();
        await engine.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public CloudContext NewCloudContext() =>
        new(new DbContextOptionsBuilder<CloudContext>().UseNpgsql(ConnectionString).Options);

    public EngineContext NewEngineContext() => new(EngineContext.Options(EngineConnectionString).Options);

    /// <summary>Empties every table. Tests in the collection run one at a time.</summary>
    public async Task ResetAsync()
    {
        // Every table cloud_main holds. Accounts, the server registry and the public rooms
        // feed all landed after this list was first written, and a table left off it is not
        // reset between tests -- it is the one way a test that never touches shared fixtures
        // can still see another test's rows (spec: fix shared-database interference with
        // unique data per test, not by leaving a table dirty).
        await using (var cloud = NewCloudContext())
            await cloud.Database.ExecuteSqlRawAsync(
                "TRUNCATE install, admin_session, settings, instance_alert, showcase_entry, " +
                "account, account_session, account_token, " +
                "public_room, rooms_server, registered_server, server_report, " +
                "page_instance, visitor_instance");

        await using var engine = NewEngineContext();
        await engine.Database.ExecuteSqlRawAsync("TRUNCATE companion_event, install_clock, event_day_total, event_hour_total, instance_log");
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
