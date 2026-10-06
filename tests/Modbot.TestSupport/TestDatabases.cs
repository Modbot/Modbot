using Npgsql;

namespace Modbot.TestSupport;

/// <summary>
/// Cleaning up the databases tests make for themselves inside the shared container.
/// </summary>
public static class TestDatabases
{
    /// <summary>
    /// Drops a database a test made, once the test is done with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The container's data lives in memory (<see cref="PostgresContainer"/>), and a migrated
    /// database is some megabytes of it, so a suite that makes one per test and drops none would
    /// fill the machine long before it finished.
    /// </para>
    /// <para>
    /// The pool's idle connections are closed first, and the drop forces out whatever is left
    /// (<c>WITH (FORCE)</c>), so a connection a test forgot to return cannot make it fail. Cleanup
    /// is not a reason to fail a test, so nothing here throws: a database that could not be
    /// dropped stays until the container goes.
    /// </para>
    /// </remarks>
    /// <param name="adminConnectionString">A connection string to any other database in the same container, such as the fixture's.</param>
    /// <param name="databaseConnectionString">The connection string of the database to drop.</param>
    public static async Task DropAsync(string adminConnectionString, string databaseConnectionString)
    {
        try
        {
            using (var pooled = new NpgsqlConnection(databaseConnectionString))
                NpgsqlConnection.ClearPool(pooled);

            var name = new NpgsqlConnectionStringBuilder(databaseConnectionString).Database;
            if (string.IsNullOrEmpty(name))
                return;

            var admin = new NpgsqlConnectionStringBuilder(adminConnectionString) { Pooling = false };

            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();

            // The name is one this suite generated; the quoting is for the form of it, not for input.
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
        }
    }
}
