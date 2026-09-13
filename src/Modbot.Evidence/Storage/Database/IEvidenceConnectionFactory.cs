using Modbot.Evidence.Options;
using Npgsql;

namespace Modbot.Evidence.Storage.Database;

/// <summary>
/// Opens a connection for the in-database backend.
/// </summary>
/// <remarks>
/// An interface rather than a connection string passed around, because a host that already owns an
/// <c>NpgsqlDataSource</c> should hand Modbot a pooled connection from it rather than have a second
/// pool appear alongside the first.
/// </remarks>
public interface IEvidenceConnectionFactory
{
    Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default);
}

/// <summary>The obvious implementation: a connection string, and Npgsql's own pool.</summary>
public sealed class ConnectionStringEvidenceConnectionFactory : IEvidenceConnectionFactory
{
    private readonly string _connectionString;

    public ConnectionStringEvidenceConnectionFactory(DatabaseEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("The in-database backend needs a connection string.", nameof(options));

        _connectionString = options.ConnectionString;
    }

    public ConnectionStringEvidenceConnectionFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(_connectionString);

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
