using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// The group-metadata producer: one request, and a fact only when something moved.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GroupInfoSyncTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    [Fact]
    public async Task TheFirstPollRecordsABaselineWithTheHeadcountAndTheRoles()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();

        var run = await RunGroupInfoAsync();

        Assert.Equal(SyncOutcome.Produced, run.Outcome);
        Assert.True(run.Baseline);

        var fact = Assert.Single(await FactsAsync());

        Assert.Equal(FactType.GroupInfoChanged, fact.Type);
        Assert.Equal(GroupId, fact.SubjectId);

        // No actor. VRChat's group object says what the group looks like, never who changed it --
        // inventing one here would be exactly the false attribution spec 5.8 depends on avoiding.
        Assert.Null(fact.ActorId);

        // Parsed rather than string-matched: the column is jsonb, so PostgreSQL rewrites the
        // text -- respacing it and reordering the keys -- and a substring assertion would be
        // testing Postgres's formatter rather than the payload.
        var baseline = Payload(fact)["baseline"]!;

        Assert.Equal(8123, baseline["MemberCount"]!.GetValue<int>());
        Assert.Equal("grol_1", baseline["Roles"]![0]!["Id"]!.GetValue<string>());
    }

    /// <summary>
    /// The point of the whole producer. A fact per poll would be 288 identical rows a day, and
    /// every "how often does this change" answer would be wrong by two orders of magnitude -- the
    /// same failure the log research (§4.0) found in the avatar lines.
    /// </summary>
    [Fact]
    public async Task APollThatFindsNothingChangedWritesNothing()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();

        await RunGroupInfoAsync();
        var second = await RunGroupInfoAsync();
        var third = await RunGroupInfoAsync();

        Assert.Equal(SyncOutcome.Quiet, second.Outcome);
        Assert.Equal(SyncOutcome.Quiet, third.Outcome);
        Assert.Single(await FactsAsync());
    }

    [Fact]
    public async Task AChangedMemberCountWritesOneFactNamingOnlyWhatChanged()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        await RunGroupInfoAsync();

        Clock.Advance(TimeSpan.FromMinutes(5));
        VRChat.Groups.Group!.MemberCount = 8124;

        var run = await RunGroupInfoAsync();

        Assert.Equal(["MemberCount"], run.Changed);

        var facts = await FactsAsync();
        Assert.Equal(2, facts.Count);

        var changedFields = Payload(facts[^1])["changed"]!.AsObject();

        Assert.Equal(["MemberCount"], changedFields.Select(f => f.Key));
        Assert.Equal(8123, changedFields["MemberCount"]!["old"]!.GetValue<int>());
        Assert.Equal(8124, changedFields["MemberCount"]!["new"]!.GetValue<int>());
    }

    /// <summary>
    /// A poll knows only that the change happened between the previous poll and this one. Spec 5.3
    /// is explicit that collapsing that window into an instant invents precision and produces fake
    /// spikes, so the window is what gets recorded.
    /// </summary>
    [Fact]
    public async Task AChangeCarriesTheWindowItCouldHaveHappenedIn()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        await RunGroupInfoAsync();

        var seenAt = Clock.UtcNow;
        Clock.Advance(TimeSpan.FromMinutes(5));
        VRChat.Groups.Group!.Name = "Renamed Group";

        await RunGroupInfoAsync();

        var change = (await FactsAsync())[^1];

        Assert.Equal(seenAt, change.OccurredAt);
        Assert.Equal(Clock.UtcNow, change.OccurredBefore);
        Assert.Equal(FactSource.SyncDiff, change.Source);
    }

    /// <summary>
    /// Role definitions are what make a <c>RoleGranted</c> fact readable: VRChat's audit entry
    /// names the role by id, and roles get renamed. Without a dated record of what the id meant,
    /// a moderation timeline reads "granted grol_9f3c..." forever.
    /// </summary>
    [Fact]
    public async Task ARenamedRoleIsRecordedWithBothNames()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        await RunGroupInfoAsync();

        Clock.Advance(TimeSpan.FromMinutes(5));
        VRChat.Groups.Group!.Roles![0].Name = "Senior Moderator";

        var run = await RunGroupInfoAsync();

        Assert.Equal(["Roles"], run.Changed);

        var roles = Payload((await FactsAsync())[^1])["changed"]!["Roles"]!;

        Assert.Equal("Moderator", roles["old"]![0]!["Name"]!.GetValue<string>());
        Assert.Equal("Senior Moderator", roles["new"]![0]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnconfiguredDeploymentIssuesNothingAtAll()
    {
        await using (var context = Database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.ManagedGroupId = null;
            await context.SaveChangesAsync(Ct);
        }

        var run = await RunGroupInfoAsync();

        Assert.Equal(SyncOutcome.NotConfigured, run.Outcome);
        Assert.Equal(0, VRChat.Groups.GroupRequests);
    }

    /// <summary>
    /// A failed poll leaves the recorded snapshot alone. Clearing it would make the next
    /// successful poll write a second baseline and look like the group changed everything at once.
    /// </summary>
    [Fact]
    public async Task AFailedPollLeavesTheRecordedSnapshotUntouched()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        await RunGroupInfoAsync();

        VRChat.Groups.GroupStatus = System.Net.HttpStatusCode.InternalServerError;
        var failed = await RunGroupInfoAsync();

        Assert.Equal(SyncOutcome.Failed, failed.Outcome);

        VRChat.Groups.GroupStatus = System.Net.HttpStatusCode.OK;
        var recovered = await RunGroupInfoAsync();

        Assert.Equal(SyncOutcome.Quiet, recovered.Outcome);
        Assert.Single(await FactsAsync());
    }

    /// <summary>
    /// Spec 4.2.3: the UI shows real last-sync times per data type. A quiet group and a broken
    /// producer both write no facts, so the newest fact cannot answer "is this fresh".
    /// </summary>
    [Fact]
    public async Task EveryPollRecordsWhenItRanEvenWhenItWroteNothing()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        await RunGroupInfoAsync();

        Clock.Advance(TimeSpan.FromMinutes(5));
        await RunGroupInfoAsync();

        Assert.Equal(Clock.UtcNow, (await SettingsAsync()).GroupInfoPolledAt);
    }

    /// <summary>
    /// The member count chart is drawn from readings, not from facts: a poll that changed nothing
    /// still writes its two counts with the time it read them, because the chart shows every
    /// reading and the time of each is part of what it shows.
    /// </summary>
    [Fact]
    public async Task EveryPollRecordsAReadingOfBothCounts_WhetherOrNotAnythingChanged()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();

        await RunGroupInfoAsync();

        Clock.Advance(TimeSpan.FromMinutes(5));
        var quiet = await RunGroupInfoAsync();

        Clock.Advance(TimeSpan.FromMinutes(5));
        VRChat.Groups.Group!.OnlineMemberCount = 41;
        await RunGroupInfoAsync();

        Assert.Equal(SyncOutcome.Quiet, quiet.Outcome);

        var readings = await ReadingsAsync();

        Assert.Equal([Now, Now.AddMinutes(5), Now.AddMinutes(10)], readings.Select(r => r.CountedAt));
        Assert.Equal([8123, 8123, 8123], readings.Select(r => r.MemberCount));
        Assert.Equal([12, 12, 41], readings.Select(r => r.OnlineMemberCount));
        Assert.All(readings, r => Assert.Equal(GroupId, r.GroupId));
    }

    [Fact]
    public async Task AFailedPollRecordsNoReading()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        await RunGroupInfoAsync();

        Clock.Advance(TimeSpan.FromMinutes(5));
        VRChat.Groups.GroupStatus = System.Net.HttpStatusCode.InternalServerError;
        await RunGroupInfoAsync();

        Assert.Single(await ReadingsAsync());
    }

    /// <summary>
    /// The VRChat analytics page shows the group's languages and links as its VRChat page does, and
    /// they come from the same read the sync already makes.
    /// </summary>
    [Fact]
    public async Task ThePollRecordsTheGroupsLanguagesAndLinks()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        VRChat.Groups.Group!.Languages = ["eng", "jpn"];
        VRChat.Groups.Group!.Links = ["https://discord.gg/example", "https://example.org/rules"];

        await RunGroupInfoAsync();

        var settings = await SettingsAsync();

        Assert.Equal(["eng", "jpn"], settings.ManagedGroupLanguages);
        Assert.Equal(["https://discord.gg/example", "https://example.org/rules"], settings.ManagedGroupLinks);
    }

    /// <summary>A link goes straight into an <c>href</c>, so anything but a web address is dropped.</summary>
    [Fact]
    public async Task ALinkThatIsNotAWebAddressIsNotKept()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        VRChat.Groups.Group!.Links =
        [
            "javascript:alert(1)",
            "not a link",
            "",
            "http://example.org/",
            "https://example.org/a",
            "https://example.org/a",
        ];

        await RunGroupInfoAsync();

        Assert.Equal(["http://example.org/", "https://example.org/a"], (await SettingsAsync()).ManagedGroupLinks);
    }

    /// <summary>
    /// A list VRChat left out says nothing about the group, and keeps what was recorded. An empty
    /// list says the group has none, and replaces it.
    /// </summary>
    [Fact]
    public async Task AMissingListKeepsWhatWasRecorded_AndAnEmptyOneClearsIt()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        VRChat.Groups.Group!.Languages = ["eng"];
        VRChat.Groups.Group!.Links = ["https://example.org/"];
        await RunGroupInfoAsync();

        Clock.Advance(TimeSpan.FromMinutes(5));
        VRChat.Groups.Group!.Languages = null!;
        VRChat.Groups.Group!.Links = null!;
        await RunGroupInfoAsync();

        var kept = await SettingsAsync();
        Assert.Equal(["eng"], kept.ManagedGroupLanguages);
        Assert.Equal(["https://example.org/"], kept.ManagedGroupLinks);

        Clock.Advance(TimeSpan.FromMinutes(5));
        VRChat.Groups.Group!.Languages = [];
        VRChat.Groups.Group!.Links = [];
        await RunGroupInfoAsync();

        var cleared = await SettingsAsync();
        Assert.Empty(cleared.ManagedGroupLanguages!);
        Assert.Empty(cleared.ManagedGroupLinks!);
    }

    /// <summary>
    /// Languages and links are not watched for change: a poll that saw a new one writes no fact,
    /// the same as a new picture.
    /// </summary>
    [Fact]
    public async Task NewLanguagesOrLinksWriteNoFact()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        await RunGroupInfoAsync();

        Clock.Advance(TimeSpan.FromMinutes(5));
        VRChat.Groups.Group!.Languages = ["spa"];
        VRChat.Groups.Group!.Links = ["https://example.org/"];

        var run = await RunGroupInfoAsync();

        Assert.Equal(SyncOutcome.Quiet, run.Outcome);
        Assert.Single(await FactsAsync());
        Assert.Equal(["spa"], (await SettingsAsync()).ManagedGroupLanguages);
    }

    private async Task<IReadOnlyList<GroupMemberCount>> ReadingsAsync()
    {
        await using var context = Database.NewContext();

        return await context.GroupMemberCounts
            .AsNoTracking()
            .OrderBy(r => r.CountedAt)
            .ToListAsync(Ct);
    }

    /// <summary>
    /// The member list never includes the account asking for it, so the Members list was one short
    /// of the count VRChat shows. The group answer's <c>myMember</c> is the only read that has it.
    /// </summary>
    [Fact]
    public async Task ThePollWritesModbotsOwnMemberRowFromMyMember()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        VRChat.Groups.GroupJson = """
            {
              "id": "grp_1",
              "myMember": {
                "id": "gmem_bot",
                "userId": "usr_bot",
                "roleIds": ["grol_b", "grol_a"],
                "joinedAt": "2026-01-02T03:04:05.000Z",
                "membershipStatus": "member",
                "visibility": "visible",
                "isRepresenting": true,
                "permissions": ["*"]
              }
            }
            """;

        await RunGroupInfoAsync();

        var row = await MemberRowAsync("usr_bot");

        Assert.NotNull(row);
        Assert.Null(row.LeftAt);
        Assert.Equal("gmem_bot", row.MembershipId);
        Assert.Equal(["grol_a", "grol_b"], GroupMemberSync.RoleIds(row.Roles));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), row.JoinedAt);
        Assert.Equal("member", row.MembershipStatus);
        Assert.Equal("visible", row.Visibility);
        Assert.True(row.IsRepresenting);
        Assert.Equal(Clock.UtcNow, row.LastSeenAt);
        Assert.Equal("usr_bot", (await SettingsAsync()).VRChatAccountUserId);

        // A row, not a join: the audit log records the account's own joins like anyone else's.
        Assert.Empty(await FactsOfTypeAsync(FactType.MemberJoined));
    }

    [Fact]
    public async Task AnAnswerWithNoMyMemberWritesNoMemberRow()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();

        await RunGroupInfoAsync();

        await using var context = Database.NewContext();
        Assert.Empty(await context.GroupMembers.AsNoTracking().ToListAsync(Ct));
        Assert.Null((await SettingsAsync()).VRChatAccountUserId);
    }

    private static JsonObject Payload(ModbotEvent fact) => JsonNode.Parse(fact.Data)!.AsObject();
}
