using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Messages;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Server;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// My Server: the Discord server's members, messages, voice and moderation, from daily totals, plus
/// new members who stayed and who went quiet.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ServerAnalyticsTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public ServerAnalyticsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FactRecord Discord(string type, string subject, DateTimeOffset at) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.Discord,
        SubjectId = subject,
        Source = FactSource.Discord,
    };

    private static async Task SeedAsync(ReadSurfaceTestHost host)
    {
        var now = host.Clock.UtcNow;

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            var settings = await db.GetSettingsAsync(Ct);
            settings.DiscordGuildId = Guild;

            db.DiscordChannels.AddRange(
                new DiscordChannel { ChannelId = "500", GuildId = Guild, Name = "general", FirstSeenAt = now, UpdatedAt = now },
                new DiscordChannel { ChannelId = "501", GuildId = Guild, Name = "memes", FirstSeenAt = now, UpdatedAt = now });

            db.DiscordMembers.AddRange(
                new DiscordMember { GuildId = Guild, UserId = "1", Username = "ada", DisplayName = "Ada", FirstSeenAt = now, UpdatedAt = now },
                new DiscordMember { GuildId = Guild, UserId = "2", Username = "bo", DisplayName = "Bo", FirstSeenAt = now, UpdatedAt = now, LeftAt = now.AddDays(-5) },
                new DiscordMember { GuildId = Guild, UserId = "3", Username = "cy", DisplayName = "Cy", FirstSeenAt = now, UpdatedAt = now },
                new DiscordMember { GuildId = Guild, UserId = "4", Username = "bot", DisplayName = "Music", IsBot = true, FirstSeenAt = now, UpdatedAt = now });

            await db.SaveChangesAsync(Ct);

            var id = 1;
            async Task MessageAsync(string author, DateTimeOffset at, string channel = "500")
            {
                await new MessagePartitionMaintainer(db).EnsureForAsync([at], Ct);
                db.DiscordMessages.Add(new DiscordMessage
                {
                    MessageId = (id++).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SentAt = at,
                    GuildId = Guild,
                    ChannelId = channel,
                    AuthorId = author,
                    AuthorName = author,
                    Text = "hi",
                    StoredAt = now,
                });
                await db.SaveChangesAsync(Ct);
            }

            // Ada joined 40 days ago and kept talking; Bo joined 10 days ago and left 5 days later;
            // Cy was busy 45 days ago and has said nothing since.
            await MessageAsync("1", now.AddDays(-32));
            await MessageAsync("1", now.AddDays(-9));
            await MessageAsync("1", now.AddDays(-1), "501");
            await MessageAsync("2", now.AddDays(-9), "501");
            await MessageAsync("3", now.AddDays(-45));

            var counter = new DailyTotalCounter(db, host.Clock);
            await counter.SetAsync(DailyTotalMetrics.DiscordMembersCount, 3, day: AnalyticsSql.DayOf(now.AddDays(-2)), ct: Ct);
            await counter.SetAsync(DailyTotalMetrics.DiscordMembersCount, 2, day: AnalyticsSql.DayOf(now), ct: Ct);
        }

        await host.WriteFactAsync(Discord(FactType.DiscordMemberJoined, "1", now.AddDays(-40)), Ct);
        await host.WriteFactAsync(Discord(FactType.DiscordMemberJoined, "2", now.AddDays(-10)), Ct);
        await host.WriteFactAsync(Discord(FactType.DiscordMemberLeft, "2", now.AddDays(-5)), Ct);
        await host.WriteFactAsync(Discord(FactType.DiscordMemberBanned, "2", now.AddDays(-5)), Ct);
        await host.WriteFactAsync(Discord(FactType.DiscordVoiceJoined, "3", now.AddDays(-50)), Ct);
        await host.WriteFactAsync(Discord(FactType.DiscordVoiceLeft, "3", now.AddDays(-50).AddMinutes(30)), Ct);

        await host.RebuildDailyTotalsAsync(Ct);
    }

    [Fact]
    public async Task ThePage_ChartsTheServer_FromItsDailyTotals()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?days=90", cookie, Ct);

        Assert.Equal([3m, 2m], page.MemberCount.Select(p => p.Value));
        Assert.Equal(2m, page.Joined.Sum(p => p.Value));
        Assert.Equal(1m, page.Left.Sum(p => p.Value));
        Assert.Equal(5m, page.Messages.Sum(p => p.Value));
        Assert.Equal(30m, page.VoiceMinutes.Sum(p => p.Value));
        Assert.Equal(1m, page.Bans.Sum(p => p.Value));
        Assert.Equal(5m, page.HourOfWeek.Messages.Sum());
        Assert.Equal(168, page.HourOfWeek.Messages.Count);

        Assert.Equal(["general", "memes"], page.BusiestChannels.Select(c => c.Name));
        Assert.Equal([3m, 2m], page.BusiestChannels.Select(c => c.Messages));

        var top = page.TopContributors[0];
        Assert.Equal("1", top.Who.Id);
        Assert.Equal("Ada", top.Who.Name);
        Assert.Equal("discord", top.Who.Platform);
        Assert.Equal(3m, top.Messages);
        Assert.Contains(page.TopContributors, c => c.Who.Id == "3" && c.VoiceMinutes == 30m);

        // Nine days ago Ada and Bo both talked.
        var nineDaysAgo = page.Active.Single(a => a.Day == AnalyticsSql.DayOf(host.Clock.UtcNow.AddDays(-9)));
        Assert.Equal(2, nineDaysAgo.Daily);

        // Yesterday only Ada did; the week before it, only her; the thirty days, Ada and Bo, counted once each.
        var yesterday = page.Active.Single(a => a.Day == AnalyticsSql.DayOf(host.Clock.UtcNow.AddDays(-1)));
        Assert.Equal(1, yesterday.Daily);
        Assert.Equal(1, yesterday.Weekly);
        Assert.Equal(2, yesterday.Monthly);
    }

    [Fact]
    public async Task NewMembersWhoStayed_AndMembersWhoWentQuiet()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?days=90", cookie, Ct);

        // After a week: Ada and Bo had joined long enough ago; Bo left within it; Ada talked in her second week.
        var week = page.NewMembers.Single(n => n.Days == 7);
        Assert.Equal(2, week.Joined);
        Assert.Equal(1, week.StillHere);
        Assert.Equal(1, week.StillActive);

        // After a month: only Ada joined that long ago; she was here and talked in the week after day 30.
        var month = page.NewMembers.Single(n => n.Days == 30);
        Assert.Equal(1, month.Joined);
        Assert.Equal(1, month.StillHere);
        Assert.Equal(1, month.StillActive);

        // Ada and Cy are in the server; the bot is not counted. Ada was active lately; Cy went quiet.
        Assert.Equal(2, page.Health.Members);
        Assert.Equal(1, page.Health.ActiveLast30Days);
        Assert.Equal(1, page.Health.WentQuiet);
        Assert.Equal("Cy", Assert.Single(page.Health.Quiet).Who.Name);
    }
}
