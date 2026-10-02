using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Messages;
using Modbot.Api.Features.DiscordReports;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordReports;

/// <summary>
/// The Discord page's Roles and Channels tabs, read from what the bot stores: role counts from the
/// member list, the last message from the message store (threads included), and See analytics to
/// read either (Discord tidy-up design).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordReportTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public DiscordReportTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ReadSurfaceTestHost> StartAsync(PostgresFixture db, bool membersListed = true)
    {
        var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await context.GetSettingsAsync(Ct);
        settings.DiscordGuildId = Guild;

        var at = host.Clock.UtcNow;

        context.DiscordServers.Add(new DiscordServer
        {
            GuildId = Guild, Name = "The Black Cat", RefreshedAt = at, UpdatedAt = at,
            MembersListedAt = membersListed ? at.AddHours(-1) : null,
        });

        DiscordRole Role(string id, string name, int position, int color = 0, bool managed = false, long? permissions = 0x6_4000, bool removed = false)
            => new()
            {
                RoleId = id, GuildId = Guild, Name = name, Color = color, Position = position, Managed = managed,
                Permissions = permissions, FirstSeenAt = at, UpdatedAt = at, RemovedAt = removed ? at : null,
            };

        context.DiscordRoles.AddRange(
            new DiscordRole { RoleId = Guild, GuildId = Guild, Name = "@everyone", Everyone = true, Permissions = 0x6_4000, FirstSeenAt = at, UpdatedAt = at },
            Role("201", "Member", 1),
            Role("202", "Staff", 5, color: 0xE74C3C, permissions: 0x2_0000_0006),
            Role("203", "Moderators", 4, color: 0xE74C3C, permissions: 0x2_0000_0006),
            Role("204", "UTC-12", 2),
            Role("205", "Carl", 6, managed: true),
            Role("206", "Gone", 3, removed: true));

        DiscordMember Member(string id, string roles, DateTimeOffset? left = null) => new()
        {
            GuildId = Guild, UserId = id, Username = "u" + id, DisplayName = "U" + id, Roles = roles,
            FirstSeenAt = at, UpdatedAt = at, LeftAt = left,
        };

        context.DiscordMembers.AddRange(
            Member("1", """["201","202"]"""),
            Member("2", """["201","205"]"""),
            Member("3", """["201","203"]"""),
            // Left: not counted.
            Member("4", """["204"]""", left: at.AddDays(-1)));

        await context.SaveChangesAsync(Ct);
        return host;
    }

    private static async Task ChannelsAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var at = host.Clock.UtcNow;

        DiscordChannel Channel(
            string id, string name, string type = DiscordChannelTypes.Text, string? category = null, int position = 0,
            bool? everyone = true, bool canRead = true, bool removed = false) => new()
        {
            ChannelId = id, GuildId = Guild, Name = name, Type = type, CategoryId = category, Position = position,
            EveryoneCanView = everyone, BotCanView = canRead, BotCanReadHistory = canRead,
            FirstSeenAt = at, UpdatedAt = at, RemovedAt = removed ? at : null,
        };

        db.DiscordChannels.AddRange(
            Channel("100", "Town Square", DiscordChannelTypes.Category),
            Channel("101", "general-chat", category: "100", position: 1),
            Channel("102", "general-forum", DiscordChannelTypes.Forum, category: "100", position: 2),
            Channel("103", "mod-chat", position: 3, everyone: false),
            Channel("104", "rules-of-conduct", DiscordChannelTypes.Announcement, position: 4),
            Channel("105", "evidence-locker", position: 5, everyone: false, canRead: false),
            Channel("106", "Lounge", DiscordChannelTypes.Voice, position: 6),
            Channel("107", "deleted-channel", position: 7, removed: true),
            Channel("108", "brand-new", position: 8));

        // A forum's messages are all in its threads; one is still being read back.
        db.DiscordReadBacks.AddRange(
            new DiscordReadBack { ChannelId = "900", GuildId = Guild, ParentChannelId = "102", Name = "a post", StartedAt = at, UpdatedAt = at, FinishedAt = at },
            new DiscordReadBack { ChannelId = "108", GuildId = Guild, Name = "brand-new", StartedAt = at, UpdatedAt = at });

        await db.SaveChangesAsync(Ct);

        var times = new[] { at.AddMinutes(-10), at.AddDays(-90), at.AddDays(-2), at.AddYears(-5), at.AddDays(-400) };
        await new MessagePartitionMaintainer(db).EnsureForAsync(times, Ct);

        DiscordMessage Message(string id, string channel, DateTimeOffset sent, string? thread = null) => new()
        {
            MessageId = id, SentAt = sent, GuildId = Guild, ChannelId = channel, ThreadId = thread,
            AuthorId = "1", AuthorName = "U1", Text = "hi", StoredAt = at,
        };

        var deleted = Message("5", "103", times[2]);
        deleted.DeletedAt = times[2].AddMinutes(1);

        db.DiscordMessages.AddRange(
            Message("1", "101", times[0]),
            Message("2", "101", times[1]),
            Message("3", "102", times[1], thread: "900"),
            Message("4", "102", times[4], thread: "900"),
            deleted,
            Message("6", "104", times[3]),
            Message("7", "105", times[0]),
            Message("8", "106", times[0]));

        await db.SaveChangesAsync(Ct);
    }

    // ── Roles ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Roles_AreCountedFromTheMemberList_Marked_AndInOrder()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var report = await host.GetJsonAsync<RoleReport>("/api/discord/reports/roles", cookie, Ct);

        Assert.Equal(Guild, report.GuildId);
        Assert.Equal(host.Clock.UtcNow.AddHours(-1), report.MembersListedAt);

        // No @everyone, no deleted role.
        Assert.Equal(["204", "205", "202", "203", "201"], report.Roles.Select(r => r.Id));

        var byId = report.Roles.ToDictionary(r => r.Id);
        Assert.Equal(0, byId["204"].Members);
        Assert.Equal([RoleFlags.NoMembers], byId["204"].Flags);
        Assert.Equal([RoleFlags.BotRole], byId["205"].Flags);
        Assert.Equal([RoleFlags.SamePermissionsAndColour], byId["202"].Flags);
        Assert.Equal(["Moderators"], byId["202"].SamePermissionsAndColourAs);
        Assert.Equal(3, byId["201"].Members);
        Assert.Empty(byId["201"].Flags);
    }

    [Fact]
    public async Task Roles_BeforeTheMemberListIsRead_HaveNoCounts()
    {
        await using var host = await StartAsync(_db, membersListed: false);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var report = await host.GetJsonAsync<RoleReport>("/api/discord/reports/roles", cookie, Ct);

        Assert.Null(report.MembersListedAt);
        Assert.All(report.Roles, r => Assert.Null(r.Members));
        Assert.All(report.Roles, r => Assert.DoesNotContain(RoleFlags.NoMembers, r.Flags));
    }

    // ── Channels ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Channels_TakeTheirLastMessageFromTheStore_ThreadsIncluded_QuietestFirst()
    {
        await using var host = await StartAsync(_db);
        await ChannelsAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        var at = host.Clock.UtcNow;

        var list = await host.GetJsonAsync<QuietChannelList>("/api/discord/reports/quiet-channels", cookie, Ct);

        Assert.Equal(Guild, list.GuildId);
        Assert.Equal(at, list.Now);

        // Text, announcement and forum only; nothing deleted; quietest first.
        Assert.Equal(["104", "102", "103", "101", "108", "105"], list.Channels.Select(c => c.Id));

        var byId = list.Channels.ToDictionary(c => c.Id);
        Assert.Equal(at.AddYears(-5), byId["104"].LastMessageAt);

        // The forum's newest message is in one of its threads.
        Assert.Equal(at.AddDays(-90), byId["102"].LastMessageAt);
        Assert.Equal("Town Square", byId["102"].CategoryName);
        Assert.False(byId["102"].StillReading);

        // A deleted message still says somebody wrote there.
        Assert.Equal(at.AddDays(-2), byId["103"].LastMessageAt);
        Assert.True(byId["103"].StaffOnly);

        Assert.Equal(at.AddMinutes(-10), byId["101"].LastMessageAt);

        Assert.True(byId["108"].StillReading);
        Assert.Null(byId["108"].LastMessageAt);

        Assert.False(byId["105"].CanRead);
        Assert.Null(byId["105"].LastMessageAt);
    }

    [Fact]
    public async Task Channels_CanLeaveStaffOnlyOnesOut()
    {
        await using var host = await StartAsync(_db);
        await ChannelsAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var list = await host.GetJsonAsync<QuietChannelList>("/api/discord/reports/quiet-channels?hideStaffOnly=true", cookie, Ct);

        Assert.Equal(["104", "102", "101", "108"], list.Channels.Select(c => c.Id));
        Assert.All(list.Channels, c => Assert.False(c.StaffOnly));
    }

    [Fact]
    public async Task WithNoServerSet_BothAreEmpty()
    {
        await using var host = await StartAsync(_db);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            (await db.GetSettingsAsync(Ct)).DiscordGuildId = null;
            await db.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var roles = await host.GetJsonAsync<RoleReport>("/api/discord/reports/roles", cookie, Ct);
        Assert.Null(roles.GuildId);
        Assert.Empty(roles.Roles);

        var channels = await host.GetJsonAsync<QuietChannelList>("/api/discord/reports/quiet-channels", cookie, Ct);
        Assert.Null(channels.GuildId);
        Assert.Empty(channels.Channels);
    }

    // ── Permission ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/discord/reports/roles")]
    [InlineData("/api/discord/reports/quiet-channels")]
    public async Task EachNeedsViewAnalytics(string path)
    {
        await using var host = await StartAsync(_db);

        var others = await host.SignedInAsync(ModbotPermissions.ViewMembers | ModbotPermissions.ManageSettings, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(path, others, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(path, Ct)).StatusCode);

        var analytics = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(path, analytics, Ct)).StatusCode);
    }
}
