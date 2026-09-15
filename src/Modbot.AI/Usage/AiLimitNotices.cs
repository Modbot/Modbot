using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.AI.Usage;

/// <summary>
/// Where alerts about AI spend go besides the fact log. Email alerts plug in here when Modbot Cloud
/// sends them (AI chat design §10.7).
/// </summary>
/// <remarks>
/// Called once per limit per UTC day or month, straight after the fact is recorded, from the
/// request that was stopped. An implementation that sends anything should hand it off rather than
/// wait on it, because that request is waiting.
/// </remarks>
public interface IAiSpendAlerts
{
    Task LimitReachedAsync(AiLimitReached reached, CancellationToken ct);
}

/// <summary>No alerts beyond the fact log. What every deployment has today.</summary>
public sealed class NoAiSpendAlerts : IAiSpendAlerts
{
    public Task LimitReachedAsync(AiLimitReached reached, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Records that a spend limit was reached: one fact per limit per day or month.</summary>
public sealed class AiLimitNotices
{
    /// <summary>How long a record of a reached limit is kept. Longer than any period it guards.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(70);

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly IAiSpendAlerts _alerts;
    private readonly ILogger _log;

    public AiLimitNotices(
        ModbotContext db, IFactWriter facts, EventPartitionMaintainer partitions, IModbotClock clock, IAiSpendAlerts alerts)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(alerts);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _alerts = alerts;
        _log = Log.Logger.ForContext(LogArea.Name, LogArea.Setup);
    }

    /// <summary>
    /// Writes the fact and tells <see cref="IAiSpendAlerts"/>, unless this limit was already recorded
    /// in this period. Never throws for a failure to record: the call is refused either way.
    /// </summary>
    public async Task RecordOnceAsync(AiLimitReached reached, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reached);

        var now = _clock.UtcNow;

        try
        {
            // The insert is the claim, so two requests stopped at the same moment write one fact.
            var claimed = await _db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO ai_limit_reached (key, period_start, reached_at) VALUES ({reached.Key}, {reached.PeriodStart.ToUniversalTime()}, {now.ToUniversalTime()}) ON CONFLICT DO NOTHING",
                ct).ConfigureAwait(false);

            if (claimed == 0)
                return;

            var old = now - KeptFor;
            await _db.AiLimitsReached.Where(r => r.PeriodStart < old).ExecuteDeleteAsync(ct).ConfigureAwait(false);

            await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
            await _facts.WriteAsync(new FactRecord
            {
                Type = FactType.AiLimitReached,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = reached.Key[..reached.Key.LastIndexOf(':')],
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["appliesTo"] = reached.AppliesTo,
                    ["feature"] = reached.Feature,
                    ["name"] = reached.Name,
                    ["userId"] = reached.UserId?.ToString(),
                    ["period"] = reached.Period,
                    ["unit"] = reached.Unit,
                    ["limit"] = reached.Limit,
                    ["spent"] = reached.Spent,
                    ["message"] = reached.Message,
                },
            }, ct).ConfigureAwait(false);

            await _alerts.LimitReachedAsync(reached, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _log.Warning(e, "Could not record that the AI spend limit {Key} was reached", reached.Key);
        }
    }
}
