using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Messages;
using Modbot.Analytics.Retention;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.DailyTotals;

/// <summary>
/// The Discord server's daily totals: messages counted from stored messages, voice minutes from voice
/// facts, and member and moderation counts from facts -- all rebuilt the same way they were built.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordDailyTotalsTests : AnalyticsTestBase
{
    public DiscordDailyTotalsTests(PostgresFixture fixture) : base(fixture) { }

    private int _nextId = 1;

    private async Task MessageAsync(DateTimeOffset sentAt, string author = "111", string channel = "500", bool bot = false, string? thread = null)
    {
        await using var context = Database.NewContext();
        await new MessagePartitionMaintainer(context).EnsureForAsync([sentAt], Ct);

        context.DiscordMessages.Add(new DiscordMessage
        {
            MessageId = (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture),
            SentAt = sentAt,
            GuildId = "424242",
            ChannelId = channel,
            ThreadId = thread,
            AuthorId = author,
            AuthorName = "someone",
            AuthorIsBot = bot,
            Text = "hi",
            StoredAt = Clock.UtcNow,
        });

        await context.SaveChangesAsync(Ct);
    }

    private static FactRecord Discord(string type, DateTimeOffset at, string subject, DateTimeOffset? before = null) => new()
    {
        Type = type,
        OccurredAt = at,
        OccurredBefore = before,
        SubjectPlatform = FactPlatform.Discord,
        SubjectId = subject,
        Source = FactSource.Discord,
    };

    [Fact]
    public async Task Messages_AreCountedPerDay_PerChannel_PerPerson_AndPerHour_WithoutBots()
    {
        var day = Start.Date;
        var morning = new DateTimeOffset(day.AddHours(9), TimeSpan.Zero);

        await MessageAsync(morning, "111", "500");
        await MessageAsync(morning.AddMinutes(5), "111", "500");
        await MessageAsync(morning.AddHours(5), "222", "501");
        await MessageAsync(morning.AddHours(5), "222", "500", thread: "700");
        await MessageAsync(morning.AddHours(1), "999", "500", bot: true);
        await MessageAsync(morning.AddDays(1), "111", "500");

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        var today = DayOf(morning);
        Assert.Equal(4m, await ValueAsync(today, DailyTotalMetrics.DiscordMessages));
        Assert.Equal(1m, await ValueAsync(today.AddDays(1), DailyTotalMetrics.DiscordMessages));

        Assert.Equal(3m, await ValueAsync(today, DailyTotalMetrics.DiscordChannelMessages, "500"));
        Assert.Equal(1m, await ValueAsync(today, DailyTotalMetrics.DiscordChannelMessages, "501"));

        Assert.Equal(2m, await ValueAsync(today, DailyTotalMetrics.DiscordMemberMessages, "discord:111"));
        Assert.Equal(2m, await ValueAsync(today, DailyTotalMetrics.DiscordMemberMessages, "discord:222"));
        Assert.Null(await ValueAsync(today, DailyTotalMetrics.DiscordMemberMessages, "discord:999"));

        Assert.Equal(2m, await ValueAsync(today, DailyTotalMetrics.DiscordMessagesByHour, "09"));
        Assert.Equal(2m, await ValueAsync(today, DailyTotalMetrics.DiscordMessagesByHour, "14"));
    }

    [Fact]
    public async Task MessagesReadBackFromLongAgo_LandOnTheirOwnDays_OnTheNextIncrementalRun()
    {
        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        // Read back today, sent two years ago.
        var longAgo = Start.AddYears(-2);
        await MessageAsync(longAgo);

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(longAgo), DailyTotalMetrics.DiscordMessages));
    }

    [Fact]
    public async Task VoiceMinutes_CountEachStretch_OnTheDayItEnded_AtMostADay()
    {
        var t = Start;

        await WriteAsync(
            Discord(FactType.DiscordVoiceJoined, t, "111"),
            Discord(FactType.DiscordVoiceMoved, t.AddMinutes(30), "111"),
            Discord(FactType.DiscordVoiceLeft, t.AddMinutes(45), "111"),

            Discord(FactType.DiscordVoiceJoined, t, "222"),
            Discord(FactType.DiscordVoiceLeft, t.AddMinutes(10), "222", before: t.AddHours(2)),

            // A join with a second join and no leave between: capped, not a week.
            Discord(FactType.DiscordVoiceJoined, t.AddDays(-6), "333"),
            Discord(FactType.DiscordVoiceJoined, t, "333"));

        await using (var context = Database.NewContext())
            await NewJob(context).RebuildAsync(Ct);

        var day = DayOf(t);
        Assert.Equal(45m, await ValueAsync(day, DailyTotalMetrics.DiscordMemberVoiceMinutes, "discord:111"));

        // A leave seen only after a gap ends the stretch where the gap began.
        Assert.Equal(10m, await ValueAsync(day, DailyTotalMetrics.DiscordMemberVoiceMinutes, "discord:222"));
        Assert.Equal(1440m, await ValueAsync(day, DailyTotalMetrics.DiscordMemberVoiceMinutes, "discord:333"));
        Assert.Equal(45m + 10m + 1440m, await ValueAsync(day, DailyTotalMetrics.DiscordVoiceMinutes));
    }

    [Fact]
    public async Task MembersAndModeration_AreCountedFromDiscordFacts()
    {
        await WriteAsync(
            Discord(FactType.DiscordMemberJoined, Start, "1"),
            Discord(FactType.DiscordMemberJoined, Start, "2"),
            Discord(FactType.DiscordMemberLeft, Start, "1"),
            Discord(FactType.DiscordMemberBanned, Start, "1"),
            Discord(FactType.DiscordMemberKicked, Start, "3"),
            Discord(FactType.DiscordMemberTimedOut, Start, "4"),
            Discord(FactType.DiscordMessagesRemoved, Start, "4"),
            Discord(FactType.DiscordMessagesBulkRemoved, Start, "500"),

            // The VRChat group's joins are not the server's.
            Fact(FactType.MemberJoined, Start));

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        var day = DayOf(Start);
        Assert.Equal(2m, await ValueAsync(day, DailyTotalMetrics.DiscordMembersJoined));
        Assert.Equal(1m, await ValueAsync(day, DailyTotalMetrics.DiscordMembersLeft));
        Assert.Equal(1m, await ValueAsync(day, DailyTotalMetrics.DiscordBans));
        Assert.Equal(1m, await ValueAsync(day, DailyTotalMetrics.DiscordKicks));
        Assert.Equal(1m, await ValueAsync(day, DailyTotalMetrics.DiscordTimeouts));
        Assert.Equal(2m, await ValueAsync(day, DailyTotalMetrics.DiscordMessagesRemoved));
        Assert.Equal(1m, await ValueAsync(day, DailyTotalMetrics.MembersJoined));
    }

    [Fact]
    public async Task ARebuild_ReproducesTheIncrementalRun()
    {
        await MessageAsync(Start.AddDays(-3), "111");
        await MessageAsync(Start, "222", "501");
        await WriteAsync(
            Discord(FactType.DiscordVoiceJoined, Start, "111"),
            Discord(FactType.DiscordVoiceLeft, Start.AddMinutes(20), "111"),
            Discord(FactType.DiscordMemberJoined, Start.AddDays(-1), "5"));

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        var incremental = await SnapshotAsync();

        await using (var context = Database.NewContext())
            await NewJob(context).RebuildAsync(Ct);

        Assert.Equal(incremental, await SnapshotAsync());
        Assert.Contains(incremental, r => r.Contains(DailyTotalMetrics.DiscordMessages + " []", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PurgingADiscordUser_TakesTheirMessagesOutOfTheTotals()
    {
        await MessageAsync(Start, "111");
        await MessageAsync(Start, "111");
        await MessageAsync(Start, "222");

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        Assert.Equal(3m, await ValueAsync(DayOf(Start), DailyTotalMetrics.DiscordMessages));

        await using (var context = Database.NewContext())
        {
            var purger = new UserPurger(context, Clock, NewJob(context), new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock));
            await purger.PurgeAsync(FactPlatform.Discord, "111", ct: Ct);
        }

        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.DiscordMessages));
        Assert.Null(await ValueAsync(DayOf(Start), DailyTotalMetrics.DiscordMemberMessages, "discord:111"));
    }

    [Fact]
    public async Task MessageTotals_OutliveTheMessagesRetentionDrops()
    {
        var old = Start.AddMonths(-6);
        await MessageAsync(old);
        await MessageAsync(Start);

        await using (var context = Database.NewContext())
            await NewJob(context).RunIncrementalAsync(Ct);

        await using (var context = Database.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.DiscordMessageRetentionDays = 60;
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = Database.NewContext())
            await new RetentionPruner(context, Clock, new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock)).PruneAsync(Ct);

        // A fact on the old day makes a rebuild reach it; the message total there is the only record left.
        await WriteAsync(Discord(FactType.DiscordMemberJoined, old, "9"));

        await using (var context = Database.NewContext())
            await NewJob(context).RebuildAsync(Ct);

        Assert.Equal(1m, await ValueAsync(DayOf(old), DailyTotalMetrics.DiscordMessages));
        Assert.Equal(1m, await ValueAsync(DayOf(Start), DailyTotalMetrics.DiscordMessages));
    }
}
