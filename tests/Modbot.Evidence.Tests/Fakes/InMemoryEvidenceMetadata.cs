using System.Collections.Concurrent;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;

namespace Modbot.Evidence.Tests.Fakes;

/// <summary>
/// Stands in for the Postgres blob record, which lives behind a migration this project does
/// not own.
/// </summary>
/// <remarks>
/// <para>
/// It records the order it was called in relative to the store, which is what lets the
/// object-before-metadata rule be asserted rather than assumed.
/// </para>
/// <para>
/// Which case files hold a file is not the upload pipeline's business any more: putting a file on
/// a case file is a separate step the API makes once the bytes are recorded. So a test that needs a
/// file to be held says so with <see cref="PutOn"/>, the way the API's own step would, and lets go
/// of it with <see cref="TakeOff"/>. A file can be held by several case files at once, as it can in
/// the real table.
/// </para>
/// </remarks>
public sealed class InMemoryEvidenceMetadata : IEvidenceMetadata
{
    private readonly ConcurrentDictionary<string, List<string>> _holds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _sizes = new(StringComparer.Ordinal);

    public List<EvidenceBlobRecord> Records { get; } = [];

    public List<(string Hash, string Actor, string Reason)> Destroyed { get; } = [];

    /// <summary>Called just before each <see cref="RecordAsync"/>, to check the store's state.</summary>
    public Func<EvidenceBlobRecord, Task>? OnRecording { get; set; }

    public async Task RecordAsync(EvidenceBlobRecord record, CancellationToken ct = default)
    {
        if (OnRecording is not null)
            await OnRecording(record);

        Records.Add(record);
        _sizes[record.Hash.Hex] = record.ByteSize;
    }

    public Task<IReadOnlyList<string>> ReferencesAsync(EvidenceHash hash, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(
            _holds.TryGetValue(hash.Hex, out var reports) ? [.. reports] : []);

    public Task<long> BytesOnReportAsync(string reportId, EvidenceHash? except = null, CancellationToken ct = default)
        => Task.FromResult(
            _holds
                .Where(pair => pair.Value.Contains(reportId) && pair.Key != except?.Hex && !IsDestroyed(pair.Key))
                .Sum(pair => _sizes.GetValueOrDefault(pair.Key)));

    public Task<long> BytesStoredAsync(EvidenceHash? except = null, CancellationToken ct = default)
        => Task.FromResult(
            _sizes
                .Where(pair => pair.Key != except?.Hex && !IsDestroyed(pair.Key))
                .Sum(pair => pair.Value));

    /// <summary>Makes the next marking fail, once, the way a crash after the bytes are deleted would.</summary>
    public bool FailNextMark { get; set; }

    public Task<bool> MarkDestroyedAsync(
        EvidenceHash hash, string actor, string reason, CancellationToken ct = default)
    {
        if (FailNextMark)
        {
            FailNextMark = false;
            throw new IOException("the database went away");
        }

        if (IsDestroyed(hash.Hex))
            return Task.FromResult(false);

        Destroyed.Add((hash.Hex, actor, reason));
        return Task.FromResult(true);
    }

    /// <summary>Simulates the step after a commit: the file goes on a case file.</summary>
    public void PutOn(EvidenceHash hash, string reportId)
    {
        var holds = _holds.GetOrAdd(hash.Hex, _ => []);

        if (!holds.Contains(reportId))
            holds.Add(reportId);
    }

    /// <summary>Simulates a moderator taking the file off one case file.</summary>
    public void TakeOff(EvidenceHash hash, string reportId)
    {
        if (_holds.TryGetValue(hash.Hex, out var reports))
            reports.Remove(reportId);
    }

    private bool IsDestroyed(string hash) => Destroyed.Any(d => d.Hash == hash);
}
