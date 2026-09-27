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
    public async Task TheServer_IsShownAsTheBotLastReadIt()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordServers.Add(new DiscordServer
            {
                GuildId = Guild,
                Name = "The Black Cat",
                IconUrl = "https://cdn.discordapp.com/icons/424242/abc.png",
                BannerUrl = "https://cdn.discordapp.com/banners/424242/def.png",
                BoostCount = 9,
                BoostLevel = 2,
                RefreshedAt = host.Clock.UtcNow,
                UpdatedAt = host.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?days=90", cookie, Ct);

        var server = page.Server;
        Assert.Equal(Guild, server.GuildId);
        Assert.Equal("The Black Cat", server.Name);
        Assert.Equal("https://cdn.discordapp.com/icons/424242/abc.png", server.IconUrl);
        Assert.Equal("https://cdn.discordapp.com/banners/424242/def.png", server.BannerUrl);
        Assert.Equal(9, server.BoostCount);
        Assert.Equal(2, server.BoostLevel);

        // The newest reading of Discord's own count.
        Assert.Equal(2, server.Members);

        // 424242 is too small to carry any time, so it reads as the first moment Discord counts from.
        Assert.Equal(new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero), server.CreatedAt);
    }

    [Fact]
    public async Task WithNoServerRow_TheServerIsEmptyButTheIdAndDateAreStillThere()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?days=90", cookie, Ct);

        Assert.Equal(Guild, page.Server.GuildId);
        Assert.Null(page.Server.Name);
        Assert.Null(page.Server.IconUrl);
        Assert.Null(page.Server.BoostCount);
        Assert.NotNull(page.Server.CreatedAt);
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

    /// <summary>A Discord id made at <paramref name="at"/>: milliseconds since 2015 in the top bits.</summary>
    private static string IdMadeAt(DateTimeOffset at) =>
        (((ulong)(at.ToUnixTimeMilliseconds() - ServerProfileQuery.DiscordEpochMs)) << 22)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheWeek_IsTheLastSevenDaysAgainstTheSevenBefore_CountingOnlyJoinersWhoStayed()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);
        var now = host.Clock.UtcNow;

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordMembers.AddRange(
                // Joined this week and still here.
                new DiscordMember { GuildId = Guild, UserId = "10", Username = "di", DisplayName = "Di", JoinedAt = now.AddDays(-2), FirstSeenAt = now, UpdatedAt = now },
                // Joined this week and left again, as a captcha kick does: not counted.
                new DiscordMember { GuildId = Guild, UserId = "11", Username = "ed", DisplayName = "Ed", JoinedAt = now.AddDays(-3), LeftAt = now.AddDays(-1), FirstSeenAt = now, UpdatedAt = now },
                // Joined the week before and still here.
                new DiscordMember { GuildId = Guild, UserId = "12", Username = "fi", DisplayName = "Fi", JoinedAt = now.AddDays(-10), FirstSeenAt = now, UpdatedAt = now },
                // A bot that joined this week: not counted.
                new DiscordMember { GuildId = Guild, UserId = "13", Username = "robot", DisplayName = "Robot", IsBot = true, JoinedAt = now.AddDays(-1), FirstSeenAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?days=7", cookie, Ct);

        var today = AnalyticsSql.DayOf(now);
        Assert.Equal(today, page.Week.To);
        Assert.Equal(today.AddDays(-6), page.Week.From);

        Assert.Equal(new WeekPair(1, 1), page.Week.NewMembers);

        // Ada talked yesterday; nine days ago Ada and Bo both did. The seven-day range does not matter.
        Assert.Equal(new WeekPair(1, 2), page.Week.Talked);
        Assert.Equal(new WeekPair(1, 2), page.Week.Messages);
        Assert.Equal(new WeekPair(0, 0), page.Week.VoiceMinutes);
    }

    [Fact]
    public async Task TheReach_CountsChannelsTheBotMayRead_AndWhetherItHasTheAuditLog()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);
        var now = host.Clock.UtcNow;

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordChannels.AddRange(
                new DiscordChannel { ChannelId = "502", GuildId = Guild, Name = "hangout", Type = DiscordChannelTypes.Voice, BotCanView = true, BotCanReadHistory = true, FirstSeenAt = now, UpdatedAt = now },
                new DiscordChannel { ChannelId = "503", GuildId = Guild, Name = "old", BotCanView = true, BotCanReadHistory = true, RemovedAt = now, FirstSeenAt = now, UpdatedAt = now },
                new DiscordChannel { ChannelId = "600", GuildId = Guild, Name = "Chat", Type = DiscordChannelTypes.Category, BotCanView = true, BotCanReadHistory = true, FirstSeenAt = now, UpdatedAt = now });
            db.DiscordServers.Add(new DiscordServer { GuildId = Guild, Name = "The Black Cat", BotCanViewAuditLog = false, RefreshedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?days=90", cookie, Ct);

        // general and memes the bot may not read, hangout it may; the deleted channel and the
        // category are not counted at all.
        Assert.Equal(new ServerReach(1, 3, false), page.Reach);
    }

    [Fact]
    public async Task BusiestChannels_CarryTheirKindTheirCategoryAndWhetherTheyWereDeleted()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);
        var now = host.Clock.UtcNow;

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordChannels.Add(new DiscordChannel { ChannelId = "600", GuildId = Guild, Name = "Chat", Type = DiscordChannelTypes.Category, FirstSeenAt = now, UpdatedAt = now });
            var memes = db.DiscordChannels.Single(c => c.ChannelId == "501");
            memes.CategoryId = "600";
            memes.Type = DiscordChannelTypes.Voice;
            memes.RemovedAt = now;
            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?days=90", cookie, Ct);

        var general = page.BusiestChannels.Single(c => c.Id == "500");
        Assert.Equal(DiscordChannelTypes.Text, general.Type);
        Assert.Null(general.Category);
        Assert.False(general.Removed);

        var memesRow = page.BusiestChannels.Single(c => c.Id == "501");
        Assert.Equal(DiscordChannelTypes.Voice, memesRow.Type);
        Assert.Equal("Chat", memesRow.Category);
        Assert.True(memesRow.Removed);
    }

    [Fact]
    public async Task MembersNow_CountsLinksNewAccountsAndHowLongPeopleHaveStayed()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);
        var now = host.Clock.UtcNow;
        var fresh = IdMadeAt(now.AddDays(-5));
        var freshGone = IdMadeAt(now.AddDays(-6));

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.DiscordMembers.AddRange(
                // A five-day-old account that joined two days ago and stayed.
                new DiscordMember { GuildId = Guild, UserId = fresh, Username = "new", DisplayName = "New", JoinedAt = now.AddDays(-2), FirstSeenAt = now, UpdatedAt = now },
                // A six-day-old account that joined and left again.
                new DiscordMember { GuildId = Guild, UserId = freshGone, Username = "gone", DisplayName = "Gone", JoinedAt = now.AddDays(-3), LeftAt = now.AddDays(-1), FirstSeenAt = now, UpdatedAt = now },
                // An old account that joined ten days ago.
                new DiscordMember { GuildId = Guild, UserId = "12", Username = "fi", DisplayName = "Fi", JoinedAt = now.AddDays(-10), FirstSeenAt = now, UpdatedAt = now },
                new DiscordMember { GuildId = Guild, UserId = "14", Username = "gus", DisplayName = "Gus", JoinedAt = now.AddDays(-100), FirstSeenAt = now, UpdatedAt = now },
                new DiscordMember { GuildId = Guild, UserId = "15", Username = "hal", DisplayName = "Hal", JoinedAt = now.AddDays(-400), FirstSeenAt = now, UpdatedAt = now },
                // A new bot: never counted.
                new DiscordMember { GuildId = Guild, UserId = IdMadeAt(now.AddDays(-1)), Username = "robot", DisplayName = "Robot", IsBot = true, JoinedAt = now.AddDays(-1), FirstSeenAt = now, UpdatedAt = now });
            db.DiscordAccountLinks.AddRange(
                new DiscordAccountLink { DiscordUserId = "3", DiscordUsername = "cy", VRChatUserId = "usr_cy", LinkedAt = now },
                new DiscordAccountLink { DiscordUserId = "1", DiscordUsername = "ada", VRChatUserId = "usr_ada", LinkedAt = now.AddDays(-20), UnlinkedAt = now.AddDays(-10) });
            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var page = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?days=90", cookie, Ct);
        var members = page.MembersNow;

        // In the server: Ada, Cy, New, Fi, Gus and Hal. Bo left; the bots are not counted.
        Assert.Equal(6, members.Members);

        // Cy's link is active; Ada's was undone.
        Assert.Equal(1, members.Linked);

        // Joined in the last thirty days: New, Gone and Fi. New and Gone came on new accounts; New stayed.
        Assert.Equal(3, members.Joined);
        Assert.Equal(2, members.NewAccounts);
        Assert.Equal(1, members.NewAccountsStillHere);

        // Ada and Cy have no join time, so they count from when the bot first saw them: just now.
        Assert.Equal(new MemberTenure(4, 1, 0, 1), members.Tenure);
    }
}
