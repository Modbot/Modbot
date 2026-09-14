using Modbot.Core.Time;
using Modbot.Evidence.Storage;

namespace Modbot.Evidence.Health;

/// <param name="Succeeded">Whether the store may now be saved as the configured backend.</param>
/// <param name="FailedStep">
/// Which step failed, so the message can name credentials, endpoint, URL style, permissions or a
/// read that returned different bytes than were written — rather than "storage error".
/// </param>
/// <param name="Message">What to show the operator.</param>
/// <param name="StoreId">The store marker id written, on success.</param>
public sealed record SetupResult(bool Succeeded, string? FailedStep, string Message, Guid? StoreId);

/// <summary>
/// The round trip a backend must pass before it can be selected (design section 8.5).
/// </summary>
/// <remarks>
/// <para>
/// This is the cheapest detection there is, and none of the section 8 machinery should ever fire
/// because this caught the problem first. Saving a storage backend writes a test file, reads
/// it back, compares the bytes, promotes it, reads it again at its content-addressed key, deletes
/// it, and only then writes the store marker and lets the configuration be persisted. A backend that
/// cannot do all of that cannot be chosen.
/// </para>
/// <para>
/// It deliberately exercises the same primitives an upload uses rather than a simpler
/// write-and-read, because the step most likely to fail on an unfamiliar S3 implementation is the
/// server-side copy — and discovering that at the first real upload, after the settings page said
/// everything was fine, is precisely the experience this is meant to prevent.
/// </para>
/// </remarks>
public sealed class EvidenceStoreSetup
{
    private readonly IModbotClock _clock;

    public EvidenceStoreSetup(IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <param name="store">The store to test, built from the settings the operator just entered.</param>
    /// <param name="storeId">
    /// The store marker id. Generated fresh when setting up a new store; passed back in when
    /// re-testing one that is already configured, so a re-run does not orphan the old id.
    /// </param>
    /// <param name="deployment">A human-readable deployment name, for an operator with two buckets.</param>
    public async Task<SetupResult> SetUpAsync(
        IEvidenceStore store,
        Guid storeId,
        string? deployment = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var uploadId = EvidenceUploadId.New();
        var testFile = TestFileBytes(storeId);
        var expected = EvidenceHash.Compute(testFile);

        StagedObject staged;

        try
        {
            await using var body = new MemoryStream(testFile, writable: false);
            staged = await store.StageAsync(uploadId, body, testFile.Length + 1, testFile.Length, ct)
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

            if (!readBack.AsSpan().SequenceEqual(testFile))
            {
                return Failed(
                    "compare",
                    $"{store.Description} returned different bytes than were written to it.");
            }

            await store.CommitAsync(uploadId, expected, ct).ConfigureAwait(false);

            var stat = await store.StatAsync(expected, ct).ConfigureAwait(false);
            if (stat is null || stat.ByteSize != testFile.Length)
            {
                return Failed(
                    "commit",
                    $"{store.Description} could not promote the test object to its final key. On an "
                    + "S3-compatible store this usually means the credentials can write but not copy.");
            }

            var final = await ReadAllAsync(await store.OpenReadAsync(expected, null, ct).ConfigureAwait(false), ct)
                .ConfigureAwait(false);

            if (final is null || !final.AsSpan().SequenceEqual(testFile))
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
            await store.WriteStoreMarkerAsync(
                new StoreMarker(storeId, _clock.UtcNow, deployment), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Failed("store-marker", $"Modbot could not write its store marker to {store.Description}: {e.Message}");
        }

        return new SetupResult(
            true,
            null,
            $"{store.Description} passed. Store marker {storeId}.",
            storeId);
    }

    private static SetupResult Failed(string step, string message)
        => new(false, step, message, null);

    /// <summary>
    /// Distinct per store id, so two deployments setting up the same bucket cannot accidentally
    /// read each other's test file and conclude the round trip worked.
    /// </summary>
    private byte[] TestFileBytes(Guid storeId)
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
            // A test file Modbot could not delete is litter, not a failure of the test that matters.
            // Reporting it as one would stop an operator configuring a store that works.
        }
    }
}
