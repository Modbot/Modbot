using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordLists;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordLists;

/// <summary>
/// The channel and role lists settings pick from: read from Modbot's own tables, only for the
/// server in settings, and only for somebody who can change the Discord settings.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordListTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public DiscordListTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ReadSurfaceTestHost> StartAsync(PostgresFixture db, string? guildId = Guild)
    {
        var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await context.GetSettingsAsync(Ct);
        settings.DiscordGuildId = guildId;
        await context.SaveChangesAsync(Ct);

        return host;
    }

    private static async Task SeedAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var at = host.Clock.UtcNow;

        context.DiscordServers.Add(new DiscordServer
        {
            GuildId = Guild,
            Name = "The Black Cat",
            BotCanViewAuditLog = true,
            BotCanManageRoles = true,
            RefreshedAt = at,
            UpdatedAt = at.AddMinutes(5),
        });

        context.DiscordChannels.AddRange(
            new DiscordChannel
            {
                ChannelId = "100", GuildId = Guild, Name = "Staff", Type = DiscordChannelTypes.Category,
                Position = 0, BotCanView = true, FirstSeenAt = at, UpdatedAt = at,
            },
            new DiscordChannel
            {
                ChannelId = "101", GuildId = Guild, Name = "mod-log", Type = DiscordChannelTypes.Text,
                CategoryId = "100", Position = 1, BotCanView = true, BotCanSend = true, BotCanEmbedLinks = false,
                BotCanReadHistory = true, FirstSeenAt = at, UpdatedAt = at,
            },
            new DiscordChannel
            {
                ChannelId = "102", GuildId = Guild, Name = "old-news", Type = DiscordChannelTypes.Announcement,
                Position = 2, FirstSeenAt = at, UpdatedAt = at, RemovedAt = at,
            },
            new DiscordChannel
            {
                ChannelId = "900", GuildId = "777", Name = "another-server", Type = DiscordChannelTypes.Text,
                FirstSeenAt = at, UpdatedAt = at,
            });

        context.DiscordRoles.AddRange(
            new DiscordRole
            {
                RoleId = Guild, GuildId = Guild, Name = "@everyone", Everyone = true, FirstSeenAt = at, UpdatedAt = at,
            },
            new DiscordRole
            {
                RoleId = "201", GuildId = Guild, Name = "Member", Color = 0x3498DB, Position = 1, BotCanAssign = true,
                FirstSeenAt = at, UpdatedAt = at,
            },
            new DiscordRole
            {
                RoleId = "202", GuildId = Guild, Name = "Modbot", Position = 2, Managed = true,
                FirstSeenAt = at, UpdatedAt = at,
            },
            new DiscordRole
            {
                RoleId = "901", GuildId = "777", Name = "Elsewhere", FirstSeenAt = at, UpdatedAt = at,
            });

        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Channels_ListTheServersChannels_WithTheBotsPermissions_AndWhenTheyWereRead()
    {
        await using var host = await StartAsync(_db);
        await SeedAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = await host.GetJsonAsync<DiscordChannelsResponse>("/api/discord/channels", cookie, Ct);

        Assert.Equal(Guild, body.GuildId);
        Assert.Equal("The Black Cat", body.ServerName);
        Assert.Equal(host.Clock.UtcNow, body.RefreshedAt);
        Assert.Equal(host.Clock.UtcNow.AddMinutes(5), body.UpdatedAt);
        Assert.True(body.BotCanViewAuditLog);
        Assert.True(body.BotCanManageRoles);

        // Only this server's, in Discord's order, removed ones included and marked.
        Assert.Equal(["100", "101", "102"], body.Channels.Select(c => c.Id));

        var log = body.Channels[1];
        Assert.Equal("mod-log", log.Name);
        Assert.Equal("text", log.Type);
        Assert.Equal("100", log.CategoryId);
        Assert.False(log.Removed);
        Assert.Equal(new DiscordChannelPermissionsView(true, true, true, false, false, false), log.BotPermissions);

        Assert.True(body.Channels[2].Removed);
        Assert.Equal("announcement", body.Channels[2].Type);
    }

    [Fact]
    public async Task Roles_ListTheServersRoles_HighestFirst_WithWhetherTheBotCanAssignThem()
    {
        await using var host = await StartAsync(_db);
        await SeedAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var body = await host.GetJsonAsync<DiscordRolesResponse>("/api/discord/roles", cookie, Ct);

        Assert.Equal(host.Clock.UtcNow, body.RefreshedAt);
        Assert.True(body.BotCanManageRoles);
        Assert.Equal(["202", "201", Guild], body.Roles.Select(r => r.Id));

        var member = body.Roles[1];
        Assert.Equal("Member", member.Name);
        Assert.Equal(0x3498DB, member.Color);
        Assert.True(member.BotCanAssign);
        Assert.False(member.Managed);

        Assert.True(body.Roles[0].Managed);
        Assert.False(body.Roles[0].BotCanAssign);
        Assert.True(body.Roles[2].Everyone);
    }

    [Fact]
    public async Task BeforeTheBotHasReadAnything_TheListsAreEmpty_AndNeverRead()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var channels = await host.GetJsonAsync<DiscordChannelsResponse>("/api/discord/channels", cookie, Ct);
        Assert.Equal(Guild, channels.GuildId);
        Assert.Null(channels.RefreshedAt);
        Assert.Null(channels.ServerName);
        Assert.Empty(channels.Channels);

        var roles = await host.GetJsonAsync<DiscordRolesResponse>("/api/discord/roles", cookie, Ct);
        Assert.Null(roles.RefreshedAt);
        Assert.Empty(roles.Roles);
    }

    [Fact]
    public async Task WithNoServerInSettings_NothingIsListed()
    {
        await using var host = await StartAsync(_db, guildId: null);
        await SeedAsync(host);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var channels = await host.GetJsonAsync<DiscordChannelsResponse>("/api/discord/channels", cookie, Ct);
        Assert.Null(channels.GuildId);
        Assert.Empty(channels.Channels);
    }

    [Theory]
    [InlineData("/api/discord/channels")]
    [InlineData("/api/discord/roles")]
    public async Task OnlySomebodyWhoCanChangeSettings_MayReadTheLists(string path)
    {
        await using var host = await StartAsync(_db);
        await SeedAsync(host);

        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(path, viewer, Ct)).StatusCode);

        var anonymous = await host.Client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var administrator = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(path, administrator, Ct)).StatusCode);
    }
}
