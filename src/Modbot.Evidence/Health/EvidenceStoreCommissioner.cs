using Modbot.Core.Time;
using Modbot.Evidence.Storage;

namespace Modbot.Evidence.Health;

/// <param name="Succeeded">Whether the store may now be saved as the configured backend.</param>
/// <param name="FailedStep">
/// Which step failed, so the message can name credentials, endpoint, URL style, permissions or a
/// read that returned different bytes than were written — rather than "storage error".
/// </param>
/// <param name="Message">What to show the operator.</param>
/// <param name="StoreId">The sentinel id written, on success.</param>
public sealed record CommissioningResult(bool Succeeded, string? FailedStep, string Message, Guid? StoreId);

/// <summary>
/// The round trip a backend must pass before it can be selected (design section 8.5).
/// </summary>
/// <remarks>
/// <para>
/// This is the cheapest detection there is, and none of the section 8 machinery should ever fire
/// because this caught the problem first. Saving a storage backend writes a canary object, reads
/// it back, compares the bytes, promotes it, reads it again at its content-addressed key, deletes
/// it, and only then writes the sentinel and lets the configuration be persisted. A backend that
/// cannot do all of that cannot be chosen.
/// </para>
/// <para>
/// It deliberately exercises the same primitives an upload uses rather than a simpler
/// write-and-read, because the step most likely to fail on an unfamiliar S3 implementation is the
/// server-side copy — and discovering that at the first real upload, after the settings page said
/// everything was fine, is precisely the experience this is meant to prevent.
/// </para>
/// </remarks>
public sealed class EvidenceStoreCommissioner
{
    private readonly IModbotClock _clock;

    public EvidenceStoreCommissioner(IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <param name="store">The store to test, built from the settings the operator just entered.</param>
    /// <param name="storeId">
    /// The sentinel id. Generated fresh when commissioning a new store; passed back in when
    /// re-testing one that is already configured, so a re-run does not orphan the old id.
    /// </param>
    /// <param name="deployment">A human-readable deployment name, for an operator with two buckets.</param>
    public async Task<CommissioningResult> CommissionAsync(
        IEvidenceStore store,
        Guid storeId,
        string? deployment = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var uploadId = EvidenceUploadId.New();
        var canary = CanaryBytes(storeId);
        var expected = EvidenceHash.Compute(canary);

        StagedObject staged;

        try
        {
            await using var body = new MemoryStream(canary, writable: false);
            staged = await store.StageAsync(uploadId, body, canary.Length + 1, canary.Length, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Failed("write", $"Modbot could not write to {store.Description}: {e.Message}");
        }

        try
        {
            if (staged.Hash != expected)
            {
                return Failed(
                    "compare",
                    $"{store.Description} accepted the test object but it did not hash to what was sent. "
                    + "Something between Modbot and the store is altering bytes in transit.");
            }

            var readBack = await ReadAllAsync(await store.OpenStagedAsync(uploadId, ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);

            if (readBack is null)
                return Failed("read-back", $"{store.Description} accepted the test object and then could not find it.");

            if (!readBack.AsSpan().SequenceEqual(canary))
            {
                return Failed(
                    "compare",
                    $"{store.Description} returned different bytes than were written to it.");
            }

            await store.CommitAsync(uploadId, expected, ct).ConfigureAwait(false);

            var stat = await store.StatAsync(expected, ct).ConfigureAwait(false);
            if (stat is null || stat.ByteSize != canary.Length)
            {
                return Failed(
                    "commit",
                    $"{store.Description} could not promote the test object to its final key. On an "
                    + "S3-compatible store this usually means the credentials can write but not copy.");
            }

            var final = await ReadAllAsync(await store.OpenReadAsync(expected, null, ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);

            if (final is null || !final.AsSpan().SequenceEqual(canary))
                return Failed("verify", $"{store.Description} did not return the committed test object intact.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Failed("read-back", $"{store.Description} did not complete the round trip: {e.Message}");
        }
        finally
        {
            await TryCleanUpAsync(store, uploadId, expected, ct).ConfigureAwait(false);
        }

        try
        {
            await store.WriteSentinelAsync(
                new StoreSentinel(storeId, _clock.UtcNow, deployment), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Failed("sentinel", $"Modbot could not write its store marker to {store.Description}: {e.Message}");
        }

        return new CommissioningResult(
            true,
            null,
            $"{store.Description} passed a full write, read, copy and delete round trip, and now carries "
            + $"Modbot's store marker ({storeId}). This store has no backups unless you arranged them, "
            + "and deletions from it cannot be undone — this data is yours to look after.",
            storeId);
    }

    private static CommissioningResult Failed(string step, string message)
        => new(false, step, message, null);

    /// <summary>
    /// Distinct per store id, so two deployments commissioning the same bucket cannot accidentally
    /// read each other's canary and conclude the round trip worked.
    /// </summary>
    private byte[] CanaryBytes(Guid storeId)
        => System.Text.Encoding.UTF8.GetBytes(
            $"modbot evidence store round trip {storeId:N} at {_clock.UtcNow:O}");

    private static async Task<byte[]?> ReadAllAsync(Stream? stream, CancellationToken ct)
    {
        if (stream is null)
            return null;

        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    private static async Task TryCleanUpAsync(
        IEvidenceStore store, EvidenceUploadId uploadId, EvidenceHash hash, CancellationToken ct)
    {
        try
        {
            await store.DeleteStagedAsync(uploadId, ct).ConfigureAwait(false);
            await store.DeleteAsync(hash, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A canary Modbot could not delete is litter, not a failure of the test that matters.
            // Reporting it as one would stop an operator configuring a store that works.
        }
    }
}
