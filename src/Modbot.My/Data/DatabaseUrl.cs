using Modbot.My.Configuration;
using Npgsql;

namespace Modbot.My.Data;

/// <summary>
/// Turns whatever was put in <c>DATABASE_URL</c> into something Npgsql will accept.
/// </summary>
/// <remarks>
/// <para>
/// The same conversion as <c>Modbot.Host.Data.DatabaseUrl</c>. It is a copy rather than a shared
/// reference because the Host's lives beside the rest of Modbot's startup code, and Modbot.My must
/// not depend on any of that.
/// </para>
/// <para>
/// Railway and every other managed Postgres hand out <c>postgres://user:pass@host:5432/dbname</c>,
/// and Npgsql accepts only keyword strings. A keyword string is passed through untouched.
/// </para>
/// </remarks>
public static class DatabaseUrl
{
    private const int DefaultPostgresPort = 5432;

    public static string ToConnectionString(string databaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseUrl);

        var value = databaseUrl.Trim();

        if (!IsPostgresUrl(value))
            return value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new FormatException(
                $"{MyEnvironment.DatabaseUrlVariable} looks like a URL but could not be parsed as one. "
                + "The expected form is postgres://user:password@host:5432/database.");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? DefaultPostgresPort : uri.Port,
        };

        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (database.Length == 0)
            throw new FormatException(
                $"{MyEnvironment.DatabaseUrlVariable} names no database. The expected form is "
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
                    $"{MyEnvironment.DatabaseUrlVariable} carries the option '{key}', which Npgsql does "
                    + "not recognise. Remove it from the URL's query string.",
                    e);
            }
        }

        return builder.ConnectionString;
    }

    private static bool IsPostgresUrl(string value) =>
        value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);
}
