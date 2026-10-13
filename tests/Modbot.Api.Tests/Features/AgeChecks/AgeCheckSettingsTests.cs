using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using VRChat.API.Model;
using SettingsRow = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Tests.Features.AgeChecks;

/// <summary>
/// Settings → Discord → 18+ check: saved only by somebody who can change settings; a role that
/// holds a dangerous permission cannot be given; a role cannot be in both lists; the Linked and 18+
/// roles cannot be taken away; the group role must be in the group and the category a category.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AgeCheckSettingsTests
{
    private const string Guild = "700";
    private const string Party = "811";
    private const string Waiting = "812";
    private const string Admin = "813";
    private const string LinkedRole = "814";
    private const string Category = "900";
    private const string TextChannel = "901";
    private const string Url = "/api/settings/age-check";

    private const long Community = (1L << 10) | (1L << 11);
    private const long Administrator = 1L << 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public AgeCheckSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordRole Role(string id, string name, long permissions, DateTimeOffset at) => new()
    {
        RoleId = id,
        GuildId = Guild,
        Name = name,
        Permissions = permissions,
        BotCanAssign = true,
        FirstSeenAt = at,
        UpdatedAt = at,
    };

    private async Task<(ReadSurfaceTestHost Host, string Cookie)> StartAsync()
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(Ct);
            var at = host.Clock.UtcNow;

            settings.DiscordGuildId = Guild;
            settings.DiscordLinkedRoleId = LinkedRole;
            settings.GroupInfoSnapshot = GroupInfoSnapshot.From(new Group
            {
                Id = "grp_1",
                Name = "Test Group",
                Roles = [new GroupRole { Id = "grol_adult", Name = "Adult", Order = 1, Permissions = [] }],
            }).ToJson();

            db.DiscordRoles.AddRange(
                Role(Party, "Adult", Community, at),
                Role(Waiting, "Waiting", Community, at),
                Role(Admin, "Boss", Community | Administrator, at),
                Role(LinkedRole, "Linked", Community, at));

            db.DiscordChannels.AddRange(
                new DiscordChannel { GuildId = Guild, ChannelId = Category, Name = "Tickets", Type = DiscordChannelTypes.Category, FirstSeenAt = at, UpdatedAt = at },
                new DiscordChannel { GuildId = Guild, ChannelId = TextChannel, Name = "general", Type = DiscordChannelTypes.Text, FirstSeenAt = at, UpdatedAt = at });

            await db.SaveChangesAsync(Ct);
        }

        return (host, await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct));
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("error").GetString() ?? string.Empty;
    }

    private static async Task<SettingsRow> StoredAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ModbotContext>().GetSettingsAsync(Ct);
    }

    [Fact]
    public async Task NothingChosen_IsEmpty_AndTheTicketIsKept_AndTheGroupsRolesAreListed()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        var view = await host.GetJsonAsync<AgeCheckSettingsResponse>(Url, cookie, Ct);

        Assert.Empty(view.GiveRoleIds);
        Assert.Empty(view.RemoveRoleIds);
        Assert.Null(view.GroupRoleId);
        Assert.False(view.DeleteTicket);
        Assert.Null(view.TicketCategoryId);
        Assert.Equal("Adult", Assert.Single(view.GroupRoles).Name);
    }

    [Fact]
    public async Task EverythingChosen_IsSaved_AndReadBack()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        var saved = await ReadAsync<AgeCheckSettingsResponse>(await host.PutJsonAsync(
            Url,
            new { giveRoleIds = new[] { Party }, removeRoleIds = new[] { Waiting }, groupRoleId = "grol_adult", deleteTicket = true, ticketCategoryId = Category },
            cookie,
            Ct));

        Assert.Equal([Party], saved.GiveRoleIds);
        Assert.Equal([Waiting], saved.RemoveRoleIds);
        Assert.Equal("grol_adult", saved.GroupRoleId);
        Assert.True(saved.DeleteTicket);
        Assert.Equal(Category, saved.TicketCategoryId);

        var stored = await StoredAsync(host);
        Assert.Equal([Party], stored.AgeCheckGiveRoleIds);
        Assert.True(stored.AgeCheckDeleteTicket);
    }

    [Fact]
    public async Task ADangerousRole_CannotBeGiven()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        var refused = await host.PutJsonAsync(Url, new { giveRoleIds = new[] { Admin } }, cookie, Ct);

        Assert.Contains("Boss", await ErrorAsync(refused), StringComparison.Ordinal);
        Assert.Null((await StoredAsync(host)).AgeCheckGiveRoleIds);
    }

    [Fact]
    public async Task ARoleInBothLists_IsRefused()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        var refused = await host.PutJsonAsync(
            Url, new { giveRoleIds = new[] { Party }, removeRoleIds = new[] { Party } }, cookie, Ct);

        Assert.Contains("both lists", await ErrorAsync(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLinkedRole_CannotBeTakenAway()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        var refused = await host.PutJsonAsync(Url, new { removeRoleIds = new[] { LinkedRole } }, cookie, Ct);

        Assert.Contains("Linked", await ErrorAsync(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupRoleThatIsNotInTheGroup_AndAChannelThatIsNotACategory_AreRefused()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        Assert.Contains(
            "VRChat group",
            await ErrorAsync(await host.PutJsonAsync(Url, new { groupRoleId = "grol_nope" }, cookie, Ct)),
            StringComparison.Ordinal);

        Assert.Contains(
            "category",
            await ErrorAsync(await host.PutJsonAsync(Url, new { ticketCategoryId = TextChannel }, cookie, Ct)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task APartThatIsLeftOut_KeepsWhatIsStored_AndAnEmptyOneClears()
    {
        var (host, cookie) = await StartAsync();
        await using var _ = host;

        await ReadAsync<AgeCheckSettingsResponse>(await host.PutJsonAsync(
            Url, new { giveRoleIds = new[] { Party }, deleteTicket = true, ticketCategoryId = Category }, cookie, Ct));

        var kept = await ReadAsync<AgeCheckSettingsResponse>(await host.PutJsonAsync(Url, new { deleteTicket = false }, cookie, Ct));
        Assert.Equal([Party], kept.GiveRoleIds);
        Assert.Equal(Category, kept.TicketCategoryId);
        Assert.False(kept.DeleteTicket);

        var cleared = await ReadAsync<AgeCheckSettingsResponse>(
            await host.PutJsonAsync(Url, new { giveRoleIds = Array.Empty<string>(), ticketCategoryId = "" }, cookie, Ct));
        Assert.Empty(cleared.GiveRoleIds);
        Assert.Null(cleared.TicketCategoryId);
    }

    [Fact]
    public async Task OnlyWhoCanManageSettings_MayReadOrSaveIt()
    {
        var (host, _) = await StartAsync();
        await using var _ = host;

        var other = await host.SignedInAsync(ModbotPermissions.EditAgeVerification, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Url, other, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync(Url, new { deleteTicket = true }, other, Ct)).StatusCode);
    }
}
