using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

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

    private static readonly TimeSpan Wait = InstanceCloseEntries.Leeway + TimeSpan.FromMinutes(1);

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
