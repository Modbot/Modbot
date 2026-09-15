using Npgsql;

namespace Modbot.Cloud.Data;

/// <summary>
/// Turns whatever was put in <c>DATABASE_URL</c> or <c>DATABASE_ENGINE_URL</c> into something Npgsql
/// will accept.
/// </summary>
/// <remarks>
/// <para>
/// The same conversion as <c>Modbot.My.Data.DatabaseUrl</c>, copied rather than shared so Cloud
/// depends on nothing else in the repository.
/// </para>
/// <para>
/// Railway and every other managed Postgres hand out <c>postgres://user:pass@host:5432/dbname</c>,
/// and Npgsql accepts only keyword strings. A keyword string is passed through untouched.
/// </para>
/// </remarks>
public static class DatabaseUrl
{
    private const int DefaultPostgresPort = 5432;

    /// <param name="databaseUrl">The value.</param>
    /// <param name="variable">Which variable it came from, for the error message.</param>
    public static string ToConnectionString(string databaseUrl, string variable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseUrl);

        var value = databaseUrl.Trim();

        if (!IsPostgresUrl(value))
            return value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new FormatException(
                $"{variable} looks like a URL but could not be parsed as one. "
                + "The expected form is postgres://user:password@host:5432/database.");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? DefaultPostgresPort : uri.Port,
        };

        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (database.Length == 0)
            throw new FormatException(
                $"{variable} names no database. The expected form is "
                + "postgres://user:password@host:5432/database.");

        builder.Database = database;

        // A password containing ':' or '@' arrives percent-encoded, so split on the first colon
        // only and unescape both halves.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var credentials = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(credentials[0]);

            if (credentials.Length == 2)
                builder.Password = Uri.UnescapeDataString(credentials[1]);
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var option = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

            try
            {
                builder[key] = option;
            }
            catch (ArgumentException e)
            {
                throw new FormatException(
                    $"{variable} carries the option '{key}', which Npgsql does not recognise. "
                    + "Remove it from the URL's query string.",
                    e);
            }
        }

        return builder.ConnectionString;
    }

    private static bool IsPostgresUrl(string value) =>
        value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);
}
