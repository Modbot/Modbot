using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.TestSupport;
using Npgsql;

namespace Modbot.VRChat.Tests;

/// <summary>
/// A private, migrated database inside the shared container.
/// </summary>
/// <remarks>
/// <para>
/// The sync tests cannot share one. Each producer's cursor lives on the <c>settings</c> singleton
/// row, so two tests running against the same database would be handing each other a watermark
/// mid-poll -- and the resulting failures would be intermittent and blame the producer.
/// </para>
/// <para>
/// Duplicated from the analytics suite rather than shared, for the reason
/// <see cref="PostgresCollection"/> is: fixtures resolve within the assembly that declares them,
/// and a shared helper here would mean a test-support project that knows about every suite.
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

    /// <summary>For the tests that need to build a container around this database.</summary>
    public string ConnectionString => _connectionString;

    public ModbotContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ModbotContext>()
            .UseNpgsql(_connectionString)
            .Options;

        return new ModbotContext(options);
    }

    /// <summary>The container takes the database with it; dropping it here only fights the pool.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
