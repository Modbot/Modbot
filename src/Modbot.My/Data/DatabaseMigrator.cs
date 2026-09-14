using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Modbot.My.Data;

/// <summary>
/// Applies migrations before the first request, the same way the main Modbot host does.
/// </summary>
internal static class DatabaseMigrator
{
    /// <summary>
    /// A freshly provisioned Postgres often refuses connections for a few seconds after the app
    /// container starts beside it.
    /// </summary>
    private const int ConnectionAttempts = 10;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Returns one sentence describing the problem, or null when the schema is ready.</summary>
    public static async Task<string?> ApplyAsync(IServiceProvider services, ILogger log, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MyContext>();

            try
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

                if (pending.Count > 0)
                {
                    log.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, pending);
                    await db.Database.MigrateAsync(ct);
                }

                return null;
            }
            catch (PostgresException e)
            {
                // The server answered and refused. Retrying will not change its mind.
                return $"The database refused Modbot.My: {e.MessageText}";
            }
            catch (NpgsqlException e) when (attempt < ConnectionAttempts)
            {
                log.LogWarning(
                    "Could not reach the database ({Message}); trying again ({Attempt} of {Attempts})",
                    e.Message,
                    attempt,
                    ConnectionAttempts);

                await Task.Delay(RetryDelay, ct);
            }
            catch (NpgsqlException e)
            {
                return $"Could not reach the database after {ConnectionAttempts} attempts: {e.Message}";
            }
        }
    }
}
