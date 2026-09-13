using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Npgsql;

namespace Modbot.Analytics.Rollups;

/// <inheritdoc />
public sealed class RollupCounter : IRollupCounter
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public RollupCounter(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    public async Task IncrementAsync(
        string metric,
        string? dimension = null,
        decimal amount = 1m,
        DateOnly? day = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);

        // Refused up front, because the failure is otherwise invisible: the count would be right
        // until the next rebuild silently replaced it with whatever the facts said, which for a
        // metric with no facts behind it is nothing.
        if (RollupMetrics.Computed.Contains(metric))
        {
            throw new ArgumentException(
                $"'{metric}' is computed from facts. Counting into it would be overwritten by the "
                + "next rebuild; write a fact instead.",
                nameof(metric));
        }

        var target = day ?? DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);

        var parameters = new NpgsqlParameter[]
        {
            new("day", target),
            new("metric", metric),
            new("dimension", dimension ?? string.Empty),
            new("value", amount),
            new("origin", (short)RollupOrigin.Counted),
        };

        // One statement, so concurrent counters cannot lose an increment between a read and a
        // write. The origin guard in the DO UPDATE means a name that is also computed can never be
        // quietly added to -- the update matches nothing and the check below raises it.
        const string Sql = """
            INSERT INTO modbot_rollup_daily (day, metric, dimension, value, origin)
            VALUES (@day, @metric, @dimension, @value, @origin)
            ON CONFLICT (day, metric, dimension) DO UPDATE
                SET value = modbot_rollup_daily.value + EXCLUDED.value
                WHERE modbot_rollup_daily.origin = @origin
            """;

#pragma warning disable EF1002 // Constant SQL; every value is a parameter.
        var rows = await _db.Database.ExecuteSqlRawAsync(Sql, parameters, ct);
#pragma warning restore EF1002

        if (rows == 0)
        {
            throw new InvalidOperationException(
                $"Rollup '{metric}' on {target:yyyy-MM-dd} is computed from facts and cannot be "
                + "counted into.");
        }
    }
}
