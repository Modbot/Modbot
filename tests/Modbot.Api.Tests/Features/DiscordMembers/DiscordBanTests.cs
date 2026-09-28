using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordMembers;

/// <summary>
/// The Discord server's ban list for the Bans page: bans in the server in settings, standing or
/// lifted, with search, for whoever may read moderation history.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordBanTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public DiscordBanTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ReadSurfaceTestHost> StartAsync(PostgresFixture db, bool canBan = true, bool listed = true)
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
            GuildId = Guild,
            Name = "The Black Cat",
            RefreshedAt = at,
            UpdatedAt = at,
            BotCanBanMembers = canBan,
            BansListedAt = listed ? at.AddHours(-2) : null,
        });

        context.DiscordBans.AddRange(
            Ban("1", "ada", "Ada", at, reason: "spam links", bannedAt: null, firstSeen: at.AddDays(-10)),
            Ban("2", "bo", "Bo", at, bannedAt: at.AddDays(-1), firstSeen: at.AddDays(-1)),
            Ban("3", "cy", "Cy", at, bannedAt: at.AddDays(-5), firstSeen: at.AddDays(-5), lifted: at.AddDays(-3)),
            new DiscordBan { GuildId = "777", UserId = "9", Username = "elsewhere", FirstSeenAt = at, UpdatedAt = at });

        await context.SaveChangesAsync(Ct);
        return host;
    }

    private static DiscordBan Ban(
        string id, string username, string display, DateTimeOffset at,
        DateTimeOffset? bannedAt, DateTimeOffset firstSeen, string? reason = null, DateTimeOffset? lifted = null) => new()
    {
        GuildId = Guild,
        UserId = id,
        Username = username,
        DisplayName = display,
        Reason = reason,
        BannedAt = bannedAt,
        FirstSeenAt = firstSeen,
        LiftedAt = lifted,
        UpdatedAt = at,
    };

    [Fact]
    public async Task TheList_ShowsBansThatStand_InTheServerInSettings_NewestFirst()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var body = await host.GetJsonAsync<DiscordBanListResponse>("/api/discord/bans", cookie, Ct);

        Assert.Equal(["2", "1"], body.Bans.Select(b => b.UserId));
        Assert.Equal(2, body.Total);
        Assert.Equal(2, body.Coverage.Standing);
        Assert.True(body.Coverage.CanRead);
        Assert.Equal(host.Clock.UtcNow.AddHours(-2), body.Coverage.ListedAt);

        var ada = body.Bans[1];
        Assert.Equal("spam links", ada.Reason);
        Assert.Null(ada.BannedAt);
    }

    [Fact]
    public async Task Filters_ForLifted_All_AndSearch()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var lifted = await host.GetJsonAsync<DiscordBanListResponse>("/api/discord/bans?status=lifted", cookie, Ct);
        Assert.Equal(["3"], lifted.Bans.Select(b => b.UserId));
        Assert.NotNull(lifted.Bans[0].LiftedAt);

        var all = await host.GetJsonAsync<DiscordBanListResponse>("/api/discord/bans?status=all", cookie, Ct);
        Assert.Equal(3, all.Total);

        var byName = await host.GetJsonAsync<DiscordBanListResponse>("/api/discord/bans?search=ADA", cookie, Ct);
        Assert.Equal(["1"], byName.Bans.Select(b => b.UserId));

        var byId = await host.GetJsonAsync<DiscordBanListResponse>("/api/discord/bans?search=2", cookie, Ct);
        Assert.Equal(["2"], byId.Bans.Select(b => b.UserId));

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/discord/bans?status=gone", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task Without_Ban_Members_It_Says_The_List_Cannot_Be_Read()
    {
        await using var host = await StartAsync(_db, canBan: false, listed: false);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var body = await host.GetJsonAsync<DiscordBanListResponse>("/api/discord/bans", cookie, Ct);

        Assert.False(body.Coverage.CanRead);
        Assert.Null(body.Coverage.ListedAt);
    }

    [Fact]
    public async Task Needs_The_Permission_To_Read_Moderation_History()
    {
        await using var host = await StartAsync(_db);
        var members = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/discord/bans", members, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/discord/bans", Ct)).StatusCode);
    }
}
