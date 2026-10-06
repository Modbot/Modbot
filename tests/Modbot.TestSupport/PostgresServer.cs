using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modbot.TestSupport;

/// <summary>
/// The one PostgreSQL container of a test run, and the migrated database every other database in
/// it is copied from.
/// </summary>
/// <remarks>
/// <para>
/// A run needs one container, however many test classes there are: starting one takes seconds and
/// holds memory. It is started the first time a fixture asks for it and stopped when the process
/// ends (and, if that fails, by Testcontainers' own cleanup container, which removes it once the
/// process has gone).
/// </para>
/// <para>
/// The database is migrated once, into <c>modbot_template</c>, which nothing ever connects to.
/// Every fixture's database is a copy of it (<c>CREATE DATABASE ... TEMPLATE</c>), which takes a
/// fraction of the time that running the migrations again would, and is identical to a database
/// that was migrated from empty.
/// </para>
/// </remarks>
internal sealed class PostgresServer
{
    private const string TemplateName = "modbot_template";

    private static readonly Lazy<Task<PostgresServer>> Shared = new(StartAsync);

    private readonly PostgreSqlContainer _container;

    // PostgreSQL refuses to copy a database while any session is connected to it, and copies
    // started at the same moment are the likeliest way to be told so. One at a time.
    private readonly SemaphoreSlim _copying = new(1, 1);

    private PostgresServer(PostgreSqlContainer container) => _container = container;

    /// <summary>The server, started and holding the migrated template. Every caller gets the same one.</summary>
    public static Task<PostgresServer> GetAsync() => Shared.Value;

    /// <summary>
    /// A connection string to the server's own <c>postgres</c> database, for creating and dropping
    /// databases. Not pooled: it is used a few times a second at most, and an idle connection
    /// would count against <c>max_connections</c> for nothing.
    /// </summary>
    public string AdminConnectionString =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Pooling = false }.ConnectionString;

    /// <summary>A connection string to a database in this container, pooled as a test host's would be.</summary>
    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>
    /// Makes <paramref name="database"/> as a copy of the migrated template: the schema, the rows
    /// the migrations write, and the migration history, exactly as a database migrated from empty
    /// has them.
    /// </summary>
    public async Task CreateDatabaseAsync(string database, CancellationToken ct = default)
    {
        await _copying.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await RunAsync($"CREATE DATABASE \"{database}\" TEMPLATE \"{TemplateName}\"", ct).ConfigureAwait(false);
                    return;
                }
                catch (PostgresException e) when (e.SqlState == "55006" && attempt < 100)
                {
                    // object_in_use: the last connection to the template (the one that migrated it)
                    // has been closed by us but the server has not finished removing it yet.
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _copying.Release();
        }
    }

    private static async Task<PostgresServer> StartAsync()
    {
        var container = PostgresContainer.Builder().Build();
        await container.StartAsync().ConfigureAwait(false);

        var server = new PostgresServer(container);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => server.StopAtExit();

        try
        {
            await server.RunAsync($"CREATE DATABASE \"{TemplateName}\"", CancellationToken.None).ConfigureAwait(false);

            // Not pooled, so that when the migration is done the connection is really closed and
            // the template has no session left to refuse a copy.
            var template = new NpgsqlConnectionStringBuilder(server.AdminConnectionString) { Database = TemplateName };
            var options = new DbContextOptionsBuilder<ModbotContext>().UseNpgsql(template.ConnectionString).Options;

            await using var context = new ModbotContext(options);
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return server;
    }

    private async Task RunAsync(string sql, CancellationToken ct)
    {
        // CREATE DATABASE cannot run inside a transaction, which is what a command sent with
        // other statements gets: one statement per command.
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void StopAtExit()
    {
        try
        {
            _container.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(30));
        }
        catch (Exception)
        {
            // Testcontainers' cleanup container removes it once this process has gone.
        }
    }
}
