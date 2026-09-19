using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Facts;

/// <summary>
/// The writer's "do I already have this" check, asked on its own (import design §6.1).
/// </summary>
/// <remarks>
/// It is the same query the deduplicated ingest path runs, exposed so a caller that has to decide
/// before it writes can ask it -- an import's dry run writes nothing and still has to report what
/// it would skip. The window is the caller's, and an import passes zero.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class AlreadyRecordedTests : FactTestBase
{
    public AlreadyRecordedTests(PostgresFixture db) : base(db) { }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NothingRecorded_IsNull()
    {
        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        Assert.Null(await writer.AlreadyRecordedAsync(Ban(UniqueId("usr"), Now), TimeSpan.Zero, Ct));
    }

    [Fact]
    public async Task TheSameEventAtTheSameInstant_IsFound_WhateverElseDiffers()
    {
        var subject = UniqueId("usr");

        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        var written = await writer.WriteAsync(Ban(subject, Now, "VRChat's own wording"), Ct);

        // Neither the payload nor the source is compared: an old platform's ban reason will never
        // match VRChat's, and requiring it to would mean nothing was ever recognised.
        var found = await writer.AlreadyRecordedAsync(
            Ban(subject, Now, "the old bot's wording") with { Source = FactSource.Manual },
            TimeSpan.Zero,
            Ct);

        Assert.Equal(written.Id, found);
    }

    /// <summary>
    /// Why an import passes a window of zero. A spreadsheet that dates warnings only to the day
    /// gives every warning that day the same midnight, and any window at all would make the
    /// second one look like the first.
    /// </summary>
    [Fact]
    public async Task OneSecondApart_IsNotTheSameEvent_AtAWindowOfZero()
    {
        var subject = UniqueId("usr");

        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        await writer.WriteAsync(Ban(subject, Now), Ct);

        Assert.Null(await writer.AlreadyRecordedAsync(Ban(subject, Now.AddSeconds(1)), TimeSpan.Zero, Ct));
        Assert.NotNull(await writer.AlreadyRecordedAsync(Ban(subject, Now.AddSeconds(1)), TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task ADifferentTypeOrADifferentPerson_IsADifferentEvent()
    {
        var subject = UniqueId("usr");
        var somebodyElse = UniqueId("usr");

        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        await writer.WriteAsync(Ban(subject, Now), Ct);

        Assert.Null(await writer.AlreadyRecordedAsync(
            Ban(subject, Now) with { Type = FactType.MemberKicked }, TimeSpan.Zero, Ct));

        Assert.Null(await writer.AlreadyRecordedAsync(Ban(somebodyElse, Now), TimeSpan.Zero, Ct));
    }

    /// <summary>
    /// The bug behind the paired audit entries: one kick recorded twice, once knowing the
    /// instance and once not. An imported record never carries an instance, so before this the
    /// check could never fire for anything that happened in one.
    /// </summary>
    [Fact]
    public async Task AFactThatKnowsTheInstance_AndOneThatDoesNot_AreTheSameEvent()
    {
        var kicked = UniqueId("usr");
        var alsoKicked = UniqueId("usr");

        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        // Modbot read this kick from VRChat's own audit log, which names the instance.
        var inAnInstance = await writer.WriteAsync(
            Ban(kicked, Now) with { Type = FactType.GroupInstanceKick, WorldId = "wrld_test", InstanceId = "11032" },
            Ct);

        Assert.Equal(
            inAnInstance.Id,
            await writer.AlreadyRecordedAsync(Ban(kicked, Now) with { Type = FactType.GroupInstanceKick }, TimeSpan.Zero, Ct));

        // And the other way round: what is stored is quiet about the instance, what arrives
        // names one.
        var nowhereInParticular = await writer.WriteAsync(
            Ban(alsoKicked, Now) with { Type = FactType.GroupInstanceKick },
            Ct);

        Assert.Equal(
            nowhereInParticular.Id,
            await writer.AlreadyRecordedAsync(
                Ban(alsoKicked, Now) with { Type = FactType.GroupInstanceKick, WorldId = "wrld_test", InstanceId = "11032" },
                TimeSpan.Zero,
                Ct));
    }

    [Fact]
    public async Task TwoInstancesBothKnown_AndDifferent_AreTwoEvents()
    {
        var subject = UniqueId("usr");

        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        await writer.WriteAsync(
            Ban(subject, Now) with { Type = FactType.GroupInstanceKick, WorldId = "wrld_test", InstanceId = "11032" },
            Ct);

        Assert.Null(await writer.AlreadyRecordedAsync(
            Ban(subject, Now) with { Type = FactType.GroupInstanceKick, WorldId = "wrld_test", InstanceId = "11033" },
            TimeSpan.Zero,
            Ct));

        // A different world is a different event too, even where the instance number repeats.
        Assert.Null(await writer.AlreadyRecordedAsync(
            Ban(subject, Now) with { Type = FactType.GroupInstanceKick, WorldId = "wrld_elsewhere", InstanceId = "11032" },
            TimeSpan.Zero,
            Ct));
    }

    [Fact]
    public async Task ItWritesNothing()
    {
        var subject = UniqueId("usr");

        await using var context = Db.NewContext();
        var writer = NewWriter(context, new FakeClock(Now));

        await writer.AlreadyRecordedAsync(Ban(subject, Now), TimeSpan.Zero, Ct);
        await writer.AlreadyRecordedAsync(Ban(subject, Now), TimeSpan.Zero, Ct);

        await using var reading = Db.NewContext();
        Assert.Empty(reading.Events.Where(e => e.SubjectId == subject));
    }

    private static FactRecord Ban(string subjectId, DateTimeOffset at, string? reason = null) => new()
    {
        Type = FactType.MemberBanned,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subjectId,
        Source = FactSource.AuditLog,
        Data = reason is null
            ? null
            : new System.Text.Json.Nodes.JsonObject { ["reason"] = reason },
    };
}
