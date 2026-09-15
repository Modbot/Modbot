using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Npgsql;

namespace Modbot.Analytics.DailyTotals;

/// <inheritdoc />
public sealed class DailyTotalCounter : IDailyTotalCounter
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public DailyTotalCounter(ModbotContext db, IModbotClock clock)
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
        if (DailyTotalMetrics.Computed.Contains(metric))
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
            new("origin", (short)DailyTotalOrigin.Counted),
        };

        // One statement, so concurrent counters cannot lose an increment between a read and a
        // write. The origin guard in the DO UPDATE means a name that is also computed can never be
        // quietly added to -- the update matches nothing and the check below raises it.
        const string Sql = """
            INSERT INTO modbot_daily_total (day, metric, dimension, value, origin)
            VALUES (@day, @metric, @dimension, @value, @origin)
            ON CONFLICT (day, metric, dimension) DO UPDATE
                SET value = modbot_daily_total.value + EXCLUDED.value
                WHERE modbot_daily_total.origin = @origin
            """;

#pragma warning disable EF1002 // Constant SQL; every value is a parameter.
        var rows = await _db.Database.ExecuteSqlRawAsync(Sql, parameters, ct);
#pragma warning restore EF1002

        if (rows == 0)
        {
            throw new InvalidOperationException(
                $"Daily total '{metric}' on {target:yyyy-MM-dd} is computed from facts and cannot be "
                + "counted into.");
        }
    }

    public async Task SetAsync(
        string metric,
        decimal value,
        string? dimension = null,
        DateOnly? day = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);

        if (DailyTotalMetrics.Computed.Contains(metric))
        {
            throw new ArgumentException(
                $"'{metric}' is computed from facts. Setting it would be overwritten by the next rebuild.",
                nameof(metric));
        }

        var target = day ?? DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);

        var parameters = new NpgsqlParameter[]
        {
            new("day", target),
            new("metric", metric),
            new("dimension", dimension ?? string.Empty),
            new("value", value),
            new("origin", (short)DailyTotalOrigin.Counted),
        };

        const string Sql = """
            INSERT INTO modbot_daily_total (day, metric, dimension, value, origin)
            VALUES (@day, @metric, @dimension, @value, @origin)
            ON CONFLICT (day, metric, dimension) DO UPDATE
                SET value = EXCLUDED.value
                WHERE modbot_daily_total.origin = @origin
            """;

#pragma warning disable EF1002 // Constant SQL; every value is a parameter.
        var rows = await _db.Database.ExecuteSqlRawAsync(Sql, parameters, ct);
#pragma warning restore EF1002

        if (rows == 0)
        {
            throw new InvalidOperationException(
                $"Daily total '{metric}' on {target:yyyy-MM-dd} is computed from facts and cannot be set.");
        }
    }
}
