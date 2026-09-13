using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Npgsql;
using Serilog;

namespace Modbot.Host.Startup;

/// <summary>
/// Applies EF Core migrations before Modbot serves its first request.
/// </summary>
/// <remarks>
/// <para>
/// Foundation spec 8.2. A self-hosted appliance must not require the operator to run a migration
/// command: the deployment story is "click the template, open the URL, follow the wizard", and
/// there is no shell in that story.
/// </para>
/// <para>
/// Every failure here is reported as one sentence naming the problem, because the person reading
/// it is looking at a hosting dashboard's log pane. A stack trace in that pane is noise — the
/// operator cannot act on <c>Npgsql.PostgresException</c>, but they can act on "the password in
/// DATABASE_URL was rejected".
/// </para>
/// </remarks>
internal static class DatabaseMigrator
{
    /// <summary>
    /// Connection attempts before giving up. A freshly provisioned Postgres is routinely still
    /// accepting no connections when the app container starts beside it, and crashing on the first
    /// refusal turns an ordinary race into a failed first deploy.
    /// </summary>
    private const int ConnectionAttempts = 10;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Returns a human-readable problem, or null when the database is migrated and usable.
    /// </summary>
    public static async Task<string?> ApplyAsync(
        IServiceProvider services,
        string connectionString,
        Serilog.ILogger log,
        CancellationToken ct = default)
    {
        var target = Describe(connectionString);

        for (var attempt = 1; ; attempt++)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            try
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

                if (pending.Count == 0)
                {
                    log.Information("Database schema is up to date ({Target}).", target);
                    return null;
                }

                log.Information(
                    "Applying {Count} database migration(s) to {Target}: {Migrations}",
                    pending.Count,
                    target,
                    pending);

                await db.Database.MigrateAsync(ct);

                log.Information("Database migrations applied.");
                return null;
            }
            catch (PostgresException e)
            {
                // The server answered and refused. Retrying will not change its mind.
                return Explain(e, target);
            }
            catch (NpgsqlException e) when (attempt < ConnectionAttempts)
            {
                log.Warning(
                    "Database at {Target} is not accepting connections yet (attempt {Attempt} of "
                    + "{Attempts}): {Reason}",
                    target,
                    attempt,
                    ConnectionAttempts,
                    e.Message);

                await Task.Delay(RetryDelay, ct);
            }
            catch (NpgsqlException e)
            {
                return $"Modbot could not reach the database at {target} after {ConnectionAttempts} "
                     + $"attempts: {e.Message} Check {ModbotEnvironment.DatabaseUrlVariable} and "
                     + "that the Postgres service is running.";
            }
            catch (Exception e)
            {
                return $"Applying database migrations to {target} failed: {e.Message} Modbot will "
                     + "not start against a database it cannot migrate, because a half-migrated "
                     + "schema loses data rather than reporting an error.";
            }
        }
    }

    /// <summary>
    /// Host, port and database only. The connection string holds a password, and this text goes
    /// to a log that operators paste into support threads.
    /// </summary>
    private static string Describe(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"{builder.Host}:{builder.Port}/{builder.Database}";
        }
        catch (ArgumentException)
        {
            return "the configured database";
        }
    }

    private static string Explain(PostgresException e, string target) => e.SqlState switch
    {
        // 28P01 invalid_password, 28000 invalid_authorization_specification
        "28P01" or "28000" =>
            $"The database at {target} rejected Modbot's credentials. Check the username and "
            + $"password in {ModbotEnvironment.DatabaseUrlVariable}.",

        // 3D000 invalid_catalog_name
        "3D000" =>
            $"The database named in {ModbotEnvironment.DatabaseUrlVariable} does not exist on "
            + $"{target}. Create it, or point the variable at one that does.",

        // 42501 insufficient_privilege
        "42501" =>
            $"Modbot's database user may connect to {target} but may not change its schema. "
            + "Migrations need CREATE on the target schema; grant it, or use the owning role.",

        _ => $"Applying database migrations to {target} failed: {e.MessageText} "
             + $"(PostgreSQL {e.SqlState}).",
    };
}
