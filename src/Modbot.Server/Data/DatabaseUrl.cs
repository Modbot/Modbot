using Modbot.Core.Configuration;
using Npgsql;

namespace Modbot.Server.Data;

/// <summary>
/// Turns whatever an operator put in <c>DATABASE_URL</c> into something Npgsql will accept.
/// </summary>
/// <remarks>
/// <para>
/// Railway, Heroku, Fly and every other managed Postgres hands out a URL —
/// <c>postgres://user:pass@host:5432/dbname</c> — and Npgsql accepts only ADO.NET keyword strings.
/// Left unconverted the first thing a new deployment does is fail with "Format of the
/// initialization string does not conform to specification", which tells the operator nothing
/// about the variable they just pasted.
/// </para>
/// <para>
/// Both forms are accepted because both are real: the URL is what the hosting platform provides,
/// and a developer running Modbot locally will paste <c>Host=localhost;Database=modbot;...</c>
/// straight from pgAdmin. Anything that is not a <c>postgres://</c> or <c>postgresql://</c> URL is
/// passed through untouched and left for Npgsql to validate.
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
                $"{ModbotEnvironment.DatabaseUrlVariable} looks like a URL but could not be parsed as "
                + "one. The expected form is postgres://user:password@host:5432/database.");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? DefaultPostgresPort : uri.Port,
        };

        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (database.Length == 0)
            throw new FormatException(
                $"{ModbotEnvironment.DatabaseUrlVariable} names no database. The expected form is "
                + "postgres://user:password@host:5432/database — the part after the last slash is "
                + "the database name.");

        builder.Database = database;

        // A password containing ':' or '@' arrives percent-encoded, so split on the first colon
        // only and unescape both halves. Getting this wrong produces an authentication failure
        // that looks like a wrong password rather than a parsing bug.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var credentials = uri.UserInfo.Split(':', 2);
            builder.Username = Uri.UnescapeDataString(credentials[0]);

            if (credentials.Length == 2)
                builder.Password = Uri.UnescapeDataString(credentials[1]);
        }

        ApplyQueryParameters(builder, uri.Query);

        return builder.ConnectionString;
    }

    private static bool IsPostgresUrl(string value) =>
        value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies <c>?sslmode=require&amp;connect_timeout=10</c> onto the builder. Npgsql understands
    /// the libpq spellings as keyword aliases, so they are applied as given rather than translated.
    /// </summary>
    private static void ApplyQueryParameters(NpgsqlConnectionStringBuilder builder, string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

            try
            {
                builder[key] = value;
            }
            catch (ArgumentException e)
            {
                throw new FormatException(
                    $"{ModbotEnvironment.DatabaseUrlVariable} carries the option '{key}', which "
                    + "Npgsql does not recognise. Remove it from the URL's query string.",
                    e);
            }
        }
    }
}
