using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Modbot.Core.Data;

namespace Modbot.Host.Health;

/// <summary>
/// Answers "can Modbot reach its database right now".
/// </summary>
/// <remarks>
/// <para>
/// This is the whole difference between <c>/health/live</c> and <c>/health/ready</c>. Liveness
/// says the process is up and should not be restarted; readiness says it can actually do its job.
/// A platform that cannot tell them apart either restarts a healthy Modbot every time Postgres
/// blinks, or keeps routing traffic to one that can answer nothing.
/// </para>
/// <para>
/// <c>CanConnectAsync</c> rather than a query against a table: the question is reachability, and a
/// readiness probe that runs real work becomes load in the one situation — everything failing —
/// where the database can least afford it.
/// </para>
/// </remarks>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    public const string Name = "database";

    /// <summary>Checks carrying this tag make up <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    private readonly ModbotContext _db;

    public DatabaseHealthCheck(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("Database is reachable.")
                : HealthCheckResult.Unhealthy("Database is not reachable.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Database is not reachable.", e);
        }
    }
}
