using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Data.Entities;
using Modbot.Evidence.Options;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// Evidence design §7. The blob projection is what makes three things possible that cannot be done
/// any other way: detecting a store that has lost its objects, deleting bytes without deleting
/// somebody else's evidence, and rendering a case file without touching the store at all.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EvidenceMetadataTests(PostgresFixture db)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 4, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeClock _clock = new(Now);

    private DatabaseEvidenceMetadata New(Core.Data.ModbotContext context) => new(context, _clock);

    /// <summary>
    /// A distinct 64-hex hash per test, so tests sharing the fixture's database cannot collide on
    /// a primary key and fail each other.
    /// </summary>
    private static EvidenceHash Hash(char fill) => EvidenceHash.Parse(new string(fill, 64));

    private static EvidenceBlobRecord Record(EvidenceHash hash, string? reportId) => new(
        hash,
        ByteSize: 4096,
        ContentType: "image/png",
        Backend: EvidenceBackend.Filesystem,
        FirstStoredAt: Now,
        FileName: "proof.png",
        UploaderId: "usr_alice",
        ReportId: reportId,
        Origin: EvidenceOrigin.Uploaded);

    [Fact]
    public async Task ABlobIsRecordedWithEverythingButTheBytes()
    {
        await using var context = db.NewContext();
        var key = Hash('a').ToString();
        await New(context).RecordAsync(Record(Hash('a'), "report-1"), Ct);

        var row = await context.EvidenceBlobs.AsNoTracking().Where(b => b.Hash == key).SingleAsync(Ct);

        Assert.Equal(new string('a', 64), row.Hash);
        Assert.Equal(4096, row.ByteSize);
        Assert.Equal("image/png", row.ContentType);
        Assert.Equal("report-1", row.ReportId);
        Assert.False(row.IsDestroyed);
    }

    /// <summary>
    /// The same bytes recorded twice are one row, because the store is content-addressed.
    /// </summary>
    /// <remarks>
    /// A duplicate is the expected case, not an error: the same screenshot attached to two
    /// reports, or a commit retried after a timeout. Treating it as a conflict would fail an
    /// upload that had already succeeded.
    /// </remarks>
    [Fact]
    public async Task TheSameBytesRecordedTwiceAreOneRow()
    {
        await using var context = db.NewContext();
        var metadata = New(context);

        await metadata.RecordAsync(Record(Hash('b'), "report-1"), Ct);
        await metadata.RecordAsync(Record(Hash('b'), "report-1"), Ct);

        var key = Hash('b').ToString();
        Assert.Equal(1, await context.EvidenceBlobs.CountAsync(b => b.Hash == key, Ct));
    }

    /// <summary>
    /// Re-recording never resurrects a destroyed blob.
    /// </summary>
    /// <remarks>
    /// Without this, anybody able to upload could undo a destruction by uploading the same file
    /// again — and the row would then claim bytes exist that were deliberately erased, which is
    /// worse than either state on its own. The bytes are gone; the record of that stays true.
    /// </remarks>
    [Fact]
    public async Task ReRecordingDoesNotUndoADestruction()
    {
        await using var context = db.NewContext();
        var metadata = New(context);

        var key = Hash('c').ToString();
        await metadata.RecordAsync(Record(Hash('c'), "report-1"), Ct);
        await metadata.MarkDestroyedAsync(Hash('c'), "admin", "erasure request", Ct);

        await metadata.RecordAsync(Record(Hash('c'), "report-1"), Ct);

        var row = await context.EvidenceBlobs.AsNoTracking().Where(b => b.Hash == key).SingleAsync(Ct);

        Assert.True(row.IsDestroyed);
        Assert.Equal("admin", row.DestroyedBy);
    }

    /// <summary>
    /// Destruction keeps everything except the bytes.
    /// </summary>
    /// <remarks>
    /// "This case had a video and an administrator destroyed it on this date" has to stay
    /// answerable forever. A case file that looks like it never had evidence is indistinguishable
    /// from one nobody ever documented — which is exactly the ambiguity the record exists to
    /// prevent, and the reason a purge receipt says what it kept.
    /// </remarks>
    [Fact]
    public async Task DestroyingKeepsTheRecordOfWhatWasDestroyed()
    {
        await using var context = db.NewContext();
        var metadata = New(context);

        var key = Hash('d').ToString();
        await metadata.RecordAsync(Record(Hash('d'), "report-9"), Ct);
        await metadata.MarkDestroyedAsync(Hash('d'), "usr_owner", "subject erasure request", Ct);

        var row = await context.EvidenceBlobs.AsNoTracking().Where(b => b.Hash == key).SingleAsync(Ct);

        Assert.Equal(Now, row.DestroyedAt);
        Assert.Equal("usr_owner", row.DestroyedBy);
        Assert.Equal("subject erasure request", row.DestroyedReason);

        // Still knowable: which case it belonged to, how big it was, what kind of file it was.
        Assert.Equal("report-9", row.ReportId);
        Assert.Equal(4096, row.ByteSize);
        Assert.Equal("image/png", row.ContentType);
    }

    /// <summary>The first destruction wins; a later one cannot move the timestamp.</summary>
    /// <remarks>
    /// That timestamp is the moment the bytes stopped existing. A second call is not a second
    /// erasure and must not be able to claim it happened later than it did.
    /// </remarks>
    [Fact]
    public async Task DestroyingTwiceKeepsTheFirstAccount()
    {
        await using var context = db.NewContext();
        var metadata = New(context);

        var key = Hash('e').ToString();
        await metadata.RecordAsync(Record(Hash('e'), "report-1"), Ct);
        await metadata.MarkDestroyedAsync(Hash('e'), "first", "erasure request", Ct);

        _clock.Advance(TimeSpan.FromDays(30));
        await metadata.MarkDestroyedAsync(Hash('e'), "second", "tidying up", Ct);

        var row = await context.EvidenceBlobs.AsNoTracking().Where(b => b.Hash == key).SingleAsync(Ct);

        Assert.Equal(Now, row.DestroyedAt);
        Assert.Equal("first", row.DestroyedBy);
    }

    /// <summary>
    /// Reporting who cites these bytes is what stops one deletion taking another case's evidence.
    /// </summary>
    /// <remarks>
    /// Content addressing means the same screenshot attached to two case files is one object.
    /// Deleting a report must therefore consult this before deleting anything, and the failure
    /// mode of getting it wrong is quiet and unrecoverable: a moderator's evidence disappears
    /// because somebody tidied an unrelated report months later.
    /// </remarks>
    [Fact]
    public async Task ReferencesReportsWhoStillCitesTheBytes()
    {
        await using var context = db.NewContext();
        var metadata = New(context);

        await metadata.RecordAsync(Record(Hash('f'), "report-1"), Ct);

        Assert.Equal(["report-1"], await metadata.ReferencesAsync(Hash('f'), Ct));
    }

    /// <summary>
    /// A destroyed blob cites nothing.
    /// </summary>
    /// <remarks>
    /// Its bytes are already gone, so it cannot be a reason to keep an object alive. Counting it
    /// would make the object permanently undeletable after the one report citing it was erased —
    /// a store that can only ever grow.
    /// </remarks>
    [Fact]
    public async Task ADestroyedBlobIsNotAReferenceKeepingBytesAlive()
    {
        await using var context = db.NewContext();
        var metadata = New(context);

        await metadata.RecordAsync(Record(Hash('1'), "report-1"), Ct);
        await metadata.MarkDestroyedAsync(Hash('1'), "admin", "erasure request", Ct);

        Assert.Empty(await metadata.ReferencesAsync(Hash('1'), Ct));
    }

    /// <summary>Bytes nothing has recorded cite nothing, rather than throwing.</summary>
    [Fact]
    public async Task AnUnknownHashHasNoReferences()
    {
        await using var context = db.NewContext();

        Assert.Empty(await New(context).ReferencesAsync(Hash('9'), Ct));
    }
}
