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

    public static async Task<IsolatedDatabase> CreateAsync(PostgresFixture fixture, CancellationToken ct)
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

        await using var context = database.NewContext();
        await context.Database.MigrateAsync(ct);

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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
