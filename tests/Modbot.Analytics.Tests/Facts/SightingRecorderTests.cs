using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Facts;

/// <summary>
/// The writer tells its sighting recorder about each fact it inserts, and nothing the recorder
/// does may cost the fact: not a failure, not a cancellation, not a caller's open transaction.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SightingRecorderTests : FactTestBase
{
    public SightingRecorderTests(PostgresFixture db) : base(db) { }

    /// <summary>Remembers what it was told, and throws when asked to.</summary>
    private sealed class RecordingRecorder : ISightingRecorder
    {
        public List<(FactRecord Fact, bool CanBeCancelled)> Told { get; } = [];

        public bool Throws { get; init; }

        public Task RecordAsync(FactRecord fact, CancellationToken ct = default)
        {
            Told.Add((fact, ct.CanBeCanceled));

            return Throws
                ? throw new InvalidOperationException("the recorder failed")
                : Task.CompletedTask;
        }
    }

    private static FactRecord Audit(string subject, DateTimeOffset at) => new()
    {
        Type = FactType.MemberBanned,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        Source = FactSource.AuditLog,
        Data = new JsonObject(),
    };

    [Fact]
    public async Task AnInsertedFactIsRecordedAndADeduplicatedReportIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var recorder = new RecordingRecorder();
        var writer = new FactWriter(context, new FakeClock(Now), sightings: recorder);
        var subject = UniqueId("usr");

        var first = await writer.WriteAsync(Presence(subject, Now), ct);
        var second = await writer.WriteAsync(Presence(subject, Now.AddSeconds(2)), ct);
        await writer.WriteAsync(Audit(subject, Now.AddMinutes(1)), ct);

        Assert.False(first.WasDeduplicated);
        Assert.True(second.WasDeduplicated);

        Assert.Equal(2, recorder.Told.Count);
        Assert.Equal(FactType.InstanceJoined, recorder.Told[0].Fact.Type);
        Assert.Equal(FactType.MemberBanned, recorder.Told[1].Fact.Type);
    }

    /// <summary>
    /// The fact is committed by the time the recorder runs; a cancellation arriving then must not
    /// turn a written fact into an error that makes a sync write it again.
    /// </summary>
    [Fact]
    public async Task TheRecorderIsNotGivenTheCallersCancellation()
    {
        await using var context = Db.NewContext();
        var recorder = new RecordingRecorder();
        var writer = new FactWriter(context, new FakeClock(Now), sightings: recorder);
        using var cancellable = new CancellationTokenSource();

        await writer.WriteAsync(Audit(UniqueId("usr"), Now), cancellable.Token);

        Assert.False(Assert.Single(recorder.Told).CanBeCancelled);
    }

    [Fact]
    public async Task ARecorderThatFailsCostsNothingButTheSighting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var recorder = new RecordingRecorder { Throws = true };
        var writer = new FactWriter(context, new FakeClock(Now), sightings: recorder);
        var audited = UniqueId("usr");
        var reported = UniqueId("usr");

        var plain = await writer.WriteAsync(Audit(audited, Now), ct);
        var deduplicated = await writer.WriteAsync(Presence(reported, Now), ct);

        Assert.Equal(2, recorder.Told.Count);
        Assert.Equal(plain.Id, (await context.Events.AsNoTracking().SingleAsync(e => e.SubjectId == audited, ct)).Id);
        Assert.Equal(deduplicated.Id, (await context.Events.AsNoTracking().SingleAsync(e => e.SubjectId == reported, ct)).Id);
    }

    /// <summary>
    /// An import batches thousands of records in one transaction and a moderation action commits
    /// its fact with its own rows. In PostgreSQL a failed statement aborts the whole transaction,
    /// so a recorder failing inside one would fail the caller's next statement and roll back their
    /// facts however well the exception was swallowed. Inside a caller's transaction the writer
    /// therefore does not call it at all; the profile sync's pass records those sightings.
    /// </summary>
    [Fact]
    public async Task InsideACallersTransactionTheRecorderIsSkippedAndTheTransactionStaysUsable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = Db.NewContext();
        var recorder = new RecordingRecorder { Throws = true };
        var writer = new FactWriter(context, new FakeClock(Now), sightings: recorder);
        var audited = UniqueId("usr");
        var reported = UniqueId("usr");
        var later = UniqueId("usr");

        await using (var transaction = await context.Database.BeginTransactionAsync(ct))
        {
            await writer.WriteAsync(Audit(audited, Now), ct);
            await writer.WriteAsync(Presence(reported, Now), ct);

            // The caller's own next statement, which an aborted transaction would refuse.
            await writer.WriteAsync(Audit(later, Now.AddMinutes(1)), ct);

            await transaction.CommitAsync(ct);
        }

        Assert.Empty(recorder.Told);

        await using var check = Db.NewContext();
        Assert.Equal(1, await check.Events.CountAsync(e => e.SubjectId == audited, ct));
        Assert.Equal(1, await check.Events.CountAsync(e => e.SubjectId == reported, ct));
        Assert.Equal(1, await check.Events.CountAsync(e => e.SubjectId == later, ct));
    }
}
