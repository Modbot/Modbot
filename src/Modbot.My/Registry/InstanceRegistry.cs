using System.Collections.Concurrent;

namespace Modbot.My.Registry;

/// <summary>One registered Modbot deployment. See central services spec section 4.2.</summary>
/// <remarks>
/// The absent fields are the point. There is no group id, no group name, no operator identity and
/// no credential here — not because they are filtered out, but because the record has nowhere to
/// put them.
/// </remarks>
public sealed record RegisteredInstance
{
    /// <summary>Random UUID the deployment assigned itself on first boot.</summary>
    public required string InstanceId { get; init; }

    public required string InstanceUrl { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }

    // ── Usage analytics (section 5.2), all optional: a deployment that declined sends none of it.
    public bool AnalyticsEnabled { get; init; }
    public string? ScaleBucket { get; init; }
    public int? PairedClients { get; init; }
    public bool? DiscordConnected { get; init; }
    public IReadOnlyList<string>? TermListsImported { get; init; }
    public int? RateLimitColdStops { get; init; }
    public int? WafBlocks { get; init; }
}

/// <summary>Registration and usage reporting storage.</summary>
public interface IInstanceRegistry
{
    Task<RegisteredInstance> RegisterAsync(string instanceId, string instanceUrl, string? version, CancellationToken ct = default);
    Task<bool> ReportUsageAsync(string instanceId, UsageReport report, CancellationToken ct = default);
    Task<RegistryTotals> GetTotalsAsync(CancellationToken ct = default);
}

/// <param name="Version">Release the deployment is running.</param>
/// <param name="ScaleBucket">Bucketed member count — never an exact figure.</param>
public sealed record UsageReport(
    string? Version,
    string? ScaleBucket,
    int? PairedClients,
    bool? DiscordConnected,
    IReadOnlyList<string>? TermListsImported,
    int? RateLimitColdStops,
    int? WafBlocks);

/// <summary>
/// Aggregate figures only. Section 4.3: aggregates may be published, the rows may not.
/// </summary>
public sealed record RegistryTotals(int Instances, int ActiveLast30Days, IReadOnlyDictionary<string, int> ByVersion);

/// <summary>
/// In-memory registry. Adequate until Modbot.My gets a database; the interface is what matters, so
/// swapping in Postgres touches one registration line.
/// </summary>
/// <remarks>
/// Deliberately not persisted yet. A registry that loses its contents on restart is a limitation;
/// a half-written persistence layer that silently drops writes would be a defect.
/// </remarks>
public sealed class InMemoryInstanceRegistry : IInstanceRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredInstance> _instances = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public InMemoryInstanceRegistry(TimeProvider time) => _time = time;

    public Task<RegisteredInstance> RegisterAsync(string instanceId, string instanceUrl, string? version, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var record = _instances.AddOrUpdate(
            instanceId,
            _ => new RegisteredInstance
            {
                InstanceId = instanceId,
                InstanceUrl = instanceUrl,
                Version = version,
                RegisteredAt = now,
                LastSeenAt = now,
            },
            // Re-registration updates the URL: deployments move between hosts, and the id is what
            // identifies them across that move.
            (_, existing) => existing with { InstanceUrl = instanceUrl, Version = version ?? existing.Version, LastSeenAt = now });

        return Task.FromResult(record);
    }

    public Task<bool> ReportUsageAsync(string instanceId, UsageReport report, CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var existing))
            return Task.FromResult(false);

        _instances[instanceId] = existing with
        {
            Version = report.Version ?? existing.Version,
            LastSeenAt = _time.GetUtcNow(),
            AnalyticsEnabled = true,
            ScaleBucket = report.ScaleBucket,
            PairedClients = report.PairedClients,
            DiscordConnected = report.DiscordConnected,
            TermListsImported = report.TermListsImported,
            RateLimitColdStops = report.RateLimitColdStops,
            WafBlocks = report.WafBlocks,
        };

        return Task.FromResult(true);
    }

    public Task<RegistryTotals> GetTotalsAsync(CancellationToken ct = default)
    {
        var cutoff = _time.GetUtcNow().AddDays(-30);
        var all = _instances.Values.ToList();

        return Task.FromResult(new RegistryTotals(
            all.Count,
            all.Count(i => i.LastSeenAt >= cutoff),
            all.Where(i => i.Version is not null)
               .GroupBy(i => i.Version!)
               .ToDictionary(g => g.Key, g => g.Count())));
    }
}
