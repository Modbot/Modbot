using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;
using static Modbot.Discord.Tests.Messages.MessageTestData;

namespace Modbot.Discord.Tests.Messages;

/// <summary>
/// Messages as they happen: stored in full once, edits keep the earlier text, deletes keep the
/// row, and AI moderation checks each new message and each changed text.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordMessageHandlerTests
{
    private readonly PostgresFixture _db;

    public DiscordMessageHandlerTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A bot signed in to <see cref="MessageTestData.Guild"/>, ready.</summary>
    private static async Task<(DiscordBotService Bot, FakeGateway Gateway)> ReadyBotAsync(TestServices services)
    {
        var (bot, gateway, _) = await ReadyBotWithFactoryAsync(services);
        return (bot, gateway);
    }

    private static async Task<(DiscordBotService Bot, FakeGateway Gateway, FakeGatewayFactory Gateways)> ReadyBotWithFactoryAsync(TestServices services)
    {
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next();
        var bot = new DiscordBotService(
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            gateways,
            services.Clock,
            services.Status,
            new DiscordBotOptions(),
            (_, _) => Task.CompletedTask);

        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("token");
            s.DiscordGuildId = Guild;
        }, Ct);

        await bot.TickAsync(Ct);
        await gateway.RaiseReadyAsync();
        await bot.Reading;

        return (bot, gateway, gateways);
    }

    [Fact]
    public async Task ANewMessage_IsStoredInFull_AndCheckedOnce()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);

        var message = Message(10, text: "has anyone seen the event schedule?") with
        {
            ReplyToId = "9",
            MentionCount = 2,
            EmbedCount = 1,
        };

        await gateway.RaiseMessageAsync(message);
        await gateway.RaiseMessageAsync(message);

        await using var db = services.Database.NewContext();
        var row = await db.DiscordMessages.AsNoTracking().SingleAsync(Ct);

        Assert.Equal("10", row.MessageId);
        Assert.Equal("500", row.ChannelId);
        Assert.Null(row.ThreadId);
        Assert.Equal("900", row.AuthorId);
        Assert.Equal("Person 900", row.AuthorName);
        Assert.Equal("has anyone seen the event schedule?", row.Text);
        Assert.Equal("9", row.ReplyToId);
        Assert.Equal(2, row.MentionCount);
        Assert.Equal(1, row.EmbedCount);
        Assert.Equal(message.SentAt, row.SentAt);
        Assert.Equal(services.Clock.UtcNow, row.StoredAt);

        using var attachments = JsonDocument.Parse(row.Attachments);
        var file = attachments.RootElement[0];
        Assert.Equal("cat.png", file.GetProperty("name").GetString());
        Assert.Equal("image/png", file.GetProperty("type").GetString());
        Assert.Equal(1234, file.GetProperty("size").GetInt64());

        var seen = Assert.Single(services.Checker.Checked);
        Assert.Equal("has anyone seen the event schedule?", seen.Text);
        Assert.Equal("500", seen.ChannelId);
        Assert.Equal("Person 900", seen.AuthorName);
        Assert.False(seen.Edited);
    }

    [Fact]
    public async Task AMessageInAnotherServer_IsNotStored()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);

        await gateway.RaiseMessageAsync(Message(10) with { GuildId = "999" });

        await using var db = services.Database.NewContext();
        Assert.Equal(0, await db.DiscordMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task AnEdit_KeepsTheEarlierText_AndTheNewTextIsChecked()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);

        var original = Message(10, text: "meet at 8");
        await gateway.RaiseMessageAsync(original);

        var editedAt = original.SentAt.AddMinutes(3);
        await gateway.RaiseMessageEditedAsync(original with { Text = "meet at 9", EditedAt = editedAt });
        await gateway.RaiseMessageEditedAsync(original with { Text = "meet at 9:30", EditedAt = editedAt.AddMinutes(1) });

        // A link preview filling in is a change without an edit time: not a new version.
        await gateway.RaiseMessageEditedAsync(original with { Text = "meet at 9:30", EditedAt = null, EmbedCount = 1 });

        await using var db = services.Database.NewContext();
        var row = await db.DiscordMessages.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("meet at 9:30", row.Text);
        Assert.Equal(editedAt.AddMinutes(1), row.EditedAt);
        Assert.Equal(1, row.EmbedCount);

        var versions = await db.DiscordMessageEdits.AsNoTracking().OrderBy(e => e.ReplacedAt).ToListAsync(Ct);
        Assert.Equal(["meet at 8", "meet at 9"], versions.Select(v => v.Text));
        Assert.Equal(editedAt, versions[0].ReplacedAt);
        Assert.All(versions, v => Assert.Equal(original.SentAt, v.SentAt));

        // Checked as posted, and again after each edit that changed the text.
        Assert.Equal(["meet at 8", "meet at 9", "meet at 9:30"], services.Checker.Checked.Select(c => c.Text));
        Assert.Equal([false, true, true], services.Checker.Checked.Select(c => c.Edited));
    }

    [Fact]
    public async Task AnEditToAMessageNeverStored_StoresItAsItNowReads()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);

        await gateway.RaiseMessageEditedAsync(Message(10, text: "fixed typo", editedAt: Start.AddHours(1)));

        await using var db = services.Database.NewContext();
        Assert.Equal("fixed typo", (await db.DiscordMessages.AsNoTracking().SingleAsync(Ct)).Text);
        Assert.Equal(0, await db.DiscordMessageEdits.CountAsync(Ct));
    }

    [Fact]
    public async Task DeletedMessages_KeepTheirRows_AndAreMarked()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (_, gateway) = await ReadyBotAsync(services);

        foreach (var id in new long[] { 10, 11, 12, 13 })
            await gateway.RaiseMessageAsync(Message(id, text: $"message {id}"));

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await gateway.RaiseMessagesDeletedAsync(Guild, "500", "10");

        services.Clock.Advance(TimeSpan.FromMinutes(5));
        await gateway.RaiseMessagesDeletedAsync(Guild, "500", "11", "12", "10");

        await using var db = services.Database.NewContext();
        var rows = await db.DiscordMessages.AsNoTracking().OrderBy(m => m.MessageId).ToListAsync(Ct);

        Assert.Equal(4, rows.Count);
        Assert.Equal(services.Clock.UtcNow.AddMinutes(-5), rows[0].DeletedAt);
        Assert.Equal(services.Clock.UtcNow, rows[1].DeletedAt);
        Assert.Equal(services.Clock.UtcNow, rows[2].DeletedAt);
        Assert.Null(rows[3].DeletedAt);
        Assert.Equal("message 10", rows[0].Text);
    }

    [Fact]
    public async Task AMessageOlderThanMessageRetention_IsNotStored()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s => s.DiscordMessageRetentionDays = 30, Ct);

        using var scope = services.Scope();
        var store = scope.ServiceProvider.GetRequiredService<Modbot.Discord.Messages.DiscordMessageStore>();

        var fresh = await store.StoreAsync(
        [
            Message(1, sentAt: services.Clock.UtcNow.AddDays(-45)),
            Message(2, sentAt: services.Clock.UtcNow.AddDays(-2)),
        ], Ct);

        Assert.Equal(["2"], fresh.Select(m => m.Id));
    }

    [Fact]
    public async Task MessagesFromYearsAgo_GetTheirMonthMadeWhenStored()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);

        using var scope = services.Scope();
        var store = scope.ServiceProvider.GetRequiredService<Modbot.Discord.Messages.DiscordMessageStore>();

        var fresh = await store.StoreAsync(
        [
            Message(1, sentAt: new DateTimeOffset(2019, 3, 14, 10, 0, 0, TimeSpan.Zero)),
            Message(2, sentAt: new DateTimeOffset(2021, 12, 31, 23, 59, 0, TimeSpan.Zero)),
        ], Ct);

        Assert.Equal(2, fresh.Count);

        await using var db = services.Database.NewContext();
        var partitions = await db.Database
            .SqlQuery<string>($"SELECT child.relname AS \"Value\" FROM pg_inherits i JOIN pg_class child ON child.oid = i.inhrelid JOIN pg_class parent ON parent.oid = i.inhparent WHERE parent.relname = 'discord_message' ORDER BY 1")
            .ToListAsync(Ct);

        Assert.Equal(["discord_message_2019_03", "discord_message_2021_12"], partitions);
    }

    [Fact]
    public async Task WhenDiscordRefusesAnIntent_TheHealthPageNamesIt_AndTheBotCarriesOnWithoutIt()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (bot, gateway, gateways) = await ReadyBotWithFactoryAsync(services);

        await gateway.RaiseDisconnectedAsync(new DiscordDisconnect(
            "Discord refused the gateway intents. Turn these on in the Developer Portal under Bot: Message Content Intent.",
            Fatal: true,
            IntentsRefused: true,
            MissingIntents: ["Message Content Intent"]));

        var snapshot = services.Status.Snapshot();
        Assert.Equal(["Message Content Intent"], snapshot.MissingIntents);
        Assert.Contains("Message Content Intent", snapshot.LastError);
        Assert.True(gateway.Disposed);

        // Straight back, without the refused intent and with everything else.
        var without = gateways.Next(g => g.ReadyOnConnect = true);
        await bot.TickAsync(Ct);

        Assert.Same(without, bot.ReadyGateway);
        Assert.True(without.Options.MemberEvents);
        Assert.False(without.Options.MessageContent);
        Assert.Equal(["Message Content Intent"], services.Status.Snapshot().MissingIntents);

        // Settings saved with a change ask for everything again; once Discord takes it, the card clears.
        var everything = gateways.Next(g => g.ReadyOnConnect = true);
        await services.ConfigureAsync(s => s.DiscordLinkPromptNewMembers = true, Ct);
        await bot.TickAsync(Ct);

        Assert.Same(everything, bot.ReadyGateway);
        Assert.True(everything.Options.MessageContent);
        await everything.RaiseReadyAsync();
        Assert.Empty(services.Status.Snapshot().MissingIntents!);
    }

    [Fact]
    public void TheIntentsOffForTheApplication_AreNamedAsTheDeveloperPortalNamesThem()
    {
        Assert.Equal(
            ["Server Members Intent", "Message Content Intent"],
            DiscordNetGateway.MissingIntents(global::Discord.ApplicationFlags.GatewayPresenceLimited));

        Assert.Equal(
            ["Message Content Intent"],
            DiscordNetGateway.MissingIntents(global::Discord.ApplicationFlags.GatewayGuildMembersLimited));

        Assert.Empty(DiscordNetGateway.MissingIntents(
            global::Discord.ApplicationFlags.GatewayGuildMembers | global::Discord.ApplicationFlags.GatewayMessageContentLimited));
    }
}
