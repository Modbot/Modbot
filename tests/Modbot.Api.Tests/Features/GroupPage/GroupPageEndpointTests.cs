using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Analytics.Group;
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
/// The group's own page, changed from Modbot: each write behind its own permission, sent through
/// the gate on its own class, written down as a fact naming who made it — and nothing sent or
/// written when there is nothing to change or VRChat says no.
/// </summary>
/// <remarks>
/// The gate is the scripted fake, so nothing here reaches VRChat.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class GroupPageEndpointTests
{
    private const string GroupId = "grp_1";

    private static readonly DateTimeOffset Day = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public GroupPageEndpointTests(PostgresFixture db) => _db = db;

    // ── Setting the scene ──────────────────────────────────────────────────────────────────

    private static Group Stored() => new()
    {
        Id = GroupId,
        Name = "Test Group",
        ShortCode = "TEST",
        Discriminator = "1234",
        Description = "A group",
        Rules = "Be kind",
        JoinState = GroupJoinState.Open,
        MemberCount = 120,
        OnlineMemberCount = 7,
        Roles =
        [
            new GroupRole { Id = "grol_mod", Name = "Moderator", Order = 1, Permissions = [] },
            new GroupRole { Id = "grol_member", Name = "Member", Order = 2, DefaultRole = true, Permissions = [] },
        ],
    };

    private async Task<ReadSurfaceTestHost> StartAsync(FakeVRChatGate gate, CancellationToken ct)
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = GroupId;
        settings.GroupInfoSnapshot = GroupInfoSnapshot.From(Stored()).ToJson();
        settings.ManagedGroupLanguages = ["eng"];
        settings.ManagedGroupLinks = ["https://example.com/"];

        await db.SaveChangesAsync(ct);
        return host;
    }

    private static async Task<List<ModbotEvent>> FactsAsync(ReadSurfaceTestHost host, string type, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.Events.AsNoTracking().Where(e => e.Type == type).OrderBy(e => e.Id).ToListAsync(ct);
    }

    private static async Task<bool> IsAModbotAccountAsync(ReadSurfaceTestHost host, string? id, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return Guid.TryParse(id, out var guid) && await db.Users.AnyAsync(u => u.Id == guid, ct);
    }

    private static GroupPost Post(string id, string title, string authorId = "usr_author") => new()
    {
        Id = id,
        GroupId = GroupId,
        Title = title,
        Text = "Words",
        AuthorId = authorId,
        Visibility = GroupPostVisibility.Group,
        RoleIds = [],
        CreatedAt = new DateTime(2026, 9, 26, 18, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 9, 26, 18, 0, 0, DateTimeKind.Utc),
    };

    // ── The profile ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EditingTheProfileNeedsItsOwnPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("UpdateGroup", Stored());
        await using var host = await StartAsync(gate, ct);

        // Reading the VRChat page is not enough to change the group.
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var response = await host.PutJsonAsync("/api/group/profile", new { description = "New" }, cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task ASaveSendsOneRequest_WritesWhoChangedWhat_AndThePageShowsItAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;

        var answer = Stored();
        answer.Description = "New words";
        answer.Links = ["https://discord.gg/example"];
        answer.Languages = ["eng"];

        var gate = new FakeVRChatGate().Returns("UpdateGroup", answer);
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.EditGroupProfile | ModbotPermissions.ViewAnalytics, ct);

        // The name and languages are sent unchanged, so only the description and links are a change.
        var response = await host.PutJsonAsync(
            "/api/group/profile",
            new { name = "Test Group", description = "New words", languages = new[] { "ENG" }, links = new[] { " https://discord.gg/example " } },
            cookie,
            ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsEdit, call.Endpoint.Class);
        Assert.Equal(GroupId, call.Endpoint.ResourceId);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupProfileChanged, ct));
        Assert.Equal(GroupId, fact.SubjectId);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.True(await IsAModbotAccountAsync(host, fact.ActorId, ct));

        var changed = JsonDocument.Parse(fact.Data).RootElement.GetProperty("changed");
        Assert.Equal("A group", changed.GetProperty("description").GetProperty("old").GetString());
        Assert.Equal("New words", changed.GetProperty("description").GetProperty("new").GetString());
        Assert.True(changed.TryGetProperty("links", out _));
        Assert.False(changed.TryGetProperty("name", out _));
        Assert.False(changed.TryGetProperty("languages", out _));

        // What the page reads next is VRChat's answer, with no second request to VRChat.
        var info = await host.GetJsonAsync<GroupInfo>("/api/analytics/group/info", cookie, ct);
        Assert.Equal("New words", info.Description);
        Assert.Equal(["https://discord.gg/example"], info.Links);
        Assert.Single(gate.Calls);
    }

    [Fact]
    public async Task ASaveThatChangesNothingSendsNothing_AndRecordsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("UpdateGroup", Stored());
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.EditGroupProfile, ct);
        var response = await host.PutJsonAsync(
            "/api/group/profile",
            new { description = "A group", rules = "Be kind", joinState = "open" },
            cookie,
            ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(gate.Calls);
        Assert.Empty(await FactsAsync(host, FactType.GroupProfileChanged, ct));
    }

    [Fact]
    public async Task ARefusalIsVRChatsOwnWords_AndNothingIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate().Returns("UpdateGroup", VRChatResult<Group>.Failure(
            403,
            "Forbidden",
            rawResponse: "Error calling UpdateGroup: {\"error\":{\"message\":\"You do not have permission to do that.\",\"status_code\":403}}"));

        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.EditGroupProfile, ct);
        var response = await host.PutJsonAsync("/api/group/profile", new { rules = "New rules" }, cookie, ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("You do not have permission to do that.", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Single(gate.Calls);
        Assert.Empty(await FactsAsync(host, FactType.GroupProfileChanged, ct));
    }

    /// <summary>A rate limit is answered as one and never retried (spec 4.3.1).</summary>
    [Fact]
    public async Task ARateLimitIsA429_SentOnce()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate().Returns("UpdateGroup", VRChatResult<Group>.Failure(
            429, "VRChat rate limited this.", kind: VRChatFailureKind.RateLimited));

        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.EditGroupProfile, ct);
        var response = await host.PutJsonAsync("/api/group/profile", new { rules = "New rules" }, cookie, ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(gate.Calls);
        Assert.Empty(await FactsAsync(host, FactType.GroupProfileChanged, ct));
    }

    [Fact]
    public async Task AnEditPastVRChatsLimitsIsRefusedBeforeAnythingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("UpdateGroup", Stored());
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.EditGroupProfile, ct);

        var links = await host.PutJsonAsync(
            "/api/group/profile",
            new { links = new[] { "https://a.example/", "https://b.example/", "https://c.example/", "https://d.example/" } },
            cookie,
            ct);

        var description = await host.PutJsonAsync(
            "/api/group/profile", new { description = new string('x', GroupPageRules.DescriptionMax + 1) }, cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, links.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, description.StatusCode);
        Assert.Empty(gate.Calls);
    }

    // ── Posts ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheListIsReadFromVRChat_WithAuthorsNamedFromWhatModbotKnows()
    {
        var ct = TestContext.Current.CancellationToken;

        var older = Post("not_old", "Older");
        older.CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        // Sent oldest first, to show the page puts the newest at the top whatever VRChat's order.
        var gate = new FakeVRChatGate().Returns(
            "GetGroupPosts",
            new GroupPostsResponse([older, Post("not_new", "Newer")], 2));

        await using var host = await StartAsync(gate, ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.VRChatUsers.Add(new VRChatUser { UserId = "usr_author", DisplayName = "Nova", FirstSeenAt = Day, LastSeenAt = Day });
            await db.SaveChangesAsync(ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var list = await host.GetJsonAsync<GroupPostList>("/api/group/posts", cookie, ct);

        Assert.Equal(["not_new", "not_old"], list.Posts.Select(p => p.Id).ToList());
        Assert.All(list.Posts, p => Assert.Equal("Nova", p.AuthorName));
        Assert.Equal(["grol_mod", "grol_member"], list.Roles.Select(r => r.Id).ToList());

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsPosts, call.Endpoint.Class);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);
    }

    [Fact]
    public async Task ReadingPostsNeedsTheAnalyticsPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("GetGroupPosts", new GroupPostsResponse([], 0));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, ct);
        var response = await host.GetAsync("/api/group/posts", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task PostingChangingAndDeletingNeedTheirOwnPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("AddGroupPost", Post("not_1", "Hello"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.EditGroupProfile, ct);

        var post = await host.PostJsonAsync("/api/group/posts", new { title = "Hello", text = "Words" }, cookie, ct);
        var edit = await host.PutJsonAsync("/api/group/posts", new { id = "not_1", title = "Hello", text = "Words" }, cookie, ct);
        var delete = await host.PostJsonAsync("/api/group/posts/delete", new { id = "not_1" }, cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task APostGoesThroughTheGate_AndTheLogSaysWhoPosted()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("AddGroupPost", Post("not_1", "Hello"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupPosts, ct);
        var response = await host.PostJsonAsync(
            "/api/group/posts",
            new { title = "  Hello ", text = "Words", visibility = "group", notify = true },
            cookie,
            ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saved = JsonSerializer.Deserialize<GroupPostSaved>(await response.Content.ReadAsStringAsync(ct), Web)!;
        Assert.Equal("not_1", saved.Post.Id);

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsPostsWrite, call.Endpoint.Class);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupPostPosted, ct));
        Assert.Equal(GroupId, fact.SubjectId);
        Assert.True(await IsAModbotAccountAsync(host, fact.ActorId, ct));

        var data = JsonDocument.Parse(fact.Data).RootElement;
        Assert.Equal("not_1", data.GetProperty("postId").GetString());
        Assert.Equal("Hello", data.GetProperty("title").GetString());
        Assert.True(data.GetProperty("notified").GetBoolean());
    }

    [Fact]
    public async Task APostWithNoTitleIsRefusedBeforeAnythingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("AddGroupPost", Post("not_1", "Hello"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupPosts, ct);
        var response = await host.PostJsonAsync("/api/group/posts", new { title = "  ", text = "Words" }, cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task ChangingAPostIsRecordedAsAChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("UpdateGroupPost", Post("not_1", "Hello again"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupPosts, ct);
        var response = await host.PutJsonAsync(
            "/api/group/posts", new { id = "not_1", title = "Hello again", text = "Words" }, cookie, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(VRChatEndpointClass.GroupsPostsWrite, Assert.Single(gate.Calls).Endpoint.Class);
        Assert.Single(await FactsAsync(host, FactType.GroupPostChanged, ct));
    }

    [Fact]
    public async Task DeletingAPostIsRecordedWithItsTitle()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("DeleteGroupPost", new Success());
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupPosts, ct);
        var response = await host.PostJsonAsync("/api/group/posts/delete", new { id = "not_1", title = "Hello" }, cookie, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(VRChatEndpointClass.GroupsPostsWrite, Assert.Single(gate.Calls).Endpoint.Class);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupPostRemoved, ct));
        Assert.Equal("Hello", JsonDocument.Parse(fact.Data).RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DeletingAPostVRChatNoLongerHasIsA404_AndNothingIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("DeleteGroupPost", VRChatResult<Success>.Failure(404, "Not Found"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupPosts, ct);
        var response = await host.PostJsonAsync("/api/group/posts/delete", new { id = "not_gone" }, cookie, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await FactsAsync(host, FactType.GroupPostRemoved, ct));
    }
}
