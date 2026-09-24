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

    /// <summary>Npgsql's own name for the ceiling, which is what its builder reports a set one under.</summary>
    private const string MaxPoolSizeKeyword = "Maximum Pool Size";

    /// <summary>Connections per processor, when nobody has said otherwise.</summary>
    private const int ConnectionsPerProcessor = 4;

    /// <summary>The fewest connections the ceiling is ever set to, however small the machine.</summary>
    public const int FewestConnections = 20;

    /// <summary>The most, which is also the number Npgsql would have used by itself.</summary>
    public const int MostConnections = 100;

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

    /// <summary>
    /// Adds a ceiling on how many database connections Modbot will hold open at once, unless the
    /// operator already named one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Npgsql's own default is a hundred, which is also PostgreSQL's whole default allowance for
    /// every client on the server. On a machine where Modbot and its database share four slow
    /// cores — a Raspberry Pi, which is a deployment Modbot is meant to fit — Modbot alone could
    /// take the lot, leaving nothing for the log sink, for a psql session, or for the operator
    /// trying to find out what went wrong. Each connection is also a buffer on both sides and a
    /// backend process on the database's, so the ceiling is a memory figure twice over.
    /// </para>
    /// <para>
    /// Four per core, never fewer than twenty and never more than the hundred Npgsql would have
    /// used anyway: a big machine is left exactly as it was, and a small one stops promising more
    /// than it can serve. Nothing waits longer as a result — the work Modbot does at once is
    /// bounded by what the database can answer at once, which on four cores is a handful.
    /// </para>
    /// <para>
    /// An operator who wants a different number says so, and is believed: as
    /// <c>?Maximum Pool Size=200</c> on the URL, or as a keyword in the connection string. This
    /// only fills in an answer where there was none.
    /// </para>
    /// </remarks>
    public static string WithConnectionLimit(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Not something Npgsql can read. Leave it exactly as it is and let the connection
            // attempt report the problem in Npgsql's own words.
            return connectionString;
        }

        // ShouldSerialize, not ContainsKey: Npgsql's builder answers ContainsKey for every keyword
        // it knows about, whether or not this string supplied one, so it can never tell us what
        // the operator actually wrote. ShouldSerialize is true only for keywords that were set,
        // under any of the spellings Npgsql accepts for them.
        if (builder.ShouldSerialize(MaxPoolSizeKeyword))
            return connectionString;

        builder.MaxPoolSize = ConnectionsFor(Environment.ProcessorCount);
        return builder.ConnectionString;
    }

    /// <summary>The ceiling for a machine with this many processors.</summary>
    public static int ConnectionsFor(int processors)
        => Math.Clamp(processors * ConnectionsPerProcessor, FewestConnections, MostConnections);

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
