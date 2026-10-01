using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// Modbot's "ended on its own" entry: written for an instance that left the group's list and had no
/// close by hand, and only once the audit log has been read past its end.
/// </summary>
/// <remarks>
/// The audit log's own reading is stood in for by <see cref="Settings.AuditLogReadToEndAt"/>, the
/// moment the last complete pass began, which is exactly what the check reads.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class InstanceEndFactsTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    private const string World = "wrld_home";

    private static readonly TimeSpan Wait = InstanceEndFacts.Wait + TimeSpan.FromMinutes(1);

    /// <summary>An instance of the group that opened and left the list, as the instance poll leaves it.</summary>
    private async Task<VRChatInstance> EndedInstanceAsync(
        string number, DateTimeOffset openedAt, DateTimeOffset closedAt, string groupId = GroupId, bool closed = true)
    {
        await using var db = Database.NewContext();

        var instance = new VRChatInstance
        {
            Id = Guid.NewGuid(),
            Location = $"{World}:{number}~group({groupId})~groupAccessType(members)~region(us)",
            WorldId = World,
            VRChatInstanceId = number,
            GroupId = groupId,
            Type = "group",
            GroupAccessType = "members",
            OpenedAt = openedAt,
            LastSeenAt = closed ? closedAt : openedAt,
            ClosedAt = closed ? closedAt : null,
            ClosedBy = closed ? "list" : null,
            SeenInGroupList = true,
        };

        db.VRChatInstances.Add(instance);
        await db.SaveChangesAsync(Ct);
        return instance;
    }

    private async Task AuditLogReadToAsync(DateTimeOffset? readToEnd)
    {
        await using var db = Database.NewContext();
        var settings = await db.GetSettingsAsync(Ct);
        settings.AuditLogReadToEndAt = readToEnd;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Fixes the line between history and news: what ended before it is caught up, not posted.</summary>
    private async Task EntriesBeganAsync(DateTimeOffset at)
    {
        await using var db = Database.NewContext();
        var settings = await db.GetSettingsAsync(Ct);
        settings.InstanceEndEntriesStartedAt = at;
        await db.SaveChangesAsync(Ct);
    }

    private async Task WriteCloseEntryAsync(string number, DateTimeOffset at)
    {
        await using var db = Database.NewContext();
        var partitions = new EventPartitionMaintainer(db, Clock);
        await partitions.EnsureForAsync(at, Ct);

        await new FactWriter(db, Clock).WriteAsync(new FactRecord
        {
            Type = FactType.GroupInstanceClosed,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = $"{World}:{number}",
            ActorPlatform = FactPlatform.VRChat,
            ActorId = "usr_mod",
            WorldId = World,
            InstanceId = number,
            Source = FactSource.AuditLog,
            Data = new JsonObject(),
        }, Ct);
    }

    private async Task<int> SettleAsync()
    {
        await using var db = Database.NewContext();
        var settings = await db.GetSettingsAsync(Ct);

        var ends = new InstanceEndFacts(db, new FactWriter(db, Clock), new EventPartitionMaintainer(db, Clock));
        return await ends.SettleAsync(GroupId, settings, Clock.UtcNow, Ct);
    }

    /// <summary>The payload's <c>catchUp</c>, read as JSON: the database's own spacing is not the writer's.</summary>
    private static bool IsCatchUp(ModbotEvent entry)
        => JsonNode.Parse(entry.Data) is JsonObject data && data["catchUp"]?.GetValue<bool>() == true;

    private async Task<List<ModbotEvent>> EntriesAsync()
    {
        await using var db = Database.NewContext();
        return await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.InstanceEndedOnItsOwn)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task NothingIsWritten_UntilTheAuditLogHasBeenReadPastTheEnd()
    {
        var closedAt = Now.AddMinutes(-30);
        await EndedInstanceAsync("1", closedAt.AddMinutes(-40), closedAt);

        // Never read to the end: nothing is known about who closed what.
        await AuditLogReadToAsync(null);
        Assert.Equal(0, await SettleAsync());

        // Read to the end, but not past the end and the few minutes a close entry may be dated after it.
        await AuditLogReadToAsync(closedAt.AddMinutes(2));
        Assert.Equal(0, await SettleAsync());
        Assert.Empty(await EntriesAsync());

        await AuditLogReadToAsync(closedAt + Wait);
        Assert.Equal(1, await SettleAsync());
    }

    [Fact]
    public async Task TheEntry_IsAtTheEndTimeModbotRecorded_AboutTheLocation_WithNobodyNamed()
    {
        var closedAt = Now.AddMinutes(-30);
        var instance = await EndedInstanceAsync("1", closedAt.AddMinutes(-40), closedAt);
        await EntriesBeganAsync(Now.AddDays(-1));
        await AuditLogReadToAsync(closedAt + Wait);

        await SettleAsync();

        var entry = Assert.Single(await EntriesAsync());
        Assert.Equal(closedAt, entry.OccurredAt);
        Assert.Equal(instance.Location, entry.SubjectId);
        Assert.Equal(FactPlatform.VRChat, entry.SubjectPlatform);
        Assert.Equal(World, entry.WorldId);
        Assert.Equal("1", entry.InstanceId);
        Assert.Null(entry.ActorId);
        Assert.Equal(FactSource.SyncDiff, entry.Source);
        Assert.False(IsCatchUp(entry));
    }

    [Fact]
    public async Task AnInstanceAModeratorClosedByHand_GetsNoEntry_EvenWhenTheCloseArrivesLate()
    {
        var closedAt = Now.AddMinutes(-30);
        await EndedInstanceAsync("1", closedAt.AddMinutes(-40), closedAt);

        // The audit log has not read past the end yet: the instance waits.
        await AuditLogReadToAsync(closedAt.AddMinutes(1));
        Assert.Equal(0, await SettleAsync());

        // VRChat reported the close late; it is dated just before Modbot saw the instance leave the list.
        await WriteCloseEntryAsync("1", closedAt.AddSeconds(-20));
        await AuditLogReadToAsync(closedAt + Wait);

        Assert.Equal(0, await SettleAsync());
        Assert.Empty(await EntriesAsync());

        // And it is settled: it is not looked at again.
        await using var db = Database.NewContext();
        Assert.NotNull((await db.VRChatInstances.AsNoTracking().SingleAsync(Ct)).EndRecordedAt);
    }

    [Fact]
    public async Task AnEntryIsWrittenOnce_HoweverManyPassesRun()
    {
        var closedAt = Now.AddMinutes(-30);
        await EndedInstanceAsync("1", closedAt.AddMinutes(-40), closedAt);
        await AuditLogReadToAsync(closedAt + Wait);

        Assert.Equal(1, await SettleAsync());
        Assert.Equal(0, await SettleAsync());
        Assert.Equal(0, await SettleAsync());

        Assert.Single(await EntriesAsync());
    }

    [Fact]
    public async Task APassThatStoppedBetweenTheEntryAndTheMark_DoesNotWriteItTwice()
    {
        var closedAt = Now.AddMinutes(-30);
        var instance = await EndedInstanceAsync("1", closedAt.AddMinutes(-40), closedAt);
        await AuditLogReadToAsync(closedAt + Wait);
        Assert.Equal(1, await SettleAsync());

        // The mark is lost, as if the pass had stopped before saving it.
        await using (var db = Database.NewContext())
        {
            var row = await db.VRChatInstances.SingleAsync(i => i.Id == instance.Id, Ct);
            row.EndRecordedAt = null;
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(0, await SettleAsync());
        Assert.Single(await EntriesAsync());
    }

    [Fact]
    public async Task CatchUp_WritesEndedInstancesAtTheirRealEndTime_MarkedAsCatchUp()
    {
        var oldEnd = Now.AddDays(-3);
        var freshEnd = Now.AddMinutes(-10);

        await EndedInstanceAsync("1", oldEnd.AddHours(-1), oldEnd);
        await EndedInstanceAsync("2", freshEnd.AddHours(-1), freshEnd);

        // Entries began 20 minutes ago, so the old end is history and the fresh one is news,
        // however long the audit log took to be read past it.
        await EntriesBeganAsync(Now.AddMinutes(-20));
        await AuditLogReadToAsync(Now);

        Assert.Equal(2, await SettleAsync());

        var entries = await EntriesAsync();
        Assert.Equal([oldEnd, freshEnd], entries.Select(e => e.OccurredAt));

        // The old one is history arriving late and is not posted; the recent one is news.
        Assert.True(IsCatchUp(entries[0]));
        Assert.False(IsCatchUp(entries[1]));
    }

    [Fact]
    public async Task AnOpenInstance_AndAnotherGroupsInstance_GetNothing()
    {
        var closedAt = Now.AddMinutes(-30);
        await EndedInstanceAsync("1", closedAt.AddMinutes(-40), closedAt, closed: false);
        await EndedInstanceAsync("2", closedAt.AddMinutes(-40), closedAt, groupId: "grp_other");
        await AuditLogReadToAsync(Now);

        Assert.Equal(0, await SettleAsync());
        Assert.Empty(await EntriesAsync());
    }

    [Fact]
    public async Task ANumberHandedOutAgain_IsTwoInstances_EachJudgedOnItsOwn()
    {
        var firstEnd = Now.AddHours(-6);
        var secondEnd = Now.AddHours(-2);

        await EndedInstanceAsync("7", firstEnd.AddHours(-1), firstEnd);
        await EndedInstanceAsync("7", secondEnd.AddHours(-1), secondEnd);

        // Only the second was closed by hand.
        await WriteCloseEntryAsync("7", secondEnd.AddSeconds(-10));
        await AuditLogReadToAsync(Now);

        Assert.Equal(1, await SettleAsync());

        var entry = Assert.Single(await EntriesAsync());
        Assert.Equal(firstEnd, entry.OccurredAt);
    }

    private static string Listed(string number) => $"{World}:{number}~group({GroupId})~groupAccessType(members)~region(us)";

    /// <summary>One poll of the group's list, with the writer, and the clock moved on afterwards.</summary>
    private async Task PollAsync(TimeSpan thenWait)
    {
        await using (var db = Database.NewContext())
        {
            var ends = new InstanceEndFacts(db, new FactWriter(db, Clock), new EventPartitionMaintainer(db, Clock));
            await new GroupInstanceSync(Gate, new PlaceStore(db, Clock), db, Clock, ends: ends).RunOnceAsync(Ct);
        }

        Clock.Advance(thenWait);
    }

    /// <summary>
    /// The poll undoes an end when the instance is listed again within ten minutes, so an end is not
    /// settled inside that window: an instance that dropped off the list and came back never ended,
    /// and "ended on its own" would be false about it. The real end, later, gets the one entry.
    /// </summary>
    [Fact]
    public async Task AnInstanceThatComesBackWithinTheReopenWindow_GetsNoEntryForTheFirstEnd_AndOneForTheRealEnd()
    {
        await EntriesBeganAsync(Now.AddDays(-1));
        var location = Listed("1");

        // Listed, then off the list.
        VRChat.Groups.Instances.Add(FakeGroups.Listed(location, 2));
        await PollAsync(TimeSpan.FromMinutes(1));
        VRChat.Groups.Instances.Clear();
        await PollAsync(TimeSpan.Zero);

        await using (var db = Database.NewContext())
        {
            var first = await db.VRChatInstances.AsNoTracking().SingleAsync(Ct);
            Assert.NotNull(first.ClosedAt);

            // The audit log has been read past the end and the close-entry leeway, but the instance
            // could still come back for a few more minutes.
            await AuditLogReadToAsync(first.ClosedAt!.Value + InstanceCloseEntries.Leeway + TimeSpan.FromMinutes(1));
        }

        Clock.Advance(TimeSpan.FromMinutes(7));
        await PollAsync(TimeSpan.Zero);
        Assert.Empty(await EntriesAsync());

        // It comes back: one continuous instance, not two.
        VRChat.Groups.Instances.Add(FakeGroups.Listed(location, 3));
        await PollAsync(TimeSpan.FromMinutes(5));

        await using (var db = Database.NewContext())
        {
            var back = await db.VRChatInstances.AsNoTracking().SingleAsync(Ct);
            Assert.Null(back.ClosedAt);
            Assert.Null(back.EndRecordedAt);
        }

        // Its real end, later.
        VRChat.Groups.Instances.Clear();
        await PollAsync(TimeSpan.Zero);

        DateTimeOffset realEnd;
        await using (var db = Database.NewContext())
            realEnd = (await db.VRChatInstances.AsNoTracking().SingleAsync(Ct)).ClosedAt!.Value;

        await AuditLogReadToAsync(realEnd + Wait);
        await PollAsync(TimeSpan.Zero);
        await PollAsync(TimeSpan.Zero);

        var entry = Assert.Single(await EntriesAsync());
        Assert.Equal(realEnd, entry.OccurredAt);
    }

    [Fact]
    public async Task AnInstanceThatCameBackAndWasThenClosedByHand_GetsNoEntryAtAll()
    {
        await EntriesBeganAsync(Now.AddDays(-1));
        var location = Listed("1");

        VRChat.Groups.Instances.Add(FakeGroups.Listed(location, 2));
        await PollAsync(TimeSpan.FromMinutes(1));
        VRChat.Groups.Instances.Clear();
        await PollAsync(TimeSpan.FromMinutes(3));

        // Back within ten minutes, so still the same instance.
        VRChat.Groups.Instances.Add(FakeGroups.Listed(location, 2));
        await PollAsync(TimeSpan.FromMinutes(5));

        // A moderator closes it, and it leaves the list.
        VRChat.Groups.Instances.Clear();
        await PollAsync(TimeSpan.Zero);

        DateTimeOffset realEnd;
        await using (var db = Database.NewContext())
            realEnd = (await db.VRChatInstances.AsNoTracking().SingleAsync(Ct)).ClosedAt!.Value;

        await WriteCloseEntryAsync("1", realEnd.AddSeconds(-15));
        await AuditLogReadToAsync(realEnd + Wait);
        await PollAsync(TimeSpan.Zero);

        Assert.Empty(await EntriesAsync());
    }

    /// <summary>The poll undoing an end also undoes what was decided about it.</summary>
    [Fact]
    public async Task WhenAnEndIsUndone_TheMarkIsClearedToo()
    {
        var closedAt = Now.AddMinutes(-3);
        var instance = await EndedInstanceAsync("1", closedAt.AddMinutes(-40), closedAt);

        await using (var db = Database.NewContext())
        {
            var row = await db.VRChatInstances.SingleAsync(i => i.Id == instance.Id, Ct);
            row.EndRecordedAt = Now.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
        }

        // The place store only changes what the context tracks; the caller saves.
        await using (var db = Database.NewContext())
        {
            await new PlaceStore(db, Clock).RecordSightingAsync(instance.Location, Now, fromGroupList: true, ct: Ct);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = Database.NewContext())
        {
            var back = await db.VRChatInstances.AsNoTracking().SingleAsync(Ct);
            Assert.Null(back.ClosedAt);
            Assert.Null(back.EndRecordedAt);
        }
    }

    [Fact]
    public async Task ThePollWritesThem_WhenItIsGivenTheWriter()
    {
        var closedAt = Now.AddMinutes(-30);
        await EndedInstanceAsync("1", closedAt.AddMinutes(-40), closedAt);
        await AuditLogReadToAsync(closedAt + Wait);

        await using (var db = Database.NewContext())
        {
            var ends = new InstanceEndFacts(db, new FactWriter(db, Clock), new EventPartitionMaintainer(db, Clock));
            await new GroupInstanceSync(Gate, new PlaceStore(db, Clock), db, Clock, ends: ends).RunOnceAsync(Ct);
        }

        Assert.Single(await EntriesAsync());
    }
}
