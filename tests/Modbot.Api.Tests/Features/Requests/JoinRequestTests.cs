using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Moderation;
using Modbot.Api.Features.Requests;
using Modbot.Api.Tests.Fakes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;

// Aliased for the reason the moderation tests alias it: VRChat's GroupMember and Modbot's
// group_member row share a name, and this file uses both.
using VRChatGroupMember = VRChat.API.Model.GroupMember;
using VRChatLimitedUser = VRChat.API.Model.GroupMemberLimitedUser;

namespace Modbot.Api.Tests.Features.Requests;

/// <summary>
/// The join queue (join requests design): read live from VRChat, answered behind its own
/// permission, written down as a fact naming who decided, and honest about a request that has
/// already been answered somewhere else.
/// </summary>
/// <remarks>
/// The gate is the scripted fake, so nothing here reaches VRChat. What is proved is that the
/// screen cannot show a queue Modbot did not read, and cannot report an answer VRChat did not
/// accept.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class JoinRequestTests
{
    private const string Group = "grp_1";
    private const string Asker = "usr_asker";
    private const string ModbotAccount = "usr_modbot";

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public JoinRequestTests(PostgresFixture db) => _db = db;

    // ── Setting the scene ──────────────────────────────────────────────────────────────────

    private static async Task SeedAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        host.Clock.UtcNow = Day;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(ct);
        settings.ManagedGroupId = Group;
        settings.VRChatSessionUserId = ModbotAccount;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>One person waiting, as VRChat lists them.</summary>
    private static VRChatGroupMember Waiting(string userId, string? displayName = "Somebody")
    {
        var member = new VRChatGroupMember
        {
            UserId = userId,
            GroupId = Group,
            CreatedAt = new DateTime(2026, 3, 9, 8, 0, 0, DateTimeKind.Utc),
        };

        if (displayName is not null)
            member.User = new VRChatLimitedUser { Id = userId, DisplayName = displayName };

        return member;
    }

    /// <summary>A gate that lists one person waiting and accepts either answer.</summary>
    private static FakeVRChatGate Answering(params VRChatGroupMember[] waiting)
        => new FakeVRChatGate()
            .SignedInAs(userId: ModbotAccount)
            .Returns("GetGroupRequests", waiting.ToList())
            .Returns("RespondGroupJoinRequest", VRChatResult<object>.Ok(new object(), 200));

    private static object Body(string userId, string key)
        => new { userId, key, reasonIds = Array.Empty<Guid>(), note = "" };

    private static async Task<ModerationActionResult> ResultOf(HttpResponseMessage response, CancellationToken ct)
        => JsonSerializer.Deserialize<ModerationActionResult>(await response.Content.ReadAsStringAsync(ct), Web)!;

    private static async Task<List<ModbotEvent>> FactsAboutAsync(
        ReadSurfaceTestHost host, string userId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.Events.AsNoTracking()
            .Where(e => e.SubjectId == userId)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);
    }

    // ── The list ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheListComesFromVRChat_NotFromTheFactLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Answering(Waiting(Asker, "Nova"));
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewJoinRequests, ct);
        var list = await host.GetJsonAsync<JoinRequestList>("/api/requests", cookie, ct);

        var row = Assert.Single(list.Requests);
        Assert.Equal(Asker, row.UserId);
        Assert.Equal("Nova", row.DisplayName);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 8, 0, 0, TimeSpan.Zero), row.AskedAt);

        // Nothing about this person is stored, so the row says so rather than implying Modbot
        // has looked them up and found nothing of interest.
        Assert.False(row.Known);
        Assert.False(row.Banned);

        // One read of the queue, on its own budget, and interactive because a person is waiting.
        var call = Assert.Single(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsRequests);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);
    }

    [Fact]
    public async Task ARowCarriesWhatModbotAlreadyKnowsAboutThePerson()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Answering(Waiting(Asker)));
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            db.GroupBans.Add(new GroupBan
            {
                GroupId = Group,
                UserId = Asker,
                BannedAt = Day.AddYears(-1),
                FirstSeenAt = Day.AddYears(-1),
                LastSeenAt = Day.AddMonths(-6),
                LiftedAt = Day.AddMonths(-6),
            });

            db.GroupMembers.Add(new GroupMember
            {
                GroupId = Group,
                UserId = Asker,
                Roles = "[]",
                FirstSeenAt = Day.AddYears(-2),
                LastSeenAt = Day.AddYears(-1),
                LeftAt = Day.AddYears(-1),
            });

            await db.SaveChangesAsync(ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewJoinRequests, ct);
        var list = await host.GetJsonAsync<JoinRequestList>("/api/requests", cookie, ct);

        var row = Assert.Single(list.Requests);

        // The whole point of answering this here rather than in VRChat: a request from somebody
        // the group threw out once does not look like everybody else's.
        Assert.True(row.Known);
        Assert.False(row.Banned);
        Assert.True(row.BannedBefore);
        Assert.True(row.WasMember);
    }

    [Fact]
    public async Task AQueueVRChatWouldNotShowIsAnError_NotAnEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate().SignedInAs(userId: ModbotAccount);
        gate.Returns("GetGroupRequests", VRChatResult<List<VRChatGroupMember>>.Failure(
            429, "VRChat rate limited this.", kind: VRChatFailureKind.RateLimited));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewJoinRequests, ct);
        var response = await host.GetAsync("/api/requests", cookie, ct);

        // An empty queue and a queue Modbot could not read look the same on a screen, and only
        // one of them means there is nothing to do.
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task WithNoGroupSetUpYet_TheListRefusesRatherThanAskingVRChatForNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Answering();
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewJoinRequests, ct);
        var response = await host.GetAsync("/api/requests", cookie, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsRequests);
    }

    // ── Permissions ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeeingTheQueue_AndAnsweringIt_AreDifferentPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Answering(Waiting(Asker)));
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        // Seeing members is not seeing the queue.
        var onlooker = await host.SignedInAsync(ModbotPermissions.ViewMembers, ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/requests", onlooker, ct)).StatusCode);

        // Seeing the queue is not answering it.
        var reader = await host.SignedInAsync(ModbotPermissions.ViewJoinRequests, ct);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/requests", reader, ct)).StatusCode);

        foreach (var action in new[] { "approve", "reject" })
        {
            var refused = await host.PostJsonAsync(
                $"/api/requests/{action}", Body(Asker, Guid.NewGuid().ToString()), reader, ct);

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Empty(await FactsAboutAsync(host, Asker, ct));
    }

    [Fact]
    public async Task BanningSomebodyIsNotPermissionToLetThemIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Answering(Waiting(Asker)));
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var banner = await host.SignedInAsync(ModbotPermissions.Ban | ModbotPermissions.Kick, ct);
        var refused = await host.PostJsonAsync(
            "/api/requests/approve", Body(Asker, Guid.NewGuid().ToString()), banner, ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // ── Answering ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovingWritesAFactNamingWhoDecided()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Answering(Waiting(Asker));
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.AnswerJoinRequests, ct);
        var result = await ResultOf(
            await host.PostJsonAsync("/api/requests/approve", Body(Asker, "key-approve-1"), cookie, ct), ct);

        Assert.True(result.Done);
        Assert.False(result.Gone);
        Assert.Null(result.Error);

        var fact = Assert.Single(await FactsAboutAsync(host, Asker, ct), e => e.Type == FactType.ActionJoinRequestApproved);
        var data = JsonDocument.Parse(fact.Data ?? "{}").RootElement;

        Assert.Equal("approve", data.GetProperty("action").GetString());
        Assert.Equal(Group, data.GetProperty("groupId").GetString());
        Assert.Equal(FactPlatform.VRChat, fact.SubjectPlatform);

        // The fact says who decided, which VRChat's own log cannot: it attributes everything to
        // Modbot's account (foundation §5.9.1).
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.False(string.IsNullOrEmpty(fact.ActorId));

        // On its own budget, and interactive.
        var call = Assert.Single(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsRequestsAnswer);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);
        Assert.Equal("RespondGroupJoinRequest", call.Endpoint.Operation);
    }

    [Fact]
    public async Task RejectingWritesItsOwnFact_NotTheApprovalOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, Answering(Waiting(Asker)));
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.AnswerJoinRequests, ct);
        var result = await ResultOf(
            await host.PostJsonAsync("/api/requests/reject", Body(Asker, "key-reject-1"), cookie, ct), ct);

        Assert.True(result.Done);

        var facts = await FactsAboutAsync(host, Asker, ct);
        Assert.Single(facts, e => e.Type == FactType.ActionJoinRequestRejected);
        Assert.DoesNotContain(facts, e => e.Type == FactType.ActionJoinRequestApproved);
    }

    [Fact]
    public async Task OneConfirmationAnswersOnce_HoweverManyTimesItIsPressed()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Answering(Waiting(Asker));
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.AnswerJoinRequests, ct);

        var first = await ResultOf(
            await host.PostJsonAsync("/api/requests/approve", Body(Asker, "key-once"), cookie, ct), ct);
        var second = await ResultOf(
            await host.PostJsonAsync("/api/requests/approve", Body(Asker, "key-once"), cookie, ct), ct);

        Assert.True(first.Done);
        Assert.False(first.Repeat);
        Assert.True(second.Done);
        Assert.True(second.Repeat);

        Assert.Single(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsRequestsAnswer);
        Assert.Single(await FactsAboutAsync(host, Asker, ct), e => e.Type == FactType.ActionJoinRequestApproved);
    }

    // ── A request that is no longer there ──────────────────────────────────────────────────

    [Fact]
    public async Task AStaleRequestFailsInPlainWords_AndIsNotRecordedAsDone()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate().SignedInAs(userId: ModbotAccount);
        gate.Returns("GetGroupRequests", new List<VRChatGroupMember> { Waiting(Asker) });
        gate.Returns("RespondGroupJoinRequest", VRChatResult<object>.Failure(404, "Not Found"));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.AnswerJoinRequests, ct);
        var response = await host.PostJsonAsync("/api/requests/approve", Body(Asker, "key-stale"), cookie, ct);

        // Answered, not thrown: a row somebody else got to first is the ordinary ending for a
        // queue two moderators are working at once.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await ResultOf(response, ct);
        Assert.False(result.Done);
        Assert.True(result.Gone);
        Assert.False(result.RateLimited);
        Assert.Equal(ModerationActionService.Gone, result.Error);

        // Nothing claims the person was let in. The attempt is recorded as an attempt.
        var facts = await FactsAboutAsync(host, Asker, ct);
        Assert.DoesNotContain(facts, e => e.Type == FactType.ActionJoinRequestApproved);
        Assert.Single(facts, e => e.Type == FactType.ActionFailed);
    }

    [Fact]
    public async Task PressingAStaleRequestAgainGetsTheSameAnswer_AndSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate().SignedInAs(userId: ModbotAccount);
        gate.Returns("RespondGroupJoinRequest", VRChatResult<object>.Failure(404, "Not Found"));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.AnswerJoinRequests, ct);

        await host.PostJsonAsync("/api/requests/reject", Body(Asker, "key-stale-twice"), cookie, ct);
        var again = await ResultOf(
            await host.PostJsonAsync("/api/requests/reject", Body(Asker, "key-stale-twice"), cookie, ct), ct);

        Assert.True(again.Repeat);
        Assert.True(again.Gone);
        Assert.False(again.Done);

        Assert.Single(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsRequestsAnswer);
    }

    /// <summary>
    /// A 429 is never retried (spec 4.3.1). The answer simply did not happen, and the moderator
    /// is told that rather than being told it did.
    /// </summary>
    [Fact]
    public async Task ARateLimitedAnswerIsReportedAsNotDone_AndNothingIsRetried()
    {
        var ct = TestContext.Current.CancellationToken;

        var gate = new FakeVRChatGate().SignedInAs(userId: ModbotAccount);
        gate.Returns("RespondGroupJoinRequest", VRChatResult<object>.Failure(
            429, "VRChat rate limited this.", kind: VRChatFailureKind.RateLimited));

        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.AnswerJoinRequests, ct);
        var result = await ResultOf(
            await host.PostJsonAsync("/api/requests/approve", Body(Asker, "key-429"), cookie, ct), ct);

        Assert.False(result.Done);
        Assert.True(result.RateLimited);
        Assert.False(result.Gone);

        Assert.Single(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsRequestsAnswer);
        Assert.Single(await FactsAboutAsync(host, Asker, ct), e => e.Type == FactType.ActionFailed);
    }

    [Fact]
    public async Task ModbotWillNotAnswerARequestFromItsOwnAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = Answering(Waiting(ModbotAccount));
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, gate);
        await host.ResetAsync(ct);
        await SeedAsync(host, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.AnswerJoinRequests, ct);
        var response = await host.PostJsonAsync(
            "/api/requests/reject", Body(ModbotAccount, "key-self"), cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(gate.Calls, c => c.Endpoint.Class == VRChatEndpointClass.GroupsRequestsAnswer);
    }
}
