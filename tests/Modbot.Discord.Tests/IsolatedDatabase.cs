using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.TestSupport;
using Npgsql;

namespace Modbot.Discord.Tests;

/// <summary>
/// A fresh, migrated database inside the shared container. The poster's cursor and the bot's
/// settings live on the singleton settings row, so tests that move them cannot share one.
/// </summary>
public sealed class IsolatedDatabase : IAsyncDisposable
{
    private readonly string _connectionString;

    private IsolatedDatabase(string connectionString) => _connectionString = connectionString;

    /// <param name="migrate">False leaves the database empty, for a test that migrates it one step at a time.</param>
    public static async Task<IsolatedDatabase> CreateAsync(PostgresFixture fixture, CancellationToken ct, bool migrate = true)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var name = $"modbot_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync(ct);

            // A fresh GUID with a fixed prefix: nothing here is caller-supplied.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name };
        var database = new IsolatedDatabase(builder.ConnectionString);

        if (migrate)
        {
            await using var context = database.NewContext();
            await context.Database.MigrateAsync(ct);
        }

        return database;
    }

    public string ConnectionString => _connectionString;

    public ModbotContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ModbotContext>()
            .UseNpgsql(_connectionString)
            .Options;

        return new ModbotContext(options);
    }

    /// <summary>
    /// Closes the idle connections this database's pool keeps. Every test makes its own database,
    /// and a pool left open per test runs the shared server out of connections part way through
    /// the suite.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        NpgsqlConnection.ClearPool(connection);
        return ValueTask.CompletedTask;
    }
}
