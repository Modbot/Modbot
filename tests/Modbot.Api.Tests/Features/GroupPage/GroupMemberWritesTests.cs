using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// The writes a group's own bot needs (API conventions design §8): giving a member a group role,
/// taking one away, and inviting somebody. Each behind its permission, one interactive request
/// on the endpoint class the same call already uses, a fact about the person, nothing when VRChat
/// says no, and never on Modbot's own VRChat account.
/// </summary>
/// <remarks>The gate is the scripted fake, so nothing here reaches VRChat.</remarks>
[Collection(nameof(PostgresCollection))]
public class GroupMemberWritesTests
{
    private const string GroupId = "grp_1";
    private const string Person = "usr_member";
    private const string Self = "usr_modbot";

    private static readonly DateTimeOffset Day = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public GroupMemberWritesTests(PostgresFixture db) => _db = db;

    private async Task<ReadSurfaceTestHost> StartAsync(FakeVRChatGate gate, CancellationToken ct)
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        host.Clock.UtcNow = Day;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // The thirty-second gap is read from these rows; an earlier test's would hold this one up.
        await db.GroupAutoInvites.ExecuteDeleteAsync(ct);

        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = GroupId;
        settings.VRChatSessionUserId = Self;
        settings.GroupInfoSnapshot = GroupInfoSnapshot.From(new Group
        {
            Id = GroupId,
            Name = "Test Group",
            Roles = [new GroupRole { Id = "grol_mod", Name = "Moderator", Order = 1, Permissions = [] }],
        }).ToJson();

        await db.SaveChangesAsync(ct);
        return host;
    }

    private static async Task<List<ModbotEvent>> FactsAsync(ReadSurfaceTestHost host, string type, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.Events.AsNoTracking().Where(e => e.Type == type).OrderBy(e => e.Id).ToListAsync(ct);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        ReadSurfaceTestHost host, HttpMethod method, string path, string cookie, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return await host.Client.SendAsync(request, ct);
    }

    // ── Roles ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GivingARole_IsOneInteractiveRequest_AndAFactOnTheMembersHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("AddGroupMemberRole", VRChatResult<List<string>>.Ok(["grol_mod"], 200, """["grol_mod"]"""));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await SendAsync(host, HttpMethod.Put, $"/api/group/members/{Person}/roles/grol_mod", cookie, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("grol_mod", body.RootElement.GetProperty("roleIds")[0].GetString());

        var (endpoint, priority) = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.ModerationWrite, endpoint.Class);
        Assert.Equal(VRChatCallPriority.Interactive, priority);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupRoleGiven, ct));
        Assert.Equal(Person, fact.SubjectId);
        Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);
        Assert.Equal("Moderator", JsonDocument.Parse(fact.Data).RootElement.GetProperty("roleName").GetString());
    }

    [Fact]
    public async Task TakingARole_IsTheSameTheOtherWay()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("RemoveGroupMemberRole", VRChatResult<List<string>>.Ok([], 200, "[]"));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await SendAsync(host, HttpMethod.Delete, $"/api/group/members/{Person}/roles/grol_mod", cookie, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Person, Assert.Single(await FactsAsync(host, FactType.GroupRoleTaken, ct)).SubjectId);
    }

    [Fact]
    public async Task ARole_NeedsManageGroupRoles_AndNeverTouchesModbotsOwnAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate, ct);

        var other = await host.SignedInAsync(ModbotPermissions.ManageGroupPosts, ct);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SendAsync(host, HttpMethod.Put, $"/api/group/members/{Person}/roles/grol_mod", other, ct)).StatusCode);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(host, HttpMethod.Delete, $"/api/group/members/{Self}/roles/grol_mod", cookie, ct)).StatusCode);

        Assert.Empty(gate.Calls);
    }

    [Fact]
    public async Task ARefusedRole_RecordsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("AddGroupMemberRole", VRChatResult<List<string>>.Failure(
            403, "Forbidden", rawResponse: """{"error":{"message":"You do not have permission to do that.","status_code":403}}"""));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await SendAsync(host, HttpMethod.Put, $"/api/group/members/{Person}/roles/grol_mod", cookie, ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("vrchat-refused", body.RootElement.GetProperty("code").GetString());
        Assert.Empty(await FactsAsync(host, FactType.GroupRoleGiven, ct));
    }

    // ── Invites ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnInvite_IsOneRequest_AndAFactOnTheInvitedPersonsHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("CreateGroupInvite", VRChatResult<object>.Ok(new object(), 200));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupInvites, ct);
        var response = await host.PostJsonAsync("/api/group/invites", new { userId = Person, displayName = "Member" }, cookie, ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var (endpoint, priority) = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.GroupsInvites, endpoint.Class);
        Assert.Equal(VRChatCallPriority.Interactive, priority);

        var fact = Assert.Single(await FactsAsync(host, FactType.GroupInviteSent, ct));
        Assert.Equal(Person, fact.SubjectId);
        Assert.Equal("Member", JsonDocument.Parse(fact.Data).RootElement.GetProperty("displayName").GetString());
    }

    /// <summary>One invite every thirty seconds across the deployment, auto-invites included.</summary>
    [Fact]
    public async Task ASecondInviteTooSoon_Is429WithRetryAfter_AndNoRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate().Returns("CreateGroupInvite", VRChatResult<object>.Ok(new object(), 200));
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupInvites, ct);
        (await host.PostJsonAsync("/api/group/invites", new { userId = Person }, cookie, ct)).EnsureSuccessStatusCode();

        host.Clock.UtcNow = Day.AddSeconds(10);
        var again = await host.PostJsonAsync("/api/group/invites", new { userId = "usr_second" }, cookie, ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Equal("20", again.Headers.GetValues("Retry-After").Single());
        Assert.Single(gate.Calls);
    }

    [Fact]
    public async Task AnInvite_NeedsManageGroupInvites()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new FakeVRChatGate();
        await using var host = await StartAsync(gate, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageGroupRoles, ct);
        var response = await host.PostJsonAsync("/api/group/invites", new { userId = Person }, cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(gate.Calls);
    }
}
