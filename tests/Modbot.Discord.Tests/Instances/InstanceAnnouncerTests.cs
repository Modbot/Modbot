using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Instances;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Instances;

/// <summary>
/// The notice board: one card per room, kept up to date while the room is open, given a last word
/// when it closes, and never touched again after that.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class InstanceAnnouncerTests
{
    private const string Channel = "1234567890";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string Group = "grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd";

    private readonly PostgresFixture _db;

    public InstanceAnnouncerTests(PostgresFixture db) => _db = db;

    private static string Location(string number) =>
        $"{World}:{number}~group({Group})~groupAccessType(plus)~region(us)";

    private static async Task<InstanceAnnouncePass> RunAsync(
        TestServices services, FakeGateway gateway, CancellationToken ct)
    {
        using var scope = services.Scope();
        var announcer = scope.ServiceProvider.GetRequiredService<InstanceAnnouncer>();
        return await announcer.RunOnceAsync(gateway, ct);
    }

    private static async Task<Guid> OpenRoomAsync(
        TestServices services,
        string number,
        DateTimeOffset openedAt,
        int people = 0,
        bool seenInGroupList = true,
        CancellationToken ct = default)
    {
        await using var db = services.Database.NewContext();

        var room = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = Location(number),
            WorldId = World,
            VRChatInstanceId = number,
            GroupId = Group,
            Type = "group",
            GroupAccessType = "plus",
            Region = "us",
            OpenedAt = openedAt,
            LastSeenAt = openedAt,
            LastUserCount = people,
            PeakUserCount = people,
            SeenInGroupList = seenInGroupList,
        };

        db.VRChatInstances.Add(room);
        await db.SaveChangesAsync(ct);
        return room.Id;
    }

    private static async Task<VRChatInstance> RoomAsync(TestServices services, Guid id, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();
        return await db.VRChatInstances.AsNoTracking().FirstAsync(i => i.Id == id, ct);
    }

    [Fact]
    public async Task WithNoChannelSet_NothingIsPosted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await OpenRoomAsync(services, "68681", services.Clock.UtcNow, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(InstanceAnnouncePassOutcome.NoChannel, pass.Outcome);
        Assert.Empty(gateway.Messages);
    }

    [Fact]
    public async Task ARoomThatJustOpenedGetsOneCard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s =>
        {
            s.DiscordInstanceChannelId = Channel;
            s.DiscordInstanceMessage = "We are live!";
        }, ct);

        var id = await OpenRoomAsync(services, "68681", services.Clock.UtcNow, people: 3, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(InstanceAnnouncePassOutcome.Posted, pass.Outcome);
        Assert.Equal(1, pass.Announced);

        var message = Assert.Single(gateway.Messages);
        Assert.Equal(Channel, message.ChannelId);
        Assert.Equal("We are live!", message.Text);

        var card = Assert.Single(message.Embeds);
        Assert.Contains(card.Fields, f => f.Name == "People here now" && f.Value == "3 people");

        var room = await RoomAsync(services, id, ct);
        Assert.Equal(message.MessageId, room.AnnouncementMessageId);
        Assert.Equal(Channel, room.AnnouncementChannelId);
        Assert.False(room.AnnouncementFinished);
    }

    /// <summary>
    /// The protection against pasting a channel id at nine in the evening and filling it with
    /// cards for a room that has been running since five.
    /// </summary>
    [Fact]
    public async Task ARoomThatHasBeenOpenTooLongIsNeverAnnounced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordInstanceChannelId = Channel, ct);

        var old = services.Clock.UtcNow - InstanceAnnouncer.AnnounceWithin - TimeSpan.FromMinutes(1);
        var id = await OpenRoomAsync(services, "68681", old, people: 12, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(InstanceAnnouncePassOutcome.NothingToSay, pass.Outcome);
        Assert.Empty(gateway.Messages);

        // Marked finished, so it is not reconsidered on every pass for the rest of its life.
        Assert.True((await RoomAsync(services, id, ct)).AnnouncementFinished);
    }

    [Fact]
    public async Task ACardIsRewrittenWhenThePeopleChange_ButNotMoreThanOnceAMinute()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordInstanceChannelId = Channel, ct);
        var id = await OpenRoomAsync(services, "68681", services.Clock.UtcNow, people: 1, ct: ct);

        await RunAsync(services, gateway, ct);
        Assert.Single(gateway.Messages);

        // Straight away: too soon, nothing is rewritten.
        services.Clock.Advance(TimeSpan.FromSeconds(20));
        var tooSoon = await RunAsync(services, gateway, ct);

        Assert.Equal(InstanceAnnouncePassOutcome.NothingToSay, tooSoon.Outcome);
        Assert.Empty(gateway.Edits);

        // A minute on, with more people in: rewritten once.
        services.Clock.Advance(InstanceAnnouncer.RewriteEvery);

        await using (var db = services.Database.NewContext())
        {
            var room = await db.VRChatInstances.FirstAsync(i => i.Id == id, ct);
            room.LastUserCount = 9;
            room.PeakUserCount = 9;
            await db.SaveChangesAsync(ct);
        }

        var rewritten = await RunAsync(services, gateway, ct);

        Assert.Equal(InstanceAnnouncePassOutcome.Posted, rewritten.Outcome);
        Assert.Equal(1, rewritten.Updated);

        var edit = Assert.Single(gateway.Edits);
        Assert.Equal(gateway.Messages[0].MessageId, edit.MessageId);
        Assert.Contains(Assert.Single(edit.Embeds).Fields, f => f.Value == "9 people");
    }

    [Fact]
    public async Task AClosedRoomGetsALastWordAndIsThenLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordInstanceChannelId = Channel, ct);
        var id = await OpenRoomAsync(services, "68681", services.Clock.UtcNow, people: 4, ct: ct);

        await RunAsync(services, gateway, ct);

        services.Clock.Advance(TimeSpan.FromHours(2));

        await using (var db = services.Database.NewContext())
        {
            var room = await db.VRChatInstances.FirstAsync(i => i.Id == id, ct);
            room.ClosedAt = services.Clock.UtcNow;
            room.ClosedBy = "list";
            await db.SaveChangesAsync(ct);
        }

        var closing = await RunAsync(services, gateway, ct);

        Assert.Equal(1, closing.Finished);

        var edit = Assert.Single(gateway.Edits);
        var card = Assert.Single(edit.Embeds);
        Assert.Equal("This instance has closed.", card.Description);
        Assert.Contains(card.Fields, f => f.Name == "Ran for" && f.Value == "2h 0m");

        Assert.True((await RoomAsync(services, id, ct)).AnnouncementFinished);

        // And never again, however many passes run.
        services.Clock.Advance(TimeSpan.FromHours(1));
        var after = await RunAsync(services, gateway, ct);

        Assert.Equal(InstanceAnnouncePassOutcome.NothingToSay, after.Outcome);
        Assert.Single(gateway.Edits);
    }

    /// <summary>
    /// Posting where a person happens to be is exactly what this feature must not do. Only rooms
    /// the group's own list carried are announced.
    /// </summary>
    [Fact]
    public async Task ARoomAModeratorMerelyWalkedIntoIsNeverAnnounced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordInstanceChannelId = Channel, ct);
        await OpenRoomAsync(services, "31337", services.Clock.UtcNow, people: 2, seenInGroupList: false, ct: ct);

        var pass = await RunAsync(services, gateway, ct);

        Assert.Equal(InstanceAnnouncePassOutcome.NothingToSay, pass.Outcome);
        Assert.Empty(gateway.Messages);
    }

    /// <summary>
    /// Somebody deleted the card. Trying to rewrite a message that is not there, once a minute,
    /// forever, is the failure this avoids.
    /// </summary>
    [Fact]
    public async Task AMessageThatHasBeenDeletedIsForgottenRatherThanRetriedForever()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordInstanceChannelId = Channel, ct);
        var id = await OpenRoomAsync(services, "68681", services.Clock.UtcNow, people: 1, ct: ct);

        await RunAsync(services, gateway, ct);

        services.Clock.Advance(InstanceAnnouncer.RewriteEvery + TimeSpan.FromSeconds(5));
        gateway.FailNextEdit("That message is gone, or was not posted by the bot.", permanent: true);

        await RunAsync(services, gateway, ct);

        var room = await RoomAsync(services, id, ct);
        Assert.Null(room.AnnouncementMessageId);
        Assert.True(room.AnnouncementFinished);
    }

    [Fact]
    public async Task ARefusalThatMightPassIsTriedAgainNextPass()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordInstanceChannelId = Channel, ct);
        var id = await OpenRoomAsync(services, "68681", services.Clock.UtcNow, people: 1, ct: ct);

        gateway.FailNextPost("Discord is rate limiting the bot; it will try again shortly.");

        var refused = await RunAsync(services, gateway, ct);

        Assert.Equal(InstanceAnnouncePassOutcome.Failed, refused.Outcome);
        Assert.Empty(gateway.Messages);
        Assert.Null((await RoomAsync(services, id, ct)).AnnouncementMessageId);

        var second = await RunAsync(services, gateway, ct);

        Assert.Equal(InstanceAnnouncePassOutcome.Posted, second.Outcome);
        Assert.Single(gateway.Messages);
    }

    /// <summary>
    /// A moderator's paired client reporting the room as watched, with these people already there.
    /// </summary>
    private static async Task WatchedAsync(
        TestServices services,
        string number,
        DateTimeOffset at,
        CancellationToken ct,
        params (string Id, string Name)[] people)
    {
        Guid device;
        string moderator;

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(
                db, "mod_" + Guid.NewGuid().ToString("n")[..8], TestAccounts.Password, ModbotPermissions.None, linked: true, ct);

            device = Guid.NewGuid();
            db.ClientDevices.Add(new ClientDeviceRecord
            {
                Id = device,
                TokenHash = Guid.NewGuid().ToString("n"),
                ClientVersion = "2026.9.0",
                Platform = "windows",
                IssuedToUserId = user.Id,
                IssuedAt = at,
            });

            await db.SaveChangesAsync(ct);
            moderator = user.VRChatUserId!;
        }

        await services.WriteFactAsync(Presence(FactType.InstanceJoined, moderator, "Mod", number, at, device), ct);

        foreach (var (id, name) in people)
            await services.WriteFactAsync(Presence(FactType.InstancePresenceObserved, id, name, number, at, device), ct);
    }

    private static FactRecord Presence(string type, string subject, string name, string number, DateTimeOffset at, Guid device) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        WorldId = World,
        InstanceId = number,
        Source = FactSource.Client,
        Data = new JsonObject { ["deviceId"] = device.ToString(), ["displayName"] = name },
    };

    [Fact]
    public async Task WhileAModeratorIsWatching_TheCardListsWhoIsHere_Escaped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordInstanceChannelId = Channel, ct);
        await OpenRoomAsync(services, "68681", services.Clock.UtcNow, people: 3, ct: ct);
        await WatchedAsync(services, "68681", services.Clock.UtcNow, ct, ("usr_ada", "Ada"), ("usr_bob", "**Bob**"));

        await RunAsync(services, gateway, ct);

        var card = Assert.Single(Assert.Single(gateway.Messages).Embeds);
        var who = Assert.Single(card.Fields, f => f.Name == "Who is here");

        var lines = who.Value.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("Ada", lines);
        Assert.Contains("Mod", lines);
        Assert.Contains(@"\*\*Bob\*\*", lines);
        Assert.Contains(card.Fields, f => f.Name == "People here now" && f.Value == "3 people");
    }

    [Fact]
    public async Task WithNamesTurnedOff_TheCardShowsTheHeadCountOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s =>
        {
            s.DiscordInstanceChannelId = Channel;
            s.DiscordInstanceShowNames = false;
        }, ct);

        await OpenRoomAsync(services, "68681", services.Clock.UtcNow, people: 3, ct: ct);
        await WatchedAsync(services, "68681", services.Clock.UtcNow, ct, ("usr_ada", "Ada"));

        await RunAsync(services, gateway, ct);

        var card = Assert.Single(Assert.Single(gateway.Messages).Embeds);
        Assert.DoesNotContain(card.Fields, f => f.Name == "Who is here");
        Assert.Contains(card.Fields, f => f.Name == "People here now" && f.Value == "3 people");
    }

    [Fact]
    public async Task NobodyWatching_TheCardShowsTheHeadCountOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordInstanceChannelId = Channel, ct);
        await OpenRoomAsync(services, "68681", services.Clock.UtcNow, people: 3, ct: ct);

        await RunAsync(services, gateway, ct);

        var card = Assert.Single(Assert.Single(gateway.Messages).Embeds);
        Assert.DoesNotContain(card.Fields, f => f.Name == "Who is here");
    }

    [Fact]
    public void NamesAreOnByDefault()
        => Assert.True(new Settings().DiscordInstanceShowNames);

    [Fact]
    public async Task TheCardIsPostedWithAJoinButton_AndTheClosingEditTakesItAway()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.ConfigureAsync(s => s.DiscordInstanceChannelId = Channel, ct);
        var id = await OpenRoomAsync(services, "68681", services.Clock.UtcNow, people: 2, ct: ct);

        await RunAsync(services, gateway, ct);

        var posted = Assert.Single(gateway.Messages);
        var join = Assert.Single(posted.Links);
        Assert.Equal("Join", join.Label);
        Assert.StartsWith("https://vrchat.com/home/launch?worldId=", join.Url, StringComparison.Ordinal);
        Assert.Equal(join.Url, Assert.Single(posted.Embeds).Url);

        await using (var db = services.Database.NewContext())
        {
            var room = await db.VRChatInstances.FirstAsync(i => i.Id == id, ct);
            room.ClosedAt = services.Clock.UtcNow;
            room.ClosedBy = "list";
            await db.SaveChangesAsync(ct);
        }

        await RunAsync(services, gateway, ct);

        var closing = Assert.Single(gateway.Edits);
        Assert.Empty(closing.Links);
        Assert.Null(Assert.Single(closing.Embeds).Url);
    }
}
