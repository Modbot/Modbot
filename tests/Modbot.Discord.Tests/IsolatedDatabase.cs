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

    private readonly string _adminConnectionString;

    private IsolatedDatabase(string connectionString, string adminConnectionString)
    {
        _connectionString = connectionString;
        _adminConnectionString = adminConnectionString;
    }

    /// <param name="migrate">False leaves the database empty, for a test that migrates it one step at a time.</param>
    public static async Task<IsolatedDatabase> CreateAsync(PostgresFixture fixture, CancellationToken ct, bool migrate = true)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (migrate)
        {
            // A copy of the database the container migrated once, which is what migrating an empty one
            // here would produce, without every migration run again for every test.
            var connectionString = await fixture.CreateMigratedDatabaseAsync(ct);

            return new IsolatedDatabase(connectionString, fixture.ConnectionString);
        }

        var name = $"modbot_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync(ct);

            // A fresh GUID with a fixed prefix: nothing here is caller-supplied.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name };

        return new IsolatedDatabase(builder.ConnectionString, fixture.ConnectionString);
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
    /// Drops the database. Every test makes its own, so a pool left open per test runs the shared
    /// server out of connections part way through the suite ("too many clients already", which
    /// surfaces as whichever tests run last failing in setup), and the container's data is in
    /// memory, so a database left behind per test would fill it. The pool's idle connections are
    /// closed and the drop forces out any other (<see cref="TestDatabases.DropAsync"/>).
    /// </summary>
    public async ValueTask DisposeAsync()
        => await TestDatabases.DropAsync(_adminConnectionString, _connectionString);
}
