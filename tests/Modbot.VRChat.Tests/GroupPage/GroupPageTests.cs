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

    // ── Roles, sent invites and the gallery ────────────────────────────────────────────────

    /// <summary>
    /// The reason <see cref="GroupRoleChange"/> exists: the SDK's request always writes
    /// <c>isSelfAssignable</c> and <c>order</c>, and its permissions are an enum that cannot carry
    /// one VRChat added after it. Serialized with the SDK's own settings.
    /// </summary>
    [Fact]
    public void ARoleEditSendsOnlyTheFieldsBeingSet_WithPermissionsAsVRChatsWords()
    {
        var settings = new ApiClient().SerializerSettings;

        Assert.Equal("""{"name":"Helper"}""", JsonConvert.SerializeObject(new GroupRoleChange(name: "Helper"), settings));
        Assert.Equal(
            """{"permissions":["group-bans-manage","group-something-new"]}""",
            JsonConvert.SerializeObject(new GroupRoleChange(permissions: ["group-bans-manage", "group-something-new"]), settings));
        Assert.Equal("""{"description":""}""", JsonConvert.SerializeObject(new GroupRoleChange(description: string.Empty), settings));
    }

    [Fact]
    public void ANewRoleIsWrittenTheSameWay()
    {
        var settings = new ApiClient().SerializerSettings;

        var json = JsonConvert.SerializeObject(
            new GroupRoleChange("Helper", "Helps", ["group-audit-view"]).ForCreate(), settings);

        Assert.Equal("""{"name":"Helper","description":"Helps","permissions":["group-audit-view"]}""", json);
        Assert.DoesNotContain("isSelfAssignable", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoleChangeWithNothingSetIsEmpty()
    {
        Assert.True(new GroupRoleChange().IsEmpty);
        Assert.False(new GroupRoleChange(permissions: []).IsEmpty);
    }

    [Fact]
    public void TheNewWritesAreOnePerTenSeconds_OnTheirOwnLanes_BelowModeration()
    {
        foreach (var name in new[]
                 {
                     VRChatEndpointClass.GroupsRolesWrite,
                     VRChatEndpointClass.GroupsInvitesCancel,
                     VRChatEndpointClass.GroupsGalleryWrite,
                 })
        {
            var budget = VRChatRateLimits.Defaults[name];

            Assert.Equal(0.1, budget.HardMaxPerSecond, 9);
            Assert.Equal(VRChatEndpointClass.Global, budget.Backstop);
            Assert.True(budget.ResourceScoped);
            Assert.Equal(1, budget.BurstTokens);
            Assert.DoesNotContain(name, VRChatRateLimits.Scheduled);
        }

        // Cancelling never uses up the thirty seconds auto-invites wait between sends.
        Assert.NotEqual(
            VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsInvites].Lane,
            VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsInvitesCancel].Lane);
    }

    [Fact]
    public void TheNewReadsAreNoFasterThanGroupReads_OnTheirOwnLanes()
    {
        var groupRead = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsRead];

        foreach (var name in new[]
                 {
                     VRChatEndpointClass.GroupsRoles,
                     VRChatEndpointClass.GroupsInvitesRead,
                     VRChatEndpointClass.GroupsGallery,
                 })
        {
            var read = VRChatRateLimits.Defaults[name];

            Assert.Equal(0.2, read.HardMaxPerSecond, 9);
            Assert.True(read.HardMaxPerSecond <= groupRead.HardMaxPerSecond);
            Assert.NotEqual(groupRead.Lane, read.Lane);
            Assert.Equal(VRChatEndpointClass.Interactive, read.Backstop);
            Assert.True(read.ResourceScoped);
            Assert.DoesNotContain(name, VRChatRateLimits.Scheduled);
        }
    }

    /// <summary>A 429 on opening the Roles tab never stops the group poll.</summary>
    [Fact]
    public async Task A429OnReadingRolesDoesNotStopTheGroupPoll()
    {
        var harness = new LimiterHarness();
        var roles = new VRChatEndpoint(VRChatEndpointClass.GroupsRoles, "grp_test");

        await harness.CallAsync(roles, status: 429, ct: Ct);

        Assert.False((await harness.CallAsync(roles, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(new VRChatEndpoint(VRChatEndpointClass.GroupsRead, "grp_test"), ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(new VRChatEndpoint(VRChatEndpointClass.GroupsRolesWrite, "grp_test"), ct: Ct)).IsAcquired);
    }

    [Fact]
    public async Task A429OnCancellingAnInviteDoesNotStopSendingOne()
    {
        var harness = new LimiterHarness();
        var cancel = new VRChatEndpoint(VRChatEndpointClass.GroupsInvitesCancel, "grp_test");

        await harness.CallAsync(cancel, status: 429, ct: Ct);

        Assert.False((await harness.CallAsync(cancel, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(new VRChatEndpoint(VRChatEndpointClass.GroupsInvites, "grp_test"), ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(new VRChatEndpoint(VRChatEndpointClass.GroupsInvitesRead, "grp_test"), ct: Ct)).IsAcquired);
    }

    [Fact]
    public async Task EveryNewCallNamesItsClassAndGoesOutAsInteractive()
    {
        var gate = new RecordingGate();
        var roles = new GroupRoleManager(gate);
        var invites = new GroupSentInvites(gate);
        var gallery = new GroupGalleries(gate);

        await roles.ListAsync("grp_test", Ct);
        await roles.CreateAsync("grp_test", new GroupRoleChange(name: "Helper"), Ct);
        await roles.UpdateAsync("grp_test", "grol_1", new GroupRoleChange(name: "Helper"), Ct);
        await roles.DeleteAsync("grp_test", "grol_1", Ct);
        await invites.ListAsync("grp_test", 500, -1, Ct);
        await invites.CancelAsync("grp_test", "usr_1", Ct);
        await gallery.ListAsync("grp_test", "ggal_1", 500, -1, Ct);
        await gallery.RemoveAsync("grp_test", "ggal_1", "ggim_1", Ct);

        Assert.Equal(
            [
                (VRChatEndpointClass.GroupsRoles, "GetGroupRoles"),
                (VRChatEndpointClass.GroupsRolesWrite, "CreateGroupRole"),
                (VRChatEndpointClass.GroupsRolesWrite, "UpdateGroupRole"),
                (VRChatEndpointClass.GroupsRolesWrite, "DeleteGroupRole"),
                (VRChatEndpointClass.GroupsInvitesRead, "GetGroupInvites"),
                (VRChatEndpointClass.GroupsInvitesCancel, "DeleteGroupInvite"),
                (VRChatEndpointClass.GroupsGallery, "GetGroupGalleryImages"),
                (VRChatEndpointClass.GroupsGalleryWrite, "DeleteGroupGalleryImage"),
            ],
            gate.Calls.Select(c => (c.Endpoint.Class, c.Endpoint.Operation!)).ToList());

        Assert.All(gate.Calls, c => Assert.Equal("grp_test", c.Endpoint.ResourceId));
        Assert.All(gate.Calls, c => Assert.Equal(VRChatCallPriority.Interactive, c.Priority));
    }

    [Fact]
    public void GalleryImagesAreReadFromThePlainList_OrFromAPage()
    {
        const string one = """{"id":"ggim_1","galleryId":"ggal_1","imageUrl":"https://api.vrchat.cloud/x","approved":true}""";

        Assert.Equal(["ggim_1"], GroupGalleries.ImagesOf(null, $"[{one}]").Select(i => i.Id));
        Assert.Equal(["ggim_1"], GroupGalleries.ImagesOf(null, "{\"images\":[" + one + "]}").Select(i => i.Id));
        Assert.Equal(["ggim_1"], GroupGalleries.ImagesOf(Newtonsoft.Json.Linq.JArray.Parse($"[{one}]"), null).Select(i => i.Id));
        Assert.Empty(GroupGalleries.ImagesOf(null, "not json"));
        Assert.Empty(GroupGalleries.ImagesOf(null, """[{"galleryId":"ggal_1"}]"""));
    }

    /// <summary>
    /// A role saved from Modbot is stored the way the poll will read it, so the next poll writes
    /// no "roles changed" fact for a change the edit already recorded with who made it.
    /// </summary>
    [Fact]
    public void ARoleSavedFromModbotIsStoredTheWayThePollReadsIt()
    {
        var group = GroupInfoSnapshotTests.Group();
        var settings = new Settings { GroupInfoSnapshot = GroupInfoSnapshot.From(group).ToJson() };

        var added = new GroupRole
        {
            Id = "grol_new",
            Name = "Helper",
            Permissions = [GroupPermissions.group_audit_view, GroupPermissions.group_bans_manage],
        };

        GroupInfoSync.RecordRoles(settings, added: added);

        group.Roles = [.. group.Roles ?? [], added];
        var polled = GroupInfoSnapshot.From(group);

        Assert.Empty(polled.DifferencesFrom(GroupInfoSnapshot.Parse(settings.GroupInfoSnapshot)));
    }

    [Fact]
    public void RecordRolesMakesNoSnapshotWhereThereWasNone()
    {
        var settings = new Settings();

        GroupInfoSync.RecordRoles(settings, all: []);

        Assert.Null(settings.GroupInfoSnapshot);
    }

    [Fact]
    public void TheGalleriesAreStoredFromThePoll_AndAMissingListKeepsThem()
    {
        var settings = new Settings();

        GroupInfoSync.RecordGalleries(settings, [new GroupGallery { Id = "ggal_1", Name = "Photos", MembersOnly = true }]);
        var stored = settings.ManagedGroupGalleries;

        Assert.Equal([new GroupGallerySnapshot("ggal_1", "Photos", null, true)], GroupGallerySnapshot.Parse(stored));

        GroupInfoSync.RecordGalleries(settings, null);
        Assert.Equal(stored, settings.ManagedGroupGalleries);

        GroupInfoSync.RecordGalleries(settings, []);
        Assert.Empty(GroupGallerySnapshot.Parse(settings.ManagedGroupGalleries));
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
