using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;

namespace Modbot.Demo;

/// <summary>
/// Puts a file or two on some of the demo's case files.
/// </summary>
/// <remarks>
/// The bytes go into the database, because a demo has no bucket and should need none (demo mode
/// design §4.6). They are small SVG pictures rather than screenshots, for the same reason the
/// profile pictures are gradients: a demo must not carry a photograph of anybody.
/// </remarks>
public sealed class DemoEvidence
{
    private readonly ModbotContext _db;
    private readonly IEvidenceStore _store;
    private readonly IEvidenceMetadata _metadata;
    private readonly EvidenceOptions _options;

    public DemoEvidence(ModbotContext db, IEvidenceStore store, IEvidenceMetadata metadata, EvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _store = store;
        _metadata = metadata;
        _options = options;
    }

    public async Task WriteAsync(DemoPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // The store marker, which setting a backend up by hand writes and a seeded demo otherwise
        // never has (evidence design §8.2). Without it every piece of evidence reads as "the store
        // holds no store marker" and uploads are refused -- the lost-store lock, correctly applied
        // to a store that was never announced.
        //
        // DemoSeeder deliberately leaves EvidenceStoreId unset for exactly this reason: this is the
        // first place a store id is minted, and it happens in the same breath as the marker that
        // backs it up, so the two are never out of step.
        var settings = await _db.GetSettingsAsync(ct);
        var storeId = settings.EvidenceStoreId ?? Guid.CreateVersion7();

        settings.EvidenceStoreId = storeId;
        await _db.SaveChangesAsync(ct);

        await _store.WriteStoreMarkerAsync(new StoreMarker(storeId, plan.Now, "Modbot demo"), ct);

        // The options object the monitor reads its expected id from was bound from Settings at
        // startup, before this method ever ran -- so it still holds whatever EvidenceStoreId was
        // at that moment (null, on a demo's first boot). Setting it here, directly on the shared
        // instance EvidenceSettingsBinder also writes to, is what lets the re-check below compare
        // against the id that actually now has a marker, instead of the one from before the store
        // existed.
        _options.StoreId = storeId;

        var cases = await _db.CaseFiles.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Take(14)
            .Select(c => new { c.Id, c.AuthorUsername, c.CreatedAt })
            .ToListAsync(ct);

        for (var index = 0; index < cases.Count; index++)
        {
            var file = cases[index];
            var bytes = Encoding.UTF8.GetBytes(Picture(index));

            var uploadId = EvidenceUploadId.New();
            using var body = new MemoryStream(bytes, writable: false);

            var staged = await _store.StageAsync(uploadId, body, bytes.LongLength + 1024, bytes.LongLength, ct);
            await _store.CommitAsync(uploadId, staged.Hash, ct);

            await _metadata.RecordAsync(
                new EvidenceBlobRecord(
                    staged.Hash,
                    staged.ByteSize,
                    "image/svg+xml",
                    EvidenceBackend.Database,
                    file.CreatedAt,
                    $"screenshot-{index + 1}.svg",
                    file.AuthorUsername,
                    file.Id.ToString(),
                    EvidenceOrigin.Uploaded),
                ct);
        }
    }

    /// <summary>A labelled panel, so an opened piece of evidence is obviously a stand-in.</summary>
    private static string Picture(int index) =>
        $"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 360" width="640" height="360">
          <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stop-color="#2b3a55"/><stop offset="1" stop-color="#4a6fa5"/>
          </linearGradient></defs>
          <rect width="640" height="360" fill="url(#g)"/>
          <text x="320" y="180" fill="#eef3fb" font-family="sans-serif" font-size="28"
                text-anchor="middle">Demo evidence {index + 1}</text>
        </svg>
        """;
}
