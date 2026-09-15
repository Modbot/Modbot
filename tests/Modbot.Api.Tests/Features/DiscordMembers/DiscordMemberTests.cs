using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordMembers;

/// <summary>
/// The Discord server's member list: current and past members of the server in settings, with
/// search and a role filter, for whoever may see the group's members.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordMemberTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public DiscordMemberTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ReadSurfaceTestHost> StartAsync(PostgresFixture db)
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
            GuildId = Guild, Name = "The Black Cat", RefreshedAt = at, UpdatedAt = at, MembersListedAt = at.AddHours(-1),
        });

        context.DiscordRoles.AddRange(
            new DiscordRole { RoleId = "201", GuildId = Guild, Name = "Member", Color = 0x3498DB, Position = 1, FirstSeenAt = at, UpdatedAt = at },
            new DiscordRole { RoleId = "202", GuildId = Guild, Name = "Staff", Position = 2, FirstSeenAt = at, UpdatedAt = at });

        context.DiscordMembers.AddRange(
            Member("1", "ada", "Ada", at.AddDays(-30), roles: """["201","202"]""", nickname: "Ada the Brave", at: at),
            Member("2", "bo", "Bo", at.AddDays(-2), roles: """["201"]""", at: at),
            Member("3", "cy_100%", "Cy", at.AddDays(-60), left: at.AddDays(-1), at: at),
            new DiscordMember { GuildId = "777", UserId = "9", Username = "elsewhere", DisplayName = "Elsewhere", FirstSeenAt = at, UpdatedAt = at });

        await context.SaveChangesAsync(Ct);
        return host;
    }

    private static DiscordMember Member(
        string id, string username, string display, DateTimeOffset joined, DateTimeOffset at,
        string roles = "[]", string? nickname = null, DateTimeOffset? left = null) => new()
    {
        GuildId = Guild,
        UserId = id,
        Username = username,
        DisplayName = nickname ?? display,
        GlobalName = display,
        Nickname = nickname,
        AvatarUrl = $"https://cdn.discordapp.com/avatars/{id}/a.png",
        JoinedAt = joined,
        LeftAt = left,
        Roles = roles,
        FirstSeenAt = joined,
        UpdatedAt = at,
    };

    [Fact]
    public async Task TheList_ShowsMembersInTheServer_NewestFirst_WithTheirRolesNamed()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var body = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members", cookie, Ct);

        Assert.Equal(["2", "1"], body.Members.Select(m => m.UserId));
        Assert.Equal(2, body.Total);
        Assert.Equal(2, body.Coverage.InServer);
        Assert.Equal(Guild, body.Coverage.GuildId);
        Assert.Equal(host.Clock.UtcNow.AddHours(-1), body.Coverage.ListedAt);

        var ada = body.Members[1];
        Assert.Equal("Ada the Brave", ada.DisplayName);
        Assert.Equal("Ada", ada.GlobalName);
        Assert.Equal(["Staff", "Member"], ada.Roles.Select(r => r.Name));
        Assert.Equal(0x3498DB, ada.Roles[1].Color);
    }

    [Fact]
    public async Task Filters_ForLeft_All_Role_AndSearch()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var left = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?state=left", cookie, Ct);
        Assert.Equal(["3"], left.Members.Select(m => m.UserId));
        Assert.NotNull(left.Members[0].LeftAt);

        var all = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?state=all", cookie, Ct);
        Assert.Equal(3, all.Total);

        var staff = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?role=202", cookie, Ct);
        Assert.Equal(["1"], staff.Members.Select(m => m.UserId));

        var brave = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?search=BRAVE", cookie, Ct);
        Assert.Equal(["1"], brave.Members.Select(m => m.UserId));

        // A % typed in a search is a percent sign, not "anything".
        var percent = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?state=all&search=%25", cookie, Ct);
        Assert.Equal(["3"], percent.Members.Select(m => m.UserId));

        var paged = await host.GetJsonAsync<DiscordMemberListResponse>("/api/discord/members?pageSize=1&page=2", cookie, Ct);
        Assert.Equal(["1"], paged.Members.Select(m => m.UserId));
        Assert.Equal(2, paged.Total);

        Assert.Equal(HttpStatusCode.BadRequest, (await host.GetAsync("/api/discord/members?state=gone", cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task OneMember_IsFoundById_WhetherInTheServerOrNot()
    {
        await using var host = await StartAsync(_db);
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var cy = await host.GetJsonAsync<DiscordMemberView>("/api/discord/members/3", cookie, Ct);
        Assert.Equal("cy_100%", cy.Username);
        Assert.NotNull(cy.LeftAt);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/api/discord/members/404", cookie, Ct)).StatusCode);

        // Only the server in settings.
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/api/discord/members/9", cookie, Ct)).StatusCode);
    }

    [Theory]
    [InlineData("/api/discord/members")]
    [InlineData("/api/discord/members/1")]
    public async Task BothNeedViewMembers(string path)
    {
        await using var host = await StartAsync(_db);

        var settings = await host.SignedInAsync(ModbotPermissions.ManageSettings | ModbotPermissions.ViewAuditLog, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(path, settings, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(path, Ct)).StatusCode);

        var members = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync(path, members, Ct)).StatusCode);
    }
}
