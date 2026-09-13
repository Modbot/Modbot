using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>
/// The cursor consumer: posts what is new, skips what it has posted, honours the chosen types,
/// and never sends an account fact anywhere.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ModerationLogPosterTests
{
    private const string Channel = "1234567890";
    private const string Target = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string Actor = "usr_2a323be9-ac4e-4502-af07-357d79c48ccf";

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
    public async Task TurningTheChannelOn_StartsFromNow_AndPostsNoHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        // History that exists before anybody set a channel.
        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, "E-Ray", ct: ct);
        var newest = await services.WriteAuditFactAsync(FactType.GroupInstanceKick, Target, Actor, "E-Ray", ct: ct);

        await services.ConfigureAsync(s => s.DiscordLogChannelId = Channel, ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.StartedFromNow, pass.Outcome);
        Assert.Empty(gateway.Posts);
        Assert.Equal(newest, (await services.SettingsAsync(ct)).DiscordLogPostedThrough);

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

        await services.ConfigureAsync(s =>
        {
            s.DiscordLogChannelId = Channel;
            s.PublicAddress = "https://modbot.example.com";
        }, ct);
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

        // The cursor moved past everything posted, and the posting itself is a fact.
        var settings = await services.SettingsAsync(ct);
        Assert.True(settings.DiscordLogPostedThrough >= warn);
        Assert.True(settings.DiscordLogPostedThrough >= ban);

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
    public async Task OnlyTheChosenTypes_GoOut_AndTheCursorStillMovesPastTheRest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s =>
        {
            s.DiscordLogChannelId = Channel;
            s.DiscordLogEventTypes = FactType.MemberBanned;
        }, ct);
        await RunAsync(services, gateway, ct);

        await services.WriteAuditFactAsync(FactType.GroupInstanceKick, Target, Actor, ct: ct);
        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);
        var join = await services.WriteAuditFactAsync(FactType.MemberJoined, "usr_other", ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Posted, pass.Outcome);
        var (_, embeds) = Assert.Single(gateway.Posts);
        Assert.Equal("Banned", Assert.Single(embeds).Title);
        Assert.Equal(join, (await services.SettingsAsync(ct)).DiscordLogPostedThrough);
    }

    [Fact]
    public async Task AccountFacts_NeverReachDiscord_EvenWhenTheColumnNamesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        // Somebody -- or a bug -- wrote an account fact type into the column by hand.
        await services.ConfigureAsync(s =>
        {
            s.DiscordLogChannelId = Channel;
            s.DiscordLogEventTypes = $"{FactType.ResetLinkCreated},{FactType.Login},{FactType.MemberBanned}";
        }, ct);
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
    public async Task WithTheDefaults_AndOnlyAccountFactsNew_NothingIsPosted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordLogChannelId = Channel, ct);
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
        Assert.Equal(last, (await services.SettingsAsync(ct)).DiscordLogPostedThrough);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordLogPosted, ct));
    }

    [Fact]
    public async Task ClearingTheChannel_ForgetsTheCursor_SoTurningItBackOnStartsFromThen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordLogChannelId = Channel, ct);
        await RunAsync(services, gateway, ct);
        Assert.NotNull((await services.SettingsAsync(ct)).DiscordLogPostedThrough);

        await services.ConfigureAsync(s => s.DiscordLogChannelId = null, ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.NoChannel, pass.Outcome);
        Assert.Null((await services.SettingsAsync(ct)).DiscordLogPostedThrough);
    }

    [Fact]
    public async Task TurningTheChannelOn_WithAnEmptyLog_PostsWhatComesNext()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordLogChannelId = Channel, ct);
        Assert.Equal(ModerationLogPassOutcome.StartedFromNow, (await RunAsync(services, gateway, ct)).Outcome);
        Assert.Equal(0, (await services.SettingsAsync(ct)).DiscordLogPostedThrough);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Posted, pass.Outcome);
        Assert.Single(gateway.Posts);
    }

    [Fact]
    public async Task ARefusedPost_LeavesTheCursorWhereItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordLogChannelId = Channel, ct);
        await RunAsync(services, gateway, ct);
        var before = (await services.SettingsAsync(ct)).DiscordLogPostedThrough;

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, ct: ct);
        gateway.FailNextPost("Could not post to Discord: the bot may not post in that channel.", permanent: true);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Failed, pass.Outcome);
        Assert.Contains("may not post", pass.Error, StringComparison.Ordinal);
        Assert.Equal(before, (await services.SettingsAsync(ct)).DiscordLogPostedThrough);
        Assert.Contains("may not post", services.Status.Snapshot().LastError, StringComparison.Ordinal);

        // Once the operator fixes the channel, the same ban goes out.
        var retry = await RunAsync(services, gateway, ct);
        Assert.Equal(ModerationLogPassOutcome.Posted, retry.Outcome);
        Assert.Single(gateway.Posts);
    }

    [Fact]
    public async Task ABacklog_IsBatchedTenToAMessage_WithAGapBetweenMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordLogChannelId = Channel, ct);
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
}
