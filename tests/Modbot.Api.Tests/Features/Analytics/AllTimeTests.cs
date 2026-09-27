using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Messages;
using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Analytics.Server;
using Modbot.Api.Features.Analytics.Team;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// "All time" on each analytics page starts at that page's own first day, not the first day of
/// anything in the store.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AllTimeTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public AllTimeTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The Discord bot reads message history back when it signs in, which on a live server
    /// reached 2015. Those years belong on the Discord page and nowhere else.
    /// </summary>
    [Fact]
    public async Task AnOldDiscordMessage_MovesOnlyTheDiscordPagesStart()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var now = host.Clock.UtcNow;
        var oldMessage = new DateTimeOffset(2015, 10, 13, 18, 0, 0, TimeSpan.Zero);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

            var settings = await db.GetSettingsAsync(Ct);
            settings.DiscordGuildId = Guild;
            await db.SaveChangesAsync(Ct);

            await new MessagePartitionMaintainer(db).EnsureForAsync([oldMessage], Ct);
            db.DiscordMessages.Add(new DiscordMessage
            {
                MessageId = "1",
                SentAt = oldMessage,
                GuildId = Guild,
                ChannelId = "500",
                AuthorId = "1",
                AuthorName = "ada",
                Text = "hi",
                StoredAt = now,
            });
            await db.SaveChangesAsync(Ct);
        }

        // The group's own record starts this year: a moderator's ban, then an instance opened.
        var banned = now.AddDays(-20);
        var opened = now.AddDays(-10);

        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_1", banned, actor: "usr_mod"), Ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceCreated, "wrld_a:1", opened, actor: "usr_mod", worldId: "wrld_a", instanceId: "1"), Ct);

        await host.RebuildDailyTotalsAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var server = await host.GetJsonAsync<ServerAnalytics>("/api/analytics/server?all=true", cookie, Ct);
        Assert.Equal(new DateOnly(2015, 10, 13), server.From);
        Assert.Equal(today, server.To);
        Assert.Equal(1m, server.Messages.Single(p => p.Day == new DateOnly(2015, 10, 13)).Value);

        var instances = await host.GetJsonAsync<InstancesAnalytics>("/api/analytics/instances?all=true", cookie, Ct);
        Assert.Equal(DateOnly.FromDateTime(opened.UtcDateTime), instances.From);
        Assert.Equal(today, instances.To);

        var team = await host.GetJsonAsync<TeamAnalytics>("/api/analytics/team?all=true", cookie, Ct);
        Assert.Equal(DateOnly.FromDateTime(banned.UtcDateTime), team.From);
        Assert.Equal(today, team.To);
    }
}
