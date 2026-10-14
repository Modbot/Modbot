using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordLink;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordLink;

/// <summary>
/// The Account links tab: the share of the VRChat group and of the Discord server that has linked, and
/// the one-time reminder staff start and stop.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordLinkShareTests
{
    private const string Url = "/api/discord/account-links";
    private const string Guild = "700";
    private const string Group = "grp_1";

    private readonly PostgresFixture _db;

    public DiscordLinkShareTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ReadSurfaceTestHost> StartAsync(bool linkingSetUp = true)
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        await context.DiscordLinkNudges.ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;
        settings.DiscordGuildId = Guild;
        settings.PublicAddress = "https://modbot.example.com";
        settings.DiscordOAuthClientId = "123456";
        settings.DiscordOAuthClientSecretEncrypted = linkingSetUp ? "sealed" : null;
        await context.SaveChangesAsync(Ct);

        return host;
    }

    /// <summary>
    /// Four people in the group (one has left; two of the three still in it have linked), and in the
    /// server four people plus a bot and one who left, of whom one has linked.
    /// </summary>
    private static async Task SeedAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var at = host.Clock.UtcNow.AddDays(-30);

        context.GroupMembers.AddRange(
            GroupMember("usr_a", at),
            GroupMember("usr_b", at),
            GroupMember("usr_c", at),
            GroupMember("usr_gone", at, left: true));

        context.DiscordMembers.AddRange(
            DiscordMember("d1", at),
            DiscordMember("d2", at),
            DiscordMember("d3", at),
            DiscordMember("d4", at),
            DiscordMember("dbot", at, isBot: true),
            DiscordMember("dgone", at, left: true));

        context.DiscordAccountLinks.AddRange(
            Link("d1", "usr_a", at),
            Link("dgone", "usr_gone", at),
            Link("d9", "usr_b", at));

        await context.SaveChangesAsync(Ct);
    }

    private static GroupMember GroupMember(string id, DateTimeOffset at, bool left = false) => new()
    {
        GroupId = Group,
        UserId = id,
        FirstSeenAt = at,
        LastSeenAt = at,
        LeftAt = left ? at.AddDays(1) : null,
    };

    private static DiscordMember DiscordMember(string id, DateTimeOffset at, bool isBot = false, bool left = false) => new()
    {
        GuildId = Guild,
        UserId = id,
        Username = id,
        DisplayName = id,
        IsBot = isBot,
        JoinedAt = at,
        FirstSeenAt = at,
        LeftAt = left ? at.AddDays(1) : null,
    };

    private static DiscordAccountLink Link(string discord, string vrchat, DateTimeOffset at) => new()
    {
        DiscordUserId = discord,
        DiscordUsername = discord,
        VRChatUserId = vrchat,
        LinkedAt = at,
    };

    [Fact]
    public async Task TheShares_CountPeopleStillInTheGroupAndTheServer_AndNoBots()
    {
        await using var host = await StartAsync();
        await SeedAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var view = await host.GetJsonAsync<DiscordLinkShareResponse>(Url, cookie, Ct);

        // Group: usr_a and usr_b linked, usr_c not, usr_gone left.
        Assert.Equal(3, view.GroupMembers);
        Assert.Equal(2, view.GroupMembersLinked);

        // Server: only d1 linked; d2, d3 and d4 are waiting; the bot and the one who left are not counted.
        Assert.Equal(4, view.DiscordMembers);
        Assert.Equal(1, view.DiscordMembersLinked);
        Assert.True(view.LinkingSetUp);

        Assert.False(view.Reminder.Running);
        Assert.Equal(3, view.Reminder.Waiting);
        Assert.Equal(0, view.Reminder.Sent);
        Assert.Equal(30, view.Reminder.SecondsBetween);
    }

    [Fact]
    public async Task SomebodyWithoutSeeAnalytics_CannotOpenTheTab_AndSomebodyWhoOnlySeesCannotStartIt()
    {
        await using var host = await StartAsync();
        await SeedAsync(host);
        var nobody = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Url, nobody, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync(Url + "/reminder/start", null, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PostJsonAsync(Url + "/reminder/stop", null, viewer, Ct)).StatusCode);
    }

    [Fact]
    public async Task StartingAndStopping_IsSettings_IsAudited_AndRepeatingEitherDoesNothingMore()
    {
        await using var host = await StartAsync();
        await SeedAsync(host);
        var staff = await host.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync(Url + "/reminder/start", null, staff, Ct)).StatusCode);

        var view = await host.GetJsonAsync<DiscordLinkShareResponse>(Url, staff, Ct);
        Assert.True(view.Reminder.Running);
        Assert.NotNull(view.Reminder.StartedAt);
        Assert.Equal(3, view.Reminder.Waiting);

        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync(Url + "/reminder/start", null, staff, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync(Url + "/reminder/stop", null, staff, Ct)).StatusCode);
        Assert.False((await host.GetJsonAsync<DiscordLinkShareResponse>(Url, staff, Ct)).Reminder.Running);

        // Stopping a reminder that is not running records nothing more.
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync(Url + "/reminder/stop", null, staff, Ct)).StatusCode);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var changes = await context.Events.AsNoTracking()
            .Where(e => e.Type == FactType.SettingsChanged)
            .ToListAsync(Ct);

        var reminder = changes
            .Where(f => ApiTestHost.DataOf(f).TryGetProperty("setting", out var name) && name.GetString() == "discordLinkReminder")
            .ToList();
        Assert.Equal(2, reminder.Count);
    }

    [Fact]
    public async Task Starting_IsRefused_WhenLinkingIsNotSetUp_OrNobodyIsWaiting()
    {
        await using (var notSetUp = await StartAsync(linkingSetUp: false))
        {
            await SeedAsync(notSetUp);
            var staff = await notSetUp.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

            Assert.Equal(HttpStatusCode.BadRequest, (await notSetUp.PostJsonAsync(Url + "/reminder/start", null, staff, Ct)).StatusCode);
            Assert.False((await notSetUp.GetJsonAsync<DiscordLinkShareResponse>(Url, staff, Ct)).Reminder.Running);
        }

        await using var empty = await StartAsync();
        var cookie = await empty.SignedInAsync(ModbotPermissions.ViewAnalytics | ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await empty.PostJsonAsync(Url + "/reminder/start", null, cookie, Ct)).StatusCode);
        Assert.False((await empty.GetJsonAsync<DiscordLinkShareResponse>(Url, cookie, Ct)).Reminder.Running);
    }

    [Fact]
    public async Task TheCounts_ShowWhoWasSentTheReminder_AndWhoCouldNotBeReached()
    {
        await using var host = await StartAsync();
        await SeedAsync(host);

        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var at = host.Clock.UtcNow;
            context.DiscordLinkNudges.AddRange(
                new DiscordLinkNudge { DiscordUserId = "d2", State = DiscordLinkNudgeStates.Sent, CreatedAt = at, SentAt = at },
                new DiscordLinkNudge { DiscordUserId = "d3", State = DiscordLinkNudgeStates.NotReached, CreatedAt = at, Error = "Closed." });
            await context.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var view = await host.GetJsonAsync<DiscordLinkShareResponse>(Url, cookie, Ct);

        Assert.Equal(1, view.Reminder.Sent);
        Assert.Equal(1, view.Reminder.NotReached);
        Assert.NotNull(view.Reminder.LastSentAt);

        // Only d4 has never been asked.
        Assert.Equal(1, view.Reminder.Waiting);
    }
}
