using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.ModerationLog;

/// <summary>
/// Repeats of one change are written into the post before them instead of posted again: the same
/// change to the same thing by the same person, inside the window, while that post is still the
/// newest message in the channel (Discord event repeats design).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EventRepeatTests
{
    private const string Channel = "1234567890";
    private const string Group = "grp_7f8e1c4a-0000-4000-8000-000000000001";
    private const string Person = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string OtherPerson = "usr_0b1c2d3e-0000-4000-8000-000000000002";
    private const string Actor = "usr_2a323be9-ac4e-4502-af07-357d79c48ccf";
    private const string OtherActor = "usr_9e8d7c6b-0000-4000-8000-000000000003";

    private readonly PostgresFixture _db;

    public EventRepeatTests(PostgresFixture db) => _db = db;

    private static Task NoDelay(TimeSpan wait, CancellationToken ct) => Task.CompletedTask;

    private static async Task<ModerationLogPass> RunAsync(TestServices services, FakeGateway gateway, CancellationToken ct)
    {
        // A scope of its own each time, as the hosted service does: nothing is carried between
        // passes except what the database holds.
        using var scope = services.Scope();
        var poster = scope.ServiceProvider.GetRequiredService<ModerationLogPoster>();
        return await poster.RunOnceAsync(gateway, NoDelay, ct);
    }

    /// <summary>A route for these types, turned on and past its first pass, so what follows is new.</summary>
    private static async Task StartAsync(TestServices services, FakeGateway gateway, string[] types, CancellationToken ct)
    {
        await services.AddRouteAsync(Channel, types, ct: ct);
        Assert.Equal(ModerationLogPassOutcome.StartedFromNow, (await RunAsync(services, gateway, ct)).Outcome);
    }

    /// <summary>The group-info poll's reading: the group changed, nobody named.</summary>
    private static Task<long> GroupUpdateAsync(TestServices services, int before, int after, CancellationToken ct)
        => GroupFieldsAsync(services, ct, ("OnlineMemberCount", before, after));

    /// <summary>A group reading that found these fields different, each as a name and its old and new number.</summary>
    private static Task<long> GroupFieldsAsync(
        TestServices services, CancellationToken ct, params (string Name, int Before, int After)[] fields)
    {
        var changed = new JsonObject();
        foreach (var (name, before, after) in fields)
        {
            changed[name] = new JsonObject
            {
                ["old"] = JsonSerializer.SerializeToNode(before),
                ["new"] = JsonSerializer.SerializeToNode(after),
            };
        }

        return services.WriteFactAsync(new FactRecord
        {
            Type = FactType.GroupInfoChanged,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = Group,
            Source = FactSource.SyncDiff,
            Data = new JsonObject { ["changed"] = changed },
        }, ct);
    }

    /// <summary>A profile refresh that found a new status.</summary>
    private static Task<long> ProfileChangedAsync(
        TestServices services,
        string person,
        string status,
        CancellationToken ct,
        FactPlatform subjectPlatform = FactPlatform.VRChat,
        FactPlatform? actorPlatform = null)
        => services.WriteFactAsync(new FactRecord
        {
            Type = FactType.UserProfileChanged,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = subjectPlatform,
            SubjectId = person,
            ActorPlatform = actorPlatform,
            ActorId = actorPlatform is null ? null : Actor,
            Source = FactSource.SyncDiff,
            Data = new JsonObject
            {
                ["changed"] = new JsonObject
                {
                    ["statusDescription"] = new JsonObject { ["old"] = "busy", ["new"] = status },
                },
            },
        }, ct);

    private static string? Field(DiscordEmbedContent card, string name)
        => card.Fields.FirstOrDefault(f => f.Name == name)?.Value;

    [Fact]
    public async Task RepeatsInsideTheWindow_EditOnePost_WithTheCountAndTheLatestChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 260, 263, ct);
        await RunAsync(services, gateway, ct);

        var (_, messageId, _, first, _) = Assert.Single(gateway.Messages);
        Assert.Equal("Group details changed", Assert.Single(first).Title);

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 263, 270, ct);
        var second = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Posted, second.Outcome);
        Assert.Equal(1, second.Posted);
        Assert.Single(gateway.Messages);

        var edit = Assert.Single(gateway.Edits);
        Assert.Equal(Channel, edit.ChannelId);
        Assert.Equal(messageId, edit.MessageId);
        var card = Assert.Single(edit.Embeds);
        Assert.Equal("Group details changed · 2 times in 5m", card.Title);

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        var latest = await GroupUpdateAsync(services, 270, 281, ct);
        await RunAsync(services, gateway, ct);

        Assert.Single(gateway.Messages);
        Assert.Equal(2, gateway.Edits.Count);
        card = Assert.Single(gateway.Edits[^1].Embeds);
        Assert.Equal("Group details changed · 3 times in 10m", card.Title);

        // The card is the latest change's: its numbers, and its time.
        Assert.Contains("270 → 281", Field(card, "Changed"), StringComparison.Ordinal);
        Assert.Equal(services.Clock.UtcNow, card.Timestamp);

        var place = (await services.ChannelPlaceAsync(Channel, ct))!;
        Assert.Equal(messageId, place.RepeatPostId);
        Assert.Equal(3, place.RepeatCount);
        Assert.True(place.PostedThrough >= latest);
    }

    /// <summary>
    /// A repeat written into a one-card post keeps the buttons under it (acting from Discord design
    /// §7): an edit sets a message's buttons whole, so the fold sends the same ones again.
    /// </summary>
    [Fact]
    public async Task ARepeat_KeepsTheButtonsUnderThePost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.UserProfileChanged], ct);

        await ProfileChangedAsync(services, Person, "away", ct);
        await RunAsync(services, gateway, ct);

        var (_, messageId, _, _, _) = Assert.Single(gateway.Messages);
        var posted = gateway.ActionsSent[messageId].Select(a => a.Id).ToList();
        Assert.NotEmpty(posted);

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await ProfileChangedAsync(services, Person, "back", ct);
        await RunAsync(services, gateway, ct);

        var edit = Assert.Single(gateway.Edits);
        Assert.Equal(messageId, edit.MessageId);
        Assert.Equal(posted, gateway.EditActions[^1].Select(a => a.Id));
    }

    /// <summary>
    /// A card somebody acted on while the channel still had it as the post for repeats -- the
    /// confirmation landing in the middle of a pass -- is not written into: the gateway reads the
    /// card first, sees the line, and the repeat gets a post of its own.
    /// </summary>
    [Fact]
    public async Task ARepeat_IsNeverWrittenIntoACardSomebodyActedOn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.UserProfileChanged], ct);

        await ProfileChangedAsync(services, Person, "away", ct);
        await RunAsync(services, gateway, ct);
        var (_, messageId, _, _, _) = Assert.Single(gateway.Messages);

        // Marked by the gateway alone: the channel's row still names the card as its post.
        await gateway.MarkHandledAsync(Channel, messageId, "Banned by **alice**", "modbot:act:", ct);
        Assert.Equal(messageId, (await services.ChannelPlaceAsync(Channel, ct))!.RepeatPostId);

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await ProfileChangedAsync(services, Person, "back", ct);
        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(1, pass.Posted);
        Assert.Empty(gateway.Edits);
        Assert.Equal(2, gateway.Messages.Count);
        Assert.NotEqual(messageId, (await services.ChannelPlaceAsync(Channel, ct))!.RepeatPostId);
    }

    /// <summary>
    /// Discord refuses a message whose cards hold more than six thousand characters between them,
    /// and a refused message is retried on every pass while the cards behind it wait.
    /// </summary>
    [Fact]
    public async Task ManyLongCards_NeverMakeAMessageOverDiscordsTotal_AndEveryCardGoesOut()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.UserProfileChanged], ct);

        for (var i = 0; i < 25; i++)
            await ProfileChangedAsync(services, $"usr_{i:D8}-0000-4000-8000-000000000000", new string('x', 4000), ct);

        await RunAsync(services, gateway, ct);

        Assert.NotEmpty(gateway.Messages);
        Assert.All(gateway.Messages, m =>
        {
            Assert.InRange(m.Embeds.Count, 1, 10);
            Assert.True(m.Embeds.Sum(EmbedSize.Of) <= EmbedSize.MessageLimit);
        });
    }

    [Fact]
    public void FewerCardsFit_WhenTheirWordsAddUpToMoreThanOneMessageTakes()
    {
        static DiscordEmbedContent Card(int length)
            => new("t", new string('d', length), 0, [], null, null, null);

        // 1500 each: four fit in 6000, a fifth does not.
        var cards = Enumerable.Range(0, 10).Select(_ => Card(1499)).ToList();

        Assert.Equal(4, EmbedSize.HowManyFit(cards));
        Assert.Equal(10, EmbedSize.HowManyFit([.. Enumerable.Range(0, 10).Select(_ => Card(100))]));

        // One card too big on its own still goes, cut to fit.
        var huge = new DiscordEmbedContent(
            "title",
            new string('d', 4000),
            0,
            [.. Enumerable.Range(0, 20).Select(i => new DiscordEmbedField($"f{i}", new string('v', 900)))],
            null, null, "footer");

        Assert.Equal(1, EmbedSize.HowManyFit([huge, Card(10)]));

        var shortened = EmbedSize.Shorten(huge);
        Assert.True(EmbedSize.Of(shortened) <= EmbedSize.MessageLimit);
        Assert.Equal("title", shortened.Title);
        Assert.Equal("footer", shortened.Footer);
    }

    [Fact]
    public async Task RepeatsInOnePass_AreOneCard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 1, 2, ct);
        services.Clock.Advance(TimeSpan.FromMinutes(2));
        await GroupUpdateAsync(services, 2, 3, ct);
        services.Clock.Advance(TimeSpan.FromMinutes(2));
        await GroupUpdateAsync(services, 3, 4, ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(3, pass.Posted);
        var (_, _, _, embeds, _) = Assert.Single(gateway.Messages);
        Assert.Equal("Group details changed · 3 times in 4m", Assert.Single(embeds).Title);
        Assert.Empty(gateway.Edits);
        Assert.Equal(3, (await services.ChannelPlaceAsync(Channel, ct))!.RepeatCount);
    }

    [Fact]
    public async Task ADifferentSubject_StartsANewPost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.UserProfileChanged], ct);

        await ProfileChangedAsync(services, Person, "online", ct);
        await RunAsync(services, gateway, ct);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await ProfileChangedAsync(services, OtherPerson, "online", ct);
        await RunAsync(services, gateway, ct);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Empty(gateway.Edits);
        Assert.All(gateway.Messages, m => Assert.DoesNotContain("times", Assert.Single(m.Embeds).Title, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADifferentPersonDoingIt_StartsANewPost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.RoleUpdated], ct);

        await services.WriteAuditFactAsync(FactType.RoleUpdated, "grol_staff", Actor, "E-Ray", ct: ct);
        await RunAsync(services, gateway, ct);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await services.WriteAuditFactAsync(FactType.RoleUpdated, "grol_staff", OtherActor, "Nova", ct: ct);
        await RunAsync(services, gateway, ct);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Empty(gateway.Edits);
    }

    [Fact]
    public async Task ADifferentSetOfChangedFields_StartsANewPost_SoAnEditIsNeverHiddenInTheCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 260, 263, ct);
        await RunAsync(services, gateway, ct);
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 263, 270, ct);
        await RunAsync(services, gateway, ct);

        Assert.Single(gateway.Messages);
        Assert.Equal("Group details changed · 2 times in 5m", Assert.Single(Assert.Single(gateway.Edits).Embeds).Title);

        // The rules changed in the same reading as the count: another set of fields, so another post.
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupFieldsAsync(services, ct, ("OnlineMemberCount", 270, 271), ("Rules", 1, 2));
        await RunAsync(services, gateway, ct);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Single(gateway.Edits);
        var rules = Assert.Single(gateway.Messages[1].Embeds);
        Assert.Equal("Group details changed", rules.Title);
        Assert.Contains("Rules", Field(rules, "Changed"), StringComparison.Ordinal);

        // And a count after it does not go into the rules post either.
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 271, 275, ct);
        await RunAsync(services, gateway, ct);

        Assert.Equal(3, gateway.Messages.Count);
        Assert.Single(gateway.Edits);
        Assert.Equal(1, (await services.ChannelPlaceAsync(Channel, ct))!.RepeatCount);
    }

    [Fact]
    public async Task ADifferentSetOfChangedFields_InOnePass_IsNotOneCard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 260, 263, ct);
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await GroupFieldsAsync(services, ct, ("Rules", 1, 2));
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await GroupUpdateAsync(services, 263, 270, ct);
        await RunAsync(services, gateway, ct);

        // Three events, three cards, in one message: nothing is counted into another's title.
        var embeds = Assert.Single(gateway.Messages).Embeds;
        Assert.Equal(3, embeds.Count);
        Assert.All(embeds, e => Assert.DoesNotContain("times", e.Title, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSameFieldsInAnotherCase_AndTheBookkeepingFieldsAlongside_StillFold()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 260, 263, ct);
        await RunAsync(services, gateway, ct);

        // The source's own bookkeeping fields are not on the card, so they cannot hide anything.
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupFieldsAsync(services, ct, ("onlineMemberCount", 263, 270), ("updatedAt", 1, 2));
        await RunAsync(services, gateway, ct);

        Assert.Single(gateway.Messages);
        Assert.Equal("Group details changed · 2 times in 5m", Assert.Single(Assert.Single(gateway.Edits).Embeds).Title);
    }

    [Fact]
    public async Task TheSameIdInAnotherSystem_StartsANewPost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.UserProfileChanged], ct);

        await ProfileChangedAsync(services, Person, "online", ct);
        await RunAsync(services, gateway, ct);

        // The same text id, but a Discord subject: not the same thing.
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await ProfileChangedAsync(services, Person, "online", ct, FactPlatform.Discord);
        await RunAsync(services, gateway, ct);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Empty(gateway.Edits);
    }

    [Fact]
    public async Task TheSameActorIdInAnotherSystem_StartsANewPost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.UserProfileChanged], ct);

        await ProfileChangedAsync(services, Person, "online", ct, actorPlatform: FactPlatform.VRChat);
        await RunAsync(services, gateway, ct);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await ProfileChangedAsync(services, Person, "online", ct, actorPlatform: FactPlatform.Discord);
        await RunAsync(services, gateway, ct);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Empty(gateway.Edits);
    }

    [Fact]
    public async Task BansAndOtherActions_AreNeverFolded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.MemberBanned, FactType.MemberUnbanned], ct);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Person, Actor, ct: ct);
        await services.WriteAuditFactAsync(FactType.MemberBanned, Person, Actor, ct: ct);
        await RunAsync(services, gateway, ct);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await services.WriteAuditFactAsync(FactType.MemberBanned, Person, Actor, ct: ct);
        await RunAsync(services, gateway, ct);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Equal(2, gateway.Messages[0].Embeds.Count);
        Assert.Empty(gateway.Edits);
        Assert.All(gateway.Messages.SelectMany(m => m.Embeds), e => Assert.Equal("Banned from the group", e.Title));
        Assert.Null((await services.ChannelPlaceAsync(Channel, ct))!.RepeatPostId);
    }

    [Fact]
    public async Task AnotherOfModbotsPostsInBetween_StartsANewPost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged, FactType.MemberBanned], ct);

        await GroupUpdateAsync(services, 1, 2, ct);
        await RunAsync(services, gateway, ct);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await services.WriteAuditFactAsync(FactType.MemberBanned, Person, Actor, ct: ct);
        await RunAsync(services, gateway, ct);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await GroupUpdateAsync(services, 2, 3, ct);
        await RunAsync(services, gateway, ct);

        Assert.Equal(
            ["Group details changed", "Banned from the group", "Group details changed"],
            gateway.Messages.Select(m => Assert.Single(m.Embeds).Title));
        Assert.Empty(gateway.Edits);

        // And in one pass: a ban between two group changes keeps them apart.
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await GroupUpdateAsync(services, 3, 4, ct);
        await services.WriteAuditFactAsync(FactType.MemberBanned, OtherPerson, Actor, ct: ct);
        await GroupUpdateAsync(services, 4, 5, ct);
        gateway.Edits.Clear();
        await RunAsync(services, gateway, ct);

        // The first goes into the post before it, which was still the newest; the other two are new.
        var edit = Assert.Single(gateway.Edits);
        Assert.Equal(gateway.Messages[2].MessageId, edit.MessageId);
        Assert.Equal(
            ["Banned from the group", "Group details changed"],
            gateway.Messages[3].Embeds.Select(e => e.Title));
    }

    [Fact]
    public async Task SomebodyElsesMessageInBetween_StartsANewPost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 1, 2, ct);
        await RunAsync(services, gateway, ct);
        var posted = gateway.Messages[0].MessageId;

        // A moderator writes in the channel after the post.
        var after = (ulong.Parse(posted, System.Globalization.CultureInfo.InvariantCulture) + 500)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        gateway.History[Channel] =
        [
            new DiscordMessageSnapshot(
                after, "guild", Channel, null, "555", "sam", false, services.Clock.UtcNow, null,
                "seen it", [], 0, null, 0, false),
        ];

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 2, 3, ct);
        await RunAsync(services, gateway, ct);

        Assert.Contains(gateway.Reads, r => r.ChannelId == Channel && r.AfterId == posted);
        Assert.Equal(2, gateway.Messages.Count);
        Assert.Empty(gateway.Edits);
        Assert.Equal("Group details changed", Assert.Single(gateway.Messages[1].Embeds).Title);
        Assert.Equal(gateway.Messages[1].MessageId, (await services.ChannelPlaceAsync(Channel, ct))!.RepeatPostId);
    }

    [Fact]
    public async Task AChannelThatCannotBeRead_StartsANewPost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 1, 2, ct);
        await RunAsync(services, gateway, ct);

        gateway.NoAccess.Add(Channel);
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 2, 3, ct);
        await RunAsync(services, gateway, ct);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Empty(gateway.Edits);
    }

    [Fact]
    public async Task OnceTheWindowHasPassed_ANewPostStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        // Every 25 minutes: the second and third go into the first post; the fourth would make it
        // cover 75 minutes, past the hour, so it starts a new one.
        await GroupUpdateAsync(services, 1, 2, ct);
        await RunAsync(services, gateway, ct);

        for (var i = 0; i < 2; i++)
        {
            services.Clock.Advance(TimeSpan.FromMinutes(25));
            await GroupUpdateAsync(services, 2 + i, 3 + i, ct);
            await RunAsync(services, gateway, ct);
        }

        Assert.Single(gateway.Messages);
        Assert.Equal(2, gateway.Edits.Count);
        Assert.Equal("Group details changed · 3 times in 50m", Assert.Single(gateway.Edits[^1].Embeds).Title);

        services.Clock.Advance(TimeSpan.FromMinutes(25));
        await GroupUpdateAsync(services, 9, 10, ct);
        await RunAsync(services, gateway, ct);

        Assert.Equal(2, gateway.Messages.Count);
        Assert.Equal(2, gateway.Edits.Count);
        Assert.Equal("Group details changed", Assert.Single(gateway.Messages[1].Embeds).Title);

        var place = (await services.ChannelPlaceAsync(Channel, ct))!;
        Assert.Equal(gateway.Messages[1].MessageId, place.RepeatPostId);
        Assert.Equal(1, place.RepeatCount);
    }

    [Fact]
    public async Task APostDeletedByHand_IsReplacedByANewOne_CountedAfresh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 1, 2, ct);
        await RunAsync(services, gateway, ct);
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 2, 3, ct);
        await RunAsync(services, gateway, ct);
        Assert.Single(gateway.Edits);

        // Somebody deletes the post; the next repeat finds it gone.
        gateway.FailNextEdit("Unknown Message", permanent: true, notFound: true);
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 3, 4, ct);
        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Posted, pass.Outcome);
        Assert.Null(pass.Error);
        Assert.Equal(2, gateway.Messages.Count);
        Assert.Single(gateway.Edits);
        Assert.Equal("Group details changed", Assert.Single(gateway.Messages[1].Embeds).Title);

        var place = (await services.ChannelPlaceAsync(Channel, ct))!;
        Assert.Equal(gateway.Messages[1].MessageId, place.RepeatPostId);
        Assert.Equal(1, place.RepeatCount);
        Assert.Null(place.LastError);
    }

    [Fact]
    public async Task ARefusedEdit_LeavesThePlace_AndTriesTheSameEditLater()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await StartAsync(services, gateway, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 1, 2, ct);
        await RunAsync(services, gateway, ct);
        var before = (await services.ChannelPlaceAsync(Channel, ct))!.PostedThrough;

        gateway.FailNextEdit("Discord is rate limiting the bot.");
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 2, 3, ct);
        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(ModerationLogPassOutcome.Failed, pass.Outcome);
        Assert.Single(gateway.Messages);
        Assert.Empty(gateway.Edits);
        Assert.Equal(before, (await services.ChannelPlaceAsync(Channel, ct))!.PostedThrough);

        services.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunAsync(services, gateway, ct);

        Assert.Single(gateway.Messages);
        Assert.Equal("Group details changed · 2 times in 5m", Assert.Single(Assert.Single(gateway.Edits).Embeds).Title);
    }

    [Fact]
    public async Task AfterARestart_RepeatsStillGoIntoTheSamePost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var before = new FakeGateway();

        await StartAsync(services, before, [FactType.GroupInfoChanged], ct);

        await GroupUpdateAsync(services, 1, 2, ct);
        await RunAsync(services, before, ct);
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 2, 3, ct);
        await RunAsync(services, before, ct);
        var messageId = Assert.Single(before.Messages).MessageId;

        // A new process: a new connection, a new poster, nothing held but the database.
        var after = new FakeGateway();
        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await GroupUpdateAsync(services, 3, 4, ct);
        await RunAsync(services, after, ct);

        Assert.Empty(after.Messages);
        var edit = Assert.Single(after.Edits);
        Assert.Equal(messageId, edit.MessageId);
        Assert.Equal("Group details changed · 3 times in 10m", Assert.Single(edit.Embeds).Title);
    }
}
