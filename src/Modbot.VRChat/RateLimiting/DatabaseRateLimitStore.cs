using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.VRChat.RateLimiting;

/// <summary>
/// Keeps limiter state in <c>rate_limit_bucket</c>, which is what makes spec 4.3.2 true.
/// </summary>
/// <remarks>
/// A scope per operation rather than an injected context: the limiter is a singleton and the
/// context is scoped, and writes happen on incidents rather than on a request, so there is no
/// ambient scope to borrow. Volume is low by construction — a save happens when a bucket's state
/// actually changes, plus a periodic flush of the token counts.
/// </remarks>
public sealed class DatabaseRateLimitStore(IServiceScopeFactory scopes) : IRateLimitStore
{
    public async Task<IReadOnlyList<RateLimitBucketRecord>> LoadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var rows = await db.RateLimitBuckets
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(ToRecord)];
    }

    public async Task SaveAsync(IReadOnlyList<RateLimitBucketRecord> buckets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        if (buckets.Count == 0)
            return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var names = buckets.Select(b => b.Name).ToList();
        var existing = await db.RateLimitBuckets
            .Where(b => names.Contains(b.Name))
            .ToDictionaryAsync(b => b.Name, ct)
            .ConfigureAwait(false);

        foreach (var record in buckets)
        {
            if (!existing.TryGetValue(record.Name, out var row))
            {
                row = new RateLimitBucket { Name = record.Name };
                db.RateLimitBuckets.Add(row);
            }

            Apply(record, row);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static void Apply(RateLimitBucketRecord record, RateLimitBucket row)
    {
        row.EndpointClass = record.EndpointClass;
        row.ResourceId = record.ResourceId;
        row.CeilingPerSecond = record.CeilingPerSecond;
        row.Fraction = record.Fraction;
        row.BudgetMultiplier = record.BudgetMultiplier;
        row.Tokens = record.Tokens;
        row.TokensAt = record.TokensAt;
        row.StoppedUntil = record.StoppedUntil;
        row.ProbeInFlight = record.ProbeInFlight;
        row.ConsecutiveProbeFailures = record.ConsecutiveProbeFailures;
        row.Alerting = record.Alerting;
        row.LastAdaptedAt = record.LastAdaptedAt;
        row.LastRateLimitedAt = record.LastRateLimitedAt;
        row.RateLimitHits = record.RateLimitHits;
    }

    private static RateLimitBucketRecord ToRecord(RateLimitBucket row) => new()
    {
        Name = row.Name,
        EndpointClass = row.EndpointClass,
        ResourceId = row.ResourceId,
        CeilingPerSecond = row.CeilingPerSecond,
        Fraction = row.Fraction,
        BudgetMultiplier = row.BudgetMultiplier,
        Tokens = row.Tokens,
        TokensAt = row.TokensAt,
        StoppedUntil = row.StoppedUntil,
        ProbeInFlight = row.ProbeInFlight,
        ConsecutiveProbeFailures = row.ConsecutiveProbeFailures,
        Alerting = row.Alerting,
        LastAdaptedAt = row.LastAdaptedAt,
        LastRateLimitedAt = row.LastRateLimitedAt,
        RateLimitHits = row.RateLimitHits,
    };
}
