using Modbot.Core.Data.Entities;
using Modbot.VRChat.GroupPage;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Tests.Fakes;
using Modbot.VRChat.Tests.Sync;
using Newtonsoft.Json;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.GroupPage;

/// <summary>
/// The group's own page, changed from Modbot: the body a profile edit sends, what is stored from
/// VRChat's answer, and the budgets the new requests run on.
/// </summary>
public class GroupPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── The body of a profile edit ─────────────────────────────────────────────────────────

    /// <summary>
    /// The reason <see cref="GroupProfileChange"/> exists. The SDK's own request writes
    /// <c>bannerId</c>, <c>iconId</c> and <c>nameplateId</c> as nulls, which asks VRChat to take the
    /// group's pictures away on every Save. Serialized with the SDK's own settings, so this is the
    /// body VRChat would receive.
    /// </summary>
    [Fact]
    public void AProfileEditSendsOnlyTheFieldsBeingChanged()
    {
        var settings = new ApiClient().SerializerSettings;

        var json = JsonConvert.SerializeObject(new GroupProfileChange(description: "New words"), settings);

        Assert.Equal("{\"description\":\"New words\"}", json);
        Assert.DoesNotContain("bannerId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("iconId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("nameplateId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearingAListSendsItEmpty_AndJoinStateIsVRChatsWord()
    {
        var settings = new ApiClient().SerializerSettings;

        var json = JsonConvert.SerializeObject(
            new GroupProfileChange(links: [], joinState: GroupJoinState.Request),
            settings);

        Assert.Equal("{\"links\":[],\"joinState\":\"request\"}", json);
    }

    [Fact]
    public void AnEditWithNothingSetIsEmpty()
    {
        Assert.True(new GroupProfileChange().IsEmpty);
        Assert.False(new GroupProfileChange(rules: string.Empty).IsEmpty);
    }

    // ── What is stored from VRChat's answer ────────────────────────────────────────────────

    /// <summary>
    /// VRChat's answer to an edit carries no roles, and its counts are the poll's business. Taking
    /// either from it would have the next poll write a "change" nobody made.
    /// </summary>
    [Fact]
    public void AnEditChangesTheProfileFields_AndLeavesRolesAndCountsAlone()
    {
        var before = GroupInfoSnapshot.From(GroupInfoSnapshotTests.Group());

        var answer = new Group
        {
            Name = "Renamed",
            Description = "New words",
            Rules = string.Empty,
            JoinState = GroupJoinState.Invite,
            MemberCount = 0,
            OnlineMemberCount = 0,
            Roles = null!,
        };

        var after = before.WithEdit(answer);

        Assert.Equal("Renamed", after.Name);
        Assert.Equal("New words", after.Description);
        Assert.Equal(string.Empty, after.Rules);
        Assert.Equal("Invite", after.JoinState);

        Assert.Equal(before.MemberCount, after.MemberCount);
        Assert.Equal(before.OnlineMemberCount, after.OnlineMemberCount);
        Assert.Equal(before.Roles, after.Roles);
        Assert.Equal(before.ShortCode, after.ShortCode);

        // And the next poll, seeing the group as edited, finds nothing to record.
        var polled = GroupInfoSnapshotTests.Group();
        polled.Name = "Renamed";
        polled.Description = "New words";
        polled.Rules = string.Empty;
        polled.JoinState = GroupJoinState.Invite;
        Assert.Empty(GroupInfoSnapshot.From(polled).DifferencesFrom(after));
    }

    [Fact]
    public void RecordEditStoresTheLanguagesAndLinks_AndOnlyWebLinks()
    {
        var settings = new Settings
        {
            GroupInfoSnapshot = GroupInfoSnapshot.From(GroupInfoSnapshotTests.Group()).ToJson(),
        };

        GroupInfoSync.RecordEdit(settings, new Group
        {
            Name = "Test Group",
            Languages = ["eng", "jpn"],
            Links = ["https://discord.gg/example", "javascript:alert(1)"],
        });

        Assert.Equal(["eng", "jpn"], settings.ManagedGroupLanguages);
        Assert.Equal(["https://discord.gg/example"], settings.ManagedGroupLinks);
    }

    /// <summary>Before the first poll there is no snapshot, and the first one is the poll's baseline.</summary>
    [Fact]
    public void RecordEditMakesNoSnapshotWhereThereWasNone()
    {
        var settings = new Settings();

        GroupInfoSync.RecordEdit(settings, new Group { Name = "Test Group", Description = "x" });

        Assert.Null(settings.GroupInfoSnapshot);
    }

    // ── Budgets ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unmeasured endpoints, so the numbers are guesses meant to be too low (spec 4.3.4). Pinned so
    /// raising one is a decision somebody makes on purpose.
    /// </summary>
    [Fact]
    public void TheWritesAreOnePerTenSeconds_OnTheirOwnLanes_BelowModeration()
    {
        foreach (var name in new[] { VRChatEndpointClass.GroupsEdit, VRChatEndpointClass.GroupsPostsWrite })
        {
            var budget = VRChatRateLimits.Defaults[name];

            Assert.Equal(0.1, budget.HardMaxPerSecond, 9);
            Assert.Equal(
                0.1,
                Math.Min(budget.HardMaxPerSecond, budget.DefaultCeilingPerSecond * RateLimitOptions.DefaultFraction),
                9);

            // Below what a moderator presses: the room spec 4.3.5 keeps is kept for moderation.
            Assert.Equal(VRChatEndpointClass.Global, budget.Backstop);
            Assert.True(budget.ResourceScoped);
            Assert.Equal(1, budget.BurstTokens);
            Assert.NotEqual(VRChatRateLimits.GroupLane, budget.Lane);
            Assert.NotEqual(VRChatRateLimits.GroupsModerateLane, budget.Lane);
            Assert.DoesNotContain(name, VRChatRateLimits.Scheduled);
        }

        Assert.NotEqual(
            VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsEdit].Lane,
            VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsPostsWrite].Lane);
    }

    [Fact]
    public void ReadingPostsIsNoFasterThanGroupReads_OnItsOwnLane()
    {
        var read = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsPosts];

        Assert.Equal(0.2, read.HardMaxPerSecond, 9);
        Assert.True(read.HardMaxPerSecond <= VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsRead].HardMaxPerSecond);
        Assert.Equal(VRChatRateLimits.GroupsPostsLane, read.Lane);
        Assert.NotEqual(VRChatRateLimits.GroupsPostsWriteLane, read.Lane);
        Assert.True(read.ResourceScoped);
        Assert.DoesNotContain(VRChatEndpointClass.GroupsPosts, VRChatRateLimits.Scheduled);
    }

    /// <summary>A 429 on a profile edit stops profile edits, and never a ban or a sweep.</summary>
    [Fact]
    public async Task A429OnAnEditStopsEditsAndNothingElse()
    {
        var harness = new LimiterHarness();
        var edit = new VRChatEndpoint(VRChatEndpointClass.GroupsEdit, "grp_test");

        await harness.CallAsync(edit, status: 429, ct: Ct);

        Assert.False((await harness.CallAsync(edit, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(new VRChatEndpoint(VRChatEndpointClass.GroupsModerate, "grp_test"), ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(new VRChatEndpoint(VRChatEndpointClass.GroupsPostsWrite, "grp_test"), ct: Ct)).IsAcquired);
    }

    /// <summary>A 429 on posting leaves the list readable, and the other way round.</summary>
    [Fact]
    public async Task A429OnPostingDoesNotStopReadingPosts()
    {
        var harness = new LimiterHarness();
        var write = new VRChatEndpoint(VRChatEndpointClass.GroupsPostsWrite, "grp_test");

        await harness.CallAsync(write, status: 429, ct: Ct);

        Assert.False((await harness.CallAsync(write, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(new VRChatEndpoint(VRChatEndpointClass.GroupsPosts, "grp_test"), ct: Ct)).IsAcquired);
    }

    // ── Every call goes through the gate ───────────────────────────────────────────────────

    [Fact]
    public async Task EveryCallNamesItsClassAndGoesOutAsInteractive()
    {
        var gate = new RecordingGate();
        var profile = new GroupProfile(gate);
        var posts = new GroupPosts(gate);
        var post = new CreateGroupPostRequest(null!, null!, false, "text", "title", GroupPostVisibility.Group);

        await profile.UpdateAsync("grp_test", new GroupProfileChange(name: "New"), Ct);
        await posts.ListAsync("grp_test", 500, -3, Ct);
        await posts.CreateAsync("grp_test", post, Ct);
        await posts.UpdateAsync("grp_test", "not_1", post, Ct);
        await posts.DeleteAsync("grp_test", "not_1", Ct);

        Assert.Equal(
            [
                (VRChatEndpointClass.GroupsEdit, "UpdateGroup"),
                (VRChatEndpointClass.GroupsPosts, "GetGroupPosts"),
                (VRChatEndpointClass.GroupsPostsWrite, "AddGroupPost"),
                (VRChatEndpointClass.GroupsPostsWrite, "UpdateGroupPost"),
                (VRChatEndpointClass.GroupsPostsWrite, "DeleteGroupPost"),
            ],
            gate.Calls.Select(c => (c.Endpoint.Class, c.Endpoint.Operation!)).ToList());

        Assert.All(gate.Calls, c => Assert.Equal("grp_test", c.Endpoint.ResourceId));
        Assert.All(gate.Calls, c => Assert.Equal(VRChatCallPriority.Interactive, c.Priority));
    }

    /// <summary>Records each call and answers nothing: what is sent, not what comes back, is under test.</summary>
    private sealed class RecordingGate : IVRChatGate
    {
        public List<(VRChatEndpoint Endpoint, VRChatCallPriority Priority)> Calls { get; } = [];

        public VRChatSessionState State => VRChatSessionState.Healthy;

        public Task<VRChatResult<T>> ExecuteAsync<T>(
            VRChatEndpoint endpoint,
            Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
            VRChatCallPriority priority = VRChatCallPriority.Background,
            CancellationToken ct = default)
        {
            Calls.Add((endpoint, priority));
            return Task.FromResult(VRChatResult<T>.Failure(0, "not sent", kind: VRChatFailureKind.NotConfigured));
        }

        public Task<VRChatResult<CurrentUserLoginResponse>> SignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Modbot.VRChat.Session.SignInStatus> DescribeSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ResumeAfterWaitAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RateLimitBucketHealth>> DescribeBucketsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VRChatResult<Modbot.VRChat.Proxy.VRChatProxyResponse>> ForwardAsync(
            VRChatEndpoint endpoint,
            Modbot.VRChat.Proxy.VRChatProxyRequest request,
            Modbot.VRChat.Proxy.VRChatProxyAccount account,
            VRChatCallPriority priority = VRChatCallPriority.Interactive,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Modbot.VRChat.Files.VRChatFileResult> FetchFileAsync(Uri url, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
