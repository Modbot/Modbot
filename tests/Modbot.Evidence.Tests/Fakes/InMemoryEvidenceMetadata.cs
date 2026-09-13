using System.Collections.Concurrent;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;

namespace Modbot.Evidence.Tests.Fakes;

/// <summary>
/// Stands in for the Postgres blob record, which lives behind a migration this project does
/// not own.
/// </summary>
/// <remarks>
/// It records the order it was called in relative to the store, which is what lets the
/// object-before-metadata rule be asserted rather than assumed.
/// </remarks>
public sealed class InMemoryEvidenceMetadata : IEvidenceMetadata
{
    private readonly ConcurrentDictionary<string, List<string>> _references = new(StringComparer.Ordinal);

    public List<EvidenceBlobRecord> Records { get; } = [];

    public List<(string Hash, string Actor, string Reason)> Destroyed { get; } = [];

    /// <summary>Called just before each <see cref="RecordAsync"/>, to check the store's state.</summary>
    public Func<EvidenceBlobRecord, Task>? OnRecording { get; set; }

    public async Task RecordAsync(EvidenceBlobRecord record, CancellationToken ct = default)
    {
        if (OnRecording is not null)
            await OnRecording(record);

        Records.Add(record);

        if (record.ReportId is { } report)
            _references.GetOrAdd(record.Hash.Hex, _ => []).Add(report);
    }

    public Task<IReadOnlyList<string>> ReferencesAsync(EvidenceHash hash, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(
            _references.TryGetValue(hash.Hex, out var reports) ? [.. reports] : []);

    public Task MarkDestroyedAsync(
        EvidenceHash hash, string actor, string reason, CancellationToken ct = default)
    {
        Destroyed.Add((hash.Hex, actor, reason));
        return Task.CompletedTask;
    }

    /// <summary>Simulates a moderator detaching the file from one report.</summary>
    public void Detach(EvidenceHash hash, string reportId)
    {
        if (_references.TryGetValue(hash.Hex, out var reports))
            reports.Remove(reportId);
    }
}
