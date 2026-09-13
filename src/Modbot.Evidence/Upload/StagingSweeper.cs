using Modbot.Core.Time;
using Modbot.Evidence.Health;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;

namespace Modbot.Evidence.Upload;

/// <param name="Swept">The uploads whose staging objects were removed.</param>
/// <param name="Skipped">Why nothing was swept, when nothing was.</param>
public sealed record SweepResult(IReadOnlyList<EvidenceUploadId> Swept, string? Skipped);

/// <summary>
/// Removes staging objects from uploads nobody finished (design section 9.5).
/// </summary>
/// <remarks>
/// <para>
/// This is a job Modbot runs, not a bucket setting an operator configures, because there is no
/// bucket setting to lean on: Railway Buckets supports no lifecycle configuration at all. Building
/// it the other way would have produced a design that worked on AWS and quietly leaked objects on
/// the platform the one-click template targets.
/// </para>
/// <para>
/// The grace period is long on purpose — a slow upload on a bad connection must never be swept out
/// from under itself — and the sweep refuses to run at all while the store's state is unresolved.
/// Deleting on the authority of a database whose relationship to the store is in question is the
/// second-worst bug available here.
/// </para>
/// </remarks>
public sealed class StagingSweeper
{
    private readonly IEvidenceStore _store;
    private readonly IEvidenceUploadRegistry _registry;
    private readonly EvidenceStoreMonitor _monitor;
    private readonly EvidenceOptions _options;
    private readonly IModbotClock _clock;

    public StagingSweeper(
        IEvidenceStore store,
        IEvidenceUploadRegistry registry,
        EvidenceStoreMonitor monitor,
        EvidenceOptions options,
        IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _registry = registry;
        _monitor = monitor;
        _options = options;
        _clock = clock;
    }

    public async Task<SweepResult> SweepAsync(CancellationToken ct = default)
    {
        var health = _monitor.Current;
        if (!health.SweepAllowed)
            return new SweepResult([], "The sweep did not run: " + health.Explanation);

        var cutoff = _clock.UtcNow - _options.StagingGrace;
        var abandoned = await _registry.FindAbandonedAsync(cutoff, ct).ConfigureAwait(false);

        var swept = new List<EvidenceUploadId>();

        foreach (var upload in abandoned)
        {
            await _store.DeleteStagedAsync(upload.Id, ct).ConfigureAwait(false);
            await _registry.RemoveAsync(upload.Id, ct).ConfigureAwait(false);
            swept.Add(upload.Id);
        }

        return new SweepResult(swept, null);
    }
}
