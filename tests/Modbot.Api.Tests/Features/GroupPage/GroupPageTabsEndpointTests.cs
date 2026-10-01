using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.GroupPage;
using Modbot.Api.Tests.Fakes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Sync;
using VRChat.API.Model;

namespace Modbot.Api.Tests.Features.GroupPage;

/// <summary>
/// The VRChat page's Roles, Invites and Gallery tabs: each behind its own permission, one request
/// per look or per press, a fact naming who made each change, and nothing written when VRChat says
/// no. A 403 from VRChat names the group permission Modbot's VRChat account is missing.
/// </summary>
/// <remarks>The gate is the scripted fake, so nothing here reaches VRChat.</remarks>
[Collection(nameof(PostgresCollection))]
public class GroupPageTabsEndpointTests
{
    private const string GroupId = "grp_1";

    private static readonly DateTimeOffset Day = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public GroupPageTabsEndpointTests(PostgresFixture db) => _db = db;

    // ── Setting the scene ──────────────────────────────────────────────────────────────────

    private static Group Stored() => new()
    {
        Id = GroupId,
        Name = "Test Group",
        MemberCount = 120,
        OnlineMemberCount = 7,
        Roles =
        [
            new GroupRole { Id = "grol_mod", Name = "Moderator", Description = "Keeps the peace", Order = 1, Permissions = [GroupPermissions.group_bans_manage] },
            new GroupRole { Id = "grol_member", Name = "Member", Order = 2, DefaultRole = true, Permissions = [] },
        ],
    };

    /// <summary>VRChat's JSON for the roles, with a permission the SDK has no name for.</summary>
    private const string RolesJson = """
        [
          {"id":"grol_member","name":"Member","order":2,"defaultRole":true,"permissions":[]},
          {"id":"grol_mod","name":"Moderator","description":"Keeps the peace","order":1,
           "permissions":["group-bans-manage","group-something-new"]}
        ]
        """;

    private async Task<ReadSurfaceTestHost> StartAsync(FakeVRChatGate gate, CancellationToken ct, bool galleries = true)
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = GroupId;
        settings.GroupInfoSnapshot = GroupInfoSnapshot.From(Stored()).ToJson();
        settings.VRChatAccountRoleIds = ["grol_mod"];
        settings.VRChatAccountPermissions = ["group-bans-manage"];
        settings.ManagedGroupGalleries = galleries
            ? GroupGallerySnapshot.ToJson([new GroupGallerySnapshot("ggal_1", "Photos", null, false), new GroupGallerySnapshot("ggal_2", "Art", null, true)])
            : null;

        await db.SaveChangesAsync(ct);
        return host;
    }

    private static async Task<List<ModbotEvent>> FactsAsync(ReadSurfaceTestHost host, string type, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.Events.AsNoTracking().Where(e => e.Type == type).OrderBy(e => e.Id).ToListAsync(ct);
    }

    private static async Task<GroupInfoSnapshot?> SnapshotAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return GroupInfoSnapshot.Parse((await db.GetSettingsAsync(ct)).GroupInfoSnapshot);
    }

    private static VRChatResult<T> Forbidden<T>(string operation) => VRChatResult<T>.Failure(
        403,
        "Forbidden",
        rawResponse: $"Error calling {operation}: {{\"error\":{{\"message\":\"You do not have permission to do that.\",\"status_code\":403}}}}");

    // ── Roles ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRolesAreReadFromVRChatsOwnWords_InItsOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("GetGroupRoles", VRChatResult<List<GroupRole>>.Ok([], 200, RolesJson));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var list = await host.GetJsonAsync<GroupRoleList>("/api/group/roles", cookie, ct);

        Assert.Equal(["grol_mod", "grol_member"], list.Roles.Select(r => r.Id));
        Assert.Equal(["group-bans-manage", "group-something-new"], list.Roles[0].Permissions);
        Assert.True(list.Roles[0].HeldByModbot);
        Assert.True(list.Roles[1].IsDefault);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsRoles, call.Endpoint.Class);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);
    }

    [Fact]
    public async Task RolesNeedTheirOwnPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.EditGroupProfile, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/group/roles", cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync("/api/group/roles", new { name = "Helper" }, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync("/api/group/roles", new { id = "grol_mod", name = "Mod" }, cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync("/api/group/roles/delete", new { id = "grol_mod" }, cookie, ct)).StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task CreatingARoleIsOneRequest_AFact_AndARecordedRole()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new GroupRole { Id = "grol_new", Name = "Helper", Permissions = [GroupPermissions.group_audit_view] };
        var gate = new FakeVRChatGate().Returns("CreateGroupRole", VRChatResult<GroupRole>.Ok(
            created, 200, """{"id":"grol_new","name":"Helper","permissions":["group-audit-view"]}"""));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await host.PostJsonAsync(
            "/api/group/roles", new { name = " Helper ", permissions = new[] { "group-audit-view" } }, cookie, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = JsonSerializer.Deserialize<GroupRoleSaved>(await response.Content.ReadAsStringAsync(ct), Web)!;
        Assert.Equal("grol_new", saved.Role.Id);
        Assert.Equal(["group-audit-view"], saved.Role.Permissions);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsRolesWrite, call.Endpoint.Class);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupRoleMade, ct));
        Assert.Equal(GroupId, fact.SubjectId);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal("Helper", JsonDocument.Parse(fact.Data).RootElement.GetProperty("name").GetString());

        // Stored as the poll will read it, so the next poll records no change of its own.
        Assert.Contains((await SnapshotAsync(host, ct))!.Roles, r => r.Id == "grol_new");
    }

    [Fact]
    public async Task ChangingARoleSendsOnlyWhatDiffers_AndRecordsOldAndNew()
    {
        var ct = TestContext.Current.CancellationToken;
        var after = Stored().Roles;
        after[0].Name = "Mod";
        var gate = new FakeVRChatGate().Returns("UpdateGroupRole", VRChatResult<List<GroupRole>>.Ok(
            after, 200, RolesJson.Replace("\"Moderator\"", "\"Mod\"", StringComparison.Ordinal)));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await host.PutJsonAsync(
            "/api/group/roles",
            new { id = "grol_mod", name = "Mod", description = "Keeps the peace", permissions = new[] { "group-bans-manage" } },
            cookie,
            ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(gate.Calls);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupRoleEdited, ct));
        var changed = JsonDocument.Parse(fact.Data).RootElement.GetProperty("changed");
        Assert.Equal("Moderator", changed.GetProperty("name").GetProperty("old").GetString());
        Assert.Equal("Mod", changed.GetProperty("name").GetProperty("new").GetString());
        Assert.False(changed.TryGetProperty("description", out _));
        Assert.False(changed.TryGetProperty("permissions", out _));
    }

    [Fact]
    public async Task AChangeThatChangesNothingSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await host.PutJsonAsync("/api/group/roles", new { id = "grol_mod", name = "Moderator" }, cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    /// <summary>
    /// The account holds "Manage Group Bans" but not "Manage Group Roles", so the refusal names the
    /// second, with the roles it has.
    /// </summary>
    [Fact]
    public async Task AVRChat403NamesTheMissingPermission_AndNothingIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("DeleteGroupRole", Forbidden<List<GroupRole>>("DeleteGroupRole"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await host.PostJsonAsync("/api/group/roles/delete", new { id = "grol_mod", name = "Moderator" }, cookie, ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var missing = body.RootElement.GetProperty("missingGroupPermission");
        Assert.Equal("group-roles-manage", missing.GetProperty("permission").GetString());
        Assert.Equal(GroupId, missing.GetProperty("groupId").GetString());
        Assert.Equal("Moderator", missing.GetProperty("roles")[0].GetString());

        Assert.Single(gate.Calls);
        Assert.Empty(await FactsAsync(host, FactType.GroupRoleRemoved, ct));
    }

    [Fact]
    public async Task DeletingARoleIsOneRequest_AndAFactWithItsName()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("DeleteGroupRole", VRChatResult<List<GroupRole>>.Ok(
            [Stored().Roles[1]], 200, """[{"id":"grol_member","name":"Member","defaultRole":true,"permissions":[]}]"""));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await host.PostJsonAsync("/api/group/roles/delete", new { id = "grol_mod" }, cookie, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupRoleRemoved, ct));
        Assert.Equal("Moderator", JsonDocument.Parse(fact.Data).RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain((await SnapshotAsync(host, ct))!.Roles, r => r.Id == "grol_mod");
    }

    /// <summary>DELETE /api/group/roles/{id}: the same deletion, with the role in the address (API conventions design §4).</summary>
    [Fact]
    public async Task DeletingARoleAtItsAddress_IsTheSameDeletion()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("DeleteGroupRole", VRChatResult<List<GroupRole>>.Ok(
            [Stored().Roles[1]], 200, """[{"id":"grol_member","name":"Member","defaultRole":true,"permissions":[]}]"""));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/group/roles/grol_mod?name=Moderator");
        request.Headers.Add("Cookie", cookie);
        var response = await host.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupRoleRemoved, ct));
        Assert.Equal("grol_mod", JsonDocument.Parse(fact.Data).RootElement.GetProperty("roleId").GetString());
        Assert.Equal("Moderator", JsonDocument.Parse(fact.Data).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChangingARoleAtItsAddress_RefusesABodyNamingAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await host.PutJsonAsync("/api/group/roles/grol_mod", new { id = "grol_member", name = "Mods" }, cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    /// <summary>A VRChat refusal says so in its code, beside the missing permission the page reads.</summary>
    [Fact]
    public async Task AVRChatRefusal_HasItsOwnCode()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("DeleteGroupRole", Forbidden<List<GroupRole>>("DeleteGroupRole"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/group/roles/grol_mod");
        request.Headers.Add("Cookie", cookie);
        var response = await host.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("vrchat-refused", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("group-roles-manage", body.RootElement.GetProperty("missingGroupPermission").GetProperty("permission").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("error").GetString()));
    }

    // ── Invites ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheInvitesAreOneRequest_NewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("GetGroupInvites", new List<global::VRChat.API.Model.GroupMember>
        {
            new() { UserId = "usr_old", User = new GroupMemberLimitedUser { DisplayName = "Old" }, CreatedAt = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc) },
            new() { UserId = "usr_new", User = new GroupMemberLimitedUser { DisplayName = "New" }, CreatedAt = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc) },
        });
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupInvites, ct);
        var list = await host.GetJsonAsync<GroupInviteList>("/api/group/invites", cookie, ct);

        Assert.Equal(["usr_new", "usr_old"], list.Invites.Select(i => i.UserId));
        Assert.Equal("New", list.Invites[0].DisplayName);
        Assert.False(list.HasMore);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsInvitesRead, call.Endpoint.Class);
    }

    [Fact]
    public async Task InvitesNeedTheirOwnPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewMembers, ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/group/invites", cookie, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync("/api/group/invites/cancel", new { userId = "usr_1" }, cookie, ct)).StatusCode);
        Assert.Empty(gate.Calls);
    }

    /// <summary>The fact is about the person who was invited, so it shows on their history.</summary>
    [Fact]
    public async Task CancellingAnInviteIsOneRequest_AndAFactAboutThePerson()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("DeleteGroupInvite", VRChatResult<object>.Ok(null, 200));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupInvites, ct);
        var response = await host.PostJsonAsync("/api/group/invites/cancel", new { userId = "usr_1", displayName = "Nova" }, cookie, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsInvitesCancel, call.Endpoint.Class);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupInviteCancelled, ct));
        Assert.Equal("usr_1", fact.SubjectId);
        Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);
    }

    [Fact]
    public async Task AnInviteAlreadyGoneIsA404_AndNothingIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("DeleteGroupInvite", VRChatResult<object>.Failure(404, "Not found"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupInvites, ct);
        var response = await host.PostJsonAsync("/api/group/invites/cancel", new { userId = "usr_1" }, cookie, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await FactsAsync(host, FactType.GroupInviteCancelled, ct));
    }

    // ── The gallery ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheGalleriesCostNothing_AndTheFirstOnesImagesAreOneRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("GetGroupGalleryImages", VRChatResult<object>.Ok(
            null, 200, """[{"id":"ggim_1","galleryId":"ggal_1","imageUrl":"https://api.vrchat.cloud/x","approved":false}]"""));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupGalleryPage>("/api/group/gallery", cookie, ct);

        Assert.Equal(["ggal_1", "ggal_2"], page.Galleries.Select(g => g.Id));
        Assert.Equal("ggal_1", page.GalleryId);
        var image = Assert.Single(page.Images);
        Assert.False(image.Approved);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsGallery, call.Endpoint.Class);
    }

    [Fact]
    public async Task AGroupWithNoGalleriesSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate, ct, galleries: false);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var page = await host.GetJsonAsync<GroupGalleryPage>("/api/group/gallery", cookie, ct);

        Assert.Empty(page.Galleries);
        Assert.Null(page.GalleryId);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task RemovingAnImageNeedsItsOwnPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.PostJsonAsync("/api/group/gallery/remove", new { galleryId = "ggal_1", imageId = "ggim_1" }, cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task RemovingAnImageIsOneRequest_AndAFactNamingTheGallery()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("DeleteGroupGalleryImage", new Success());
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupGallery, ct);
        var response = await host.PostJsonAsync(
            "/api/group/gallery/remove", new { galleryId = "ggal_1", imageId = "ggim_1", submittedById = "usr_9" }, cookie, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsGalleryWrite, call.Endpoint.Class);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupGalleryImageRemoved, ct));
        Assert.Equal(GroupId, fact.SubjectId);
        Assert.Equal("Photos", JsonDocument.Parse(fact.Data).RootElement.GetProperty("galleryName").GetString());
    }

    // ── Pass 1's writes name their permission too ──────────────────────────────────────────

    [Fact]
    public async Task AProfileSaveRefusedByVRChatNamesManageGroupData()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("UpdateGroup", Forbidden<Group>("UpdateGroup"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.EditGroupProfile, ct);
        var response = await host.PutJsonAsync("/api/group/profile", new { name = "Renamed" }, cookie, ct);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("group-data-manage", body.RootElement.GetProperty("missingGroupPermission").GetProperty("permission").GetString());
    }

    [Fact]
    public async Task APostRefusedByVRChatNamesManageGroupAnnouncement()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("AddGroupPost", Forbidden<GroupPost>("AddGroupPost"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupPosts, ct);
        var response = await host.PostJsonAsync("/api/group/posts", new { title = "Hello", text = "Words" }, cookie, ct);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("group-announcement-manage", body.RootElement.GetProperty("missingGroupPermission").GetProperty("permission").GetString());
    }
}
