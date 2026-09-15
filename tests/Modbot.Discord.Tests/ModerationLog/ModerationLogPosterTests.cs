using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>
/// The poster: posts what is new to each routed channel, skips what it has posted, honours each
/// route's types and filters, sends an event once per channel, and never sends an account fact.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ModerationLogPosterTests
{
    private const string Channel = "1234567890";
    private const string OtherChannel = "2222222222";
    private const string Target = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string Actor = "usr_2a323be9-ac4e-4502-af07-357d79c48ccf";
    private const string Group = "grp_7f8e1c4a-0000-4000-8000-000000000001";

    private readonly PostgresFixture _db;

    public ModerationLogPosterTests(PostgresFixture db) => _db = db;

    private static readonly List<TimeSpan> Waits = [];

    private static Task RecordDelay(TimeSpan wait, CancellationToken ct)
    {
        Waits.Add(wait);
        return Task.CompletedTask;
    }

    private static async Task<ModerationLogPass> RunAsync(TestServices services, FakeGateway gateway, CancellationToken ct)
    {
        using var scope = services.Scope();
        var poster = scope.ServiceProvider.GetRequiredService<ModerationLogPoster>();
        return await poster.RunOnceAsync(gateway, RecordDelay, ct);
    }

    [Fact]
    public async Task WithNoRoutes_NothingIsRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);

        var pass = await RunAsync(services, new FakeGateway(), ct);
        Assert.Equal(ModerationLogPassOutcome.NoChannel, pass.Outcome);
    }

    [Fact]
    public async Task TurningTheChannelOn_StartsFromNow_AndPostsNoHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        // History that exists before anybody set a channel.
        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, "E-Ray", ct: ct);
        var newest = await services.WriteAuditFactAsync(FactType.GroupInstanceKick, Target, Actor, "E-Ray", ct: ct);

        await services.AddRouteAsync(Channel, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.StartedFromNow, pass.Outcome);
        Assert.Empty(gateway.Posts);
        Assert.Equal(newest, (await services.ChannelPlaceAsync(Channel, ct))!.PostedThrough);

        // And the next pass finds nothing to do, rather than the history.
        var next = await RunAsync(services, gateway, ct);
        Assert.Equal(ModerationLogPassOutcome.NothingNew, next.Outcome);
        Assert.Empty(gateway.Posts);
    }

    [Fact]
    public async Task PostsNewEvents_ThenSkipsThem_AndRecordsThatItPosted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.PublicAddress = "https://modbot.example.com", ct);
        await services.AddRouteAsync(Channel, ct: ct);
        await RunAsync(services, gateway, ct);

        await services.AddProfileAsync(Target, "jessie", ct: ct);
        var ban = await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, "E-Ray", "User jessie was banned by E-Ray.", ct: ct);
        var warn = await services.WriteAuditFactAsync(FactType.GroupInstanceWarn, Target, Actor, "E-Ray", ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Posted, pass.Outcome);
        Assert.Equal(2, pass.Posted);

        var (channel, embeds) = Assert.Single(gateway.Posts);
        Assert.Equal(Channel, channel);
        Assert.Equal(["Banned", "Warned in an instance"], embeds.Select(e => e.Title));
        Assert.Contains("**jessie**", embeds[0].Fields.Single(f => f.Name == "Who").Value, StringComparison.Ordinal);
        Assert.Contains("**E-Ray**", embeds[0].Fields.Single(f => f.Name == "By").Value, StringComparison.Ordinal);
        Assert.Equal($"https://modbot.example.com/audit?subject={Target}", embeds[0].Url);

        // The place moved past everything posted, and the posting itself is a fact.
        var place = await services.ChannelPlaceAsync(Channel, ct);
        Assert.True(place!.PostedThrough >= warn);
        Assert.True(place.PostedThrough >= ban);
        Assert.Equal(services.Clock.UtcNow, place.LastPostedAt);

        var posted = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordLogPosted, ct));
        Assert.Equal(Channel, posted.SubjectId);
        Assert.Equal(FactPlatform.Discord, posted.SubjectPlatform);
        using var data = JsonDocument.Parse(posted.Data);
        Assert.Equal(2, data.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(ban, data.RootElement.GetProperty("fromId").GetInt64());

        // Same two facts, second pass: nothing goes out again. The poster's own fact is read
        // past and not posted either.
        var again = await RunAsync(services, gateway, ct);
        Assert.Equal(ModerationLogPassOutcome.NothingNew, again.Outcome);
        Assert.Single(gateway.Posts);
        Assert.Equal(2, services.Status.Snapshot().PostedInThisProcess);
    }

    [Fact]
    public async Task OnlyTheChosenTypes_GoOut_AndThePlaceStillMovesPastTheRest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, [FactType.MemberBanned], ct: ct);
        await RunAsync(services, gateway, ct);

        await services.WriteAuditFactAsync(FactType.GroupInstanceKick, Target, Actor, ct: ct);
        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);
        var join = await services.WriteAuditFactAsync(FactType.MemberJoined, "usr_other", ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Posted, pass.Outcome);
        var (_, embeds) = Assert.Single(gateway.Posts);
        Assert.Equal("Banned", Assert.Single(embeds).Title);
        Assert.Equal(join, (await services.ChannelPlaceAsync(Channel, ct))!.PostedThrough);
    }

    [Fact]
    public async Task TypesBeyondModeration_CanBeSent_WithTheAuditLogsLabels()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, [FactType.MemberJoined, FactType.DiscordMemberJoined], ct: ct);
        await RunAsync(services, gateway, ct);

        await services.WriteAuditFactAsync(FactType.MemberJoined, Target, ct: ct);
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.DiscordMemberJoined,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = "555000111",
            Source = FactSource.Discord,
        }, ct);

        await RunAsync(services, gateway, ct);

        var (_, embeds) = Assert.Single(gateway.Posts);
        Assert.Equal(["Joined the group", "Joined Discord"], embeds.Select(e => e.Title));
    }

    [Fact]
    public async Task AccountFacts_NeverReachDiscord_EvenWhenARouteNamesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        // Somebody -- or a bug -- wrote account fact types into the route by hand.
        await services.AddRouteAsync(
            Channel, [FactType.ResetLinkCreated, FactType.Login, FactType.LoginFailed, FactType.MemberBanned], ct: ct);
        await RunAsync(services, gateway, ct);

        var accountId = Guid.NewGuid().ToString();
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.ResetLinkCreated,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = accountId,
            Source = FactSource.Modbot,
            Data = new System.Text.Json.Nodes.JsonObject { ["token"] = "must-never-be-posted" },
        }, ct);
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.Login,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = accountId,
            Source = FactSource.Modbot,
        }, ct);
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.LoginFailed,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = accountId,
            Source = FactSource.Modbot,
        }, ct);
        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Posted, pass.Outcome);
        var (_, embeds) = Assert.Single(gateway.Posts);
        var embed = Assert.Single(embeds);
        Assert.Equal("Banned", embed.Title);
        Assert.DoesNotContain(gateway.Posts.SelectMany(p => p.Embeds), e =>
            (e.Description ?? string.Empty).Contains("must-never-be-posted", StringComparison.Ordinal)
            || e.Fields.Any(f => f.Value.Contains(accountId, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithOnlyUnwantedFactsNew_NothingIsPosted_AndThePlaceMoves()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, ct: ct);
        await RunAsync(services, gateway, ct);

        var last = await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.ResetLinkCreated,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = Guid.NewGuid().ToString(),
            Source = FactSource.Modbot,
        }, ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.NothingNew, pass.Outcome);
        Assert.Equal(1, pass.Read);
        Assert.Empty(gateway.Posts);
        Assert.Equal(last, (await services.ChannelPlaceAsync(Channel, ct))!.PostedThrough);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordLogPosted, ct));
    }

    [Fact]
    public async Task TurningTheRouteOff_ForgetsThePlace_SoTurningItBackOnStartsFromThen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        var route = await services.AddRouteAsync(Channel, ct: ct);
        await RunAsync(services, gateway, ct);
        Assert.NotNull(await services.ChannelPlaceAsync(Channel, ct));

        await services.ChangeRouteAsync(route.Id, r => r.Enabled = false, ct);

        var pass = await RunAsync(services, gateway, ct);
        Assert.Equal(ModerationLogPassOutcome.NoChannel, pass.Outcome);
        Assert.Null(await services.ChannelPlaceAsync(Channel, ct));

        // A ban while it was off is not posted when it comes back on.
        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);
        await services.ChangeRouteAsync(route.Id, r => r.Enabled = true, ct);

        Assert.Equal(ModerationLogPassOutcome.StartedFromNow, (await RunAsync(services, gateway, ct)).Outcome);
        Assert.Equal(ModerationLogPassOutcome.NothingNew, (await RunAsync(services, gateway, ct)).Outcome);
        Assert.Empty(gateway.Posts);
    }

    [Fact]
    public async Task TurningTheChannelOn_WithAnEmptyLog_PostsWhatComesNext()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, ct: ct);
        Assert.Equal(ModerationLogPassOutcome.StartedFromNow, (await RunAsync(services, gateway, ct)).Outcome);
        Assert.Equal(0, (await services.ChannelPlaceAsync(Channel, ct))!.PostedThrough);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Posted, pass.Outcome);
        Assert.Single(gateway.Posts);
    }

    [Fact]
    public async Task ARefusedPost_LeavesThePlaceWhereItWas_AndWaitsBeforeTryingAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, ct: ct);
        await RunAsync(services, gateway, ct);
        var before = (await services.ChannelPlaceAsync(Channel, ct))!.PostedThrough;

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);
        gateway.FailNextPost("Could not post to Discord: the bot may not post in that channel.", permanent: true);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Failed, pass.Outcome);
        Assert.Contains("may not post", pass.Error, StringComparison.Ordinal);
        Assert.Contains("may not post", services.Status.Snapshot().LastError, StringComparison.Ordinal);

        var place = await services.ChannelPlaceAsync(Channel, ct);
        Assert.Equal(before, place!.PostedThrough);
        Assert.Contains("may not post", place.LastError, StringComparison.Ordinal);
        Assert.Equal(services.Clock.UtcNow, place.LastErrorAt);

        // Straight away, the channel is left alone.
        Assert.Equal(ModerationLogPassOutcome.Waiting, (await RunAsync(services, gateway, ct)).Outcome);
        Assert.Empty(gateway.Posts);

        // Once the wait is over -- and the operator has fixed the channel -- the same ban goes out
        // and the refusal is cleared.
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        var retry = await RunAsync(services, gateway, ct);
        Assert.Equal(ModerationLogPassOutcome.Posted, retry.Outcome);
        Assert.Single(gateway.Posts);
        Assert.Null((await services.ChannelPlaceAsync(Channel, ct))!.LastError);
    }

    [Fact]
    public async Task ABacklog_IsBatchedTenToAMessage_WithAGapBetweenMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, ct: ct);
        await RunAsync(services, gateway, ct);

        for (var i = 0; i < 12; i++)
            await services.WriteAuditFactAsync(FactType.MemberBanned, $"usr_{i}", Actor, ct: ct);

        Waits.Clear();
        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(12, pass.Posted);
        Assert.Equal(2, gateway.Posts.Count);
        Assert.Equal(10, gateway.Posts[0].Embeds.Count);
        Assert.Equal(2, gateway.Posts[1].Embeds.Count);
        Assert.Single(Waits);
    }

    [Fact]
    public async Task TwoRoutesToOneChannel_ThatBothMatch_SendTheEventOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, [FactType.MemberBanned, FactType.MemberUnbanned], ct: ct);
        await services.AddRouteAsync(Channel, [FactType.MemberBanned], r => r.SubjectIds = [Target], ct: ct);
        await RunAsync(services, gateway, ct);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);
        await services.WriteAuditFactAsync(FactType.MemberUnbanned, Target, Actor, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(2, pass.Posted);
        var (channel, embeds) = Assert.Single(gateway.Posts);
        Assert.Equal(Channel, channel);
        Assert.Equal(["Banned", "Unbanned"], embeds.Select(e => e.Title));
    }

    [Fact]
    public async Task TwoChannels_EachGetWhatTheirRoutesTake()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, [FactType.MemberBanned], ct: ct);
        await services.AddRouteAsync(OtherChannel, [FactType.MemberBanned, FactType.MemberJoined], ct: ct);
        await RunAsync(services, gateway, ct);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);
        await services.WriteAuditFactAsync(FactType.MemberJoined, "usr_new", ct: ct);

        await RunAsync(services, gateway, ct);

        Assert.Equal(2, gateway.Posts.Count);
        Assert.Equal(["Banned"], gateway.Posts.Single(p => p.ChannelId == Channel).Embeds.Select(e => e.Title));
        Assert.Equal(
            ["Banned", "Joined the group"],
            gateway.Posts.Single(p => p.ChannelId == OtherChannel).Embeds.Select(e => e.Title));
    }

    [Fact]
    public async Task ARefusedChannel_DoesNotHoldUpAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, ct: ct);
        await services.AddRouteAsync(OtherChannel, ct: ct);
        await RunAsync(services, gateway, ct);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);

        // The first channel in the list refuses; the second still gets the ban.
        gateway.FailNextPost("Missing Permissions", permanent: true);
        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Posted, pass.Outcome);
        var (channel, _) = Assert.Single(gateway.Posts);
        Assert.Equal(OtherChannel, channel);
        Assert.Equal("Missing Permissions", (await services.ChannelPlaceAsync(Channel, ct))!.LastError);
        Assert.Null((await services.ChannelPlaceAsync(OtherChannel, ct))!.LastError);
    }

    [Fact]
    public async Task ASubjectRoleFilter_ReadsTheRolesSavedWithTheFact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.ManagedGroupId = Group, ct);
        await AddMemberAsync(services, Target, ["grol_staff"], ct);
        await AddMemberAsync(services, "usr_plain", ["grol_member"], ct);

        await services.AddRouteAsync(Channel, [FactType.MemberKicked], r => r.SubjectVRChatRoleIds = ["grol_staff"], ct: ct);
        await RunAsync(services, gateway, ct);

        await services.WriteAuditFactAsync(FactType.MemberKicked, "usr_plain", Actor, ct: ct);
        await services.WriteAuditFactAsync(FactType.MemberKicked, Target, Actor, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(1, pass.Posted);
        var (_, embeds) = Assert.Single(gateway.Posts);
        Assert.Contains(Target, Assert.Single(embeds).Fields.Single(f => f.Name == "Who").Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActorModbotRoleFilter_FindsTheAccount_ByIdAndByLinkedVRChatAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        ModbotUser moderator;
        Guid role;
        await using (var db = services.Database.NewContext())
        {
            moderator = await TestAccounts.CreateAsync(db, "sam", TestAccounts.Password, ModbotPermissions.ViewAuditLog, linked: true, ct);
            role = await TestAccounts.RoleForAsync(db, ModbotPermissions.ViewAuditLog, ct);
        }

        await services.AddRouteAsync(
            Channel, [FactType.MemberBanned, FactType.ReportCreated], r => r.ActorModbotRoleIds = [role], ct: ct);
        await RunAsync(services, gateway, ct);

        // A ban VRChat's audit log says the moderator's VRChat account issued.
        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, moderator.VRChatUserId, ct: ct);

        // A case file Modbot says the moderator's account wrote.
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.ReportCreated,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = Target,
            ActorPlatform = FactPlatform.Modbot,
            ActorId = moderator.Id.ToString(),
            Source = FactSource.Modbot,
        }, ct);

        // Somebody with no Modbot account at all.
        await services.WriteAuditFactAsync(FactType.MemberBanned, "usr_someone", Actor, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(2, pass.Posted);
        var (_, embeds) = Assert.Single(gateway.Posts);
        Assert.Equal(["Banned", "Case file written"], embeds.Select(e => e.Title));
        Assert.Contains("**sam**", embeds[1].Fields.Single(f => f.Name == "By").Value, StringComparison.Ordinal);
    }

    private static async Task AddMemberAsync(TestServices services, string userId, string[] roles, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = Group,
            UserId = userId,
            Roles = JsonSerializer.Serialize(roles),
            FirstSeenAt = services.Clock.UtcNow,
            LastSeenAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
