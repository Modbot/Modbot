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
/// Creating a database is far cheaper than starting a second container, and migrating it proves
/// the migrations apply from empty -- which is the only way they ever run in production.
/// </para>
/// </remarks>
public sealed class IsolatedDatabase : IAsyncDisposable
{
    private readonly string _connectionString;

    private IsolatedDatabase(string connectionString) => _connectionString = connectionString;

    public static async Task<IsolatedDatabase> CreateAsync(PostgresFixture fixture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var name = $"modbot_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync(ct);

            // The name is a fresh GUID with a fixed prefix, so it needs no escaping beyond the
            // quoting; nothing here is caller-supplied.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name };
        var database = new IsolatedDatabase(builder.ConnectionString);

        await using var context = database.NewContext();
        await context.Database.MigrateAsync(ct);

        return database;
    }

    public ModbotContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ModbotContext>()
            .UseNpgsql(_connectionString)
            .Options;

        return new ModbotContext(options);
    }

    /// <summary>
    /// The database is left for the container to take with it, since dropping it here would only
    /// fight with Npgsql's connection pool -- but the pool's idle connections are closed. Every test
    /// makes its own database, and a pool left open per test runs the shared server out of
    /// connections part way through the suite.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        NpgsqlConnection.ClearPool(connection);
        return ValueTask.CompletedTask;
    }
}
