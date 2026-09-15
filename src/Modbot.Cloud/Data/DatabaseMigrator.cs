using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Engine;
using Npgsql;

namespace Modbot.Cloud.Data;

/// <summary>
/// Applies both databases' migrations before the first request, each on its own.
/// </summary>
internal static class DatabaseMigrator
{
    /// <summary>
    /// A freshly provisioned Postgres often refuses connections for a few seconds after the app
    /// container starts beside it.
    /// </summary>
    private const int ConnectionAttempts = 10;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Returns one sentence describing the problem, or null when both schemas are ready.</summary>
    public static async Task<string?> ApplyAsync(IServiceProvider services, ILogger log, CancellationToken ct = default) =>
        await ApplyAsync<CloudContext>(services, log, "main", ct)
        ?? await ApplyAsync<EngineContext>(services, log, "engine", ct);

    private static async Task<string?> ApplyAsync<TContext>(IServiceProvider services, ILogger log, string which, CancellationToken ct)
        where TContext : DbContext
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TContext>();

            try
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

                if (pending.Count > 0)
                {
                    log.LogInformation("Applying {Count} {Which} database migration(s): {Migrations}", pending.Count, which, pending);
                    await db.Database.MigrateAsync(ct);
                }

                return null;
            }
            catch (PostgresException e)
            {
                return $"The {which} database refused Modbot Cloud: {e.MessageText}";
            }
            catch (NpgsqlException e) when (attempt < ConnectionAttempts)
            {
                log.LogWarning(
                    "Could not reach the {Which} database ({Message}); trying again ({Attempt} of {Attempts})",
                    which, e.Message, attempt, ConnectionAttempts);

                await Task.Delay(RetryDelay, ct);
            }
            catch (NpgsqlException e)
            {
                return $"Could not reach the {which} database after {ConnectionAttempts} attempts: {e.Message}";
            }
        }
    }
}
