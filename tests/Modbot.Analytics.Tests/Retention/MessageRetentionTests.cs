using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Messages;
using Modbot.Analytics.Retention;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Retention;

/// <summary>
/// Stored Discord messages (M5 spec §5.1): their own retention setting, enforced by dropping whole
/// months, and erased with the person who wrote them.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MessageRetentionTests : AnalyticsTestBase
{
    public MessageRetentionTests(PostgresFixture fixture) : base(fixture) { }

    private const string Author = "111111111111111111";
    private const string Bystander = "222222222222222222";

    private async Task AddMessageAsync(string id, DateTimeOffset sentAt, string author = Author, params string[] earlierTexts)
    {
        await using var context = Database.NewContext();
        await new MessagePartitionMaintainer(context).EnsureForAsync([sentAt], Ct);

        context.DiscordMessages.Add(new DiscordMessage
        {
            MessageId = id,
            SentAt = sentAt,
            GuildId = "424242",
            ChannelId = "500",
            AuthorId = author,
            AuthorName = "someone",
            Text = "now",
            StoredAt = Start,
        });

        foreach (var text in earlierTexts)
            context.DiscordMessageEdits.Add(new DiscordMessageEdit { MessageId = id, SentAt = sentAt, Text = text, ReplacedAt = sentAt.AddMinutes(1) });

        await context.SaveChangesAsync(Ct);
    }

    private async Task<string[]> MessageIdsAsync()
    {
        await using var context = Database.NewContext();
        return await context.DiscordMessages.AsNoTracking().OrderBy(m => m.MessageId).Select(m => m.MessageId).ToArrayAsync(Ct);
    }

    private RetentionPruner NewPruner(ModbotContext context)
        => new(context, Clock, new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock));

    private UserPurger NewPurger(ModbotContext context) => new(
        context,
        Clock,
        NewJob(context),
        new FactWriter(context, Clock),
        new EventPartitionMaintainer(context, Clock));

    [Fact]
    public async Task MonthsPastMessageRetention_AreDropped_WithTheirEarlierTexts()
    {
        await AddMessageAsync("1", Start.AddMonths(-4), earlierTexts: "first draft");
        await AddMessageAsync("2", Start.AddDays(-40));
        await AddMessageAsync("3", Start.AddDays(-1));

        await using (var context = Database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.DiscordMessageRetentionDays = 30;
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = Database.NewContext())
        {
            var result = await NewPruner(context).PruneAsync(Ct);

            Assert.Contains("discord_message_2028_11", result.MessagesDropped);
            Assert.Contains("discord_message_edit_2028_11", result.MessagesDropped);

            // Forty days back is the end of January, whose month ends more than thirty days ago,
            // so it goes too; the month still in retention stays whole.
            Assert.Contains("discord_message_2029_01", result.MessagesDropped);
            Assert.DoesNotContain("discord_message_2029_03", result.MessagesDropped);
        }

        Assert.Equal(["3"], await MessageIdsAsync());

        await using var check = Database.NewContext();
        Assert.Equal(0, await check.DiscordMessageEdits.CountAsync(Ct));

        // A month dropped and needed again is made again.
        await AddMessageAsync("4", Start.AddDays(-40));
        Assert.Equal(["3", "4"], await MessageIdsAsync());
    }

    [Fact]
    public async Task WithMessageRetentionOff_NoMessagesAreDropped()
    {
        await AddMessageAsync("1", Start.AddYears(-3));

        await using (var context = Database.NewContext())
        {
            var result = await NewPruner(context).PruneAsync(Ct);
            Assert.Empty(result.MessagesDropped);
        }

        Assert.Equal(["1"], await MessageIdsAsync());
    }

    [Fact]
    public async Task PurgingADiscordUser_ErasesTheirMessagesAndEarlierTexts_AndNobodyElses()
    {
        await AddMessageAsync("1", Start.AddMonths(-6), Author, "typo", "second typo");
        await AddMessageAsync("2", Start, Author);
        await AddMessageAsync("3", Start, Bystander, "their own draft");

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.Discord, Author, ct: Ct);
            Assert.Equal(2, result.MessagesDeleted);
        }

        Assert.Equal(["3"], await MessageIdsAsync());

        await using var check = Database.NewContext();
        Assert.Equal(["their own draft"], await check.DiscordMessageEdits.AsNoTracking().Select(e => e.Text).ToArrayAsync(Ct));
    }

    [Fact]
    public async Task PurgingAVRChatUser_WithTheSameId_LeavesDiscordMessagesAlone()
    {
        await AddMessageAsync("1", Start, Author);

        await using (var context = Database.NewContext())
        {
            var result = await NewPurger(context).PurgeAsync(FactPlatform.VRChat, Author, ct: Ct);
            Assert.Equal(0, result.MessagesDeleted);
        }

        Assert.Equal(["1"], await MessageIdsAsync());
    }
}
