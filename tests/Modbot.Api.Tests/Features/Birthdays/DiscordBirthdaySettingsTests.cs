using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Birthdays;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using SettingsRow = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Tests.Features.Birthdays;

/// <summary>
/// Settings → Discord → Birthdays: saved only by somebody who can change settings; the channel is
/// needed to turn it on; the hour, time zone and text are checked; a role that holds a dangerous
/// permission, belongs to a bot, is @everyone, is unread or is missing is refused; and turning it on or
/// moving the hour never posts for a time already past.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordBirthdaySettingsTests
{
    private const string Guild = "700";
    private const string Channel = "900";
    private const string Party = "811";
    private const string Admin = "812";
    private const string Bot = "813";
    private const string Unread = "814";
    private const string Url = "/api/settings/discord-birthdays";

    private const long Community = (1L << 10) | (1L << 11);
    private const long Administrator = 1L << 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public DiscordBirthdaySettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ReadSurfaceTestHost Host, string Cookie)> StartAsync(PostgresFixture db)
    {
        var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        using (var scope = host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await context.GetSettingsAsync(Ct);
            settings.DiscordGuildId = Guild;

            var at = host.Clock.UtcNow;

            context.DiscordRoles.AddRange(
                Role(Party, "Birthday", Community, at),
                Role(Admin, "Boss", Community | Administrator, at),
                Role(Bot, "Some bot", Community, at, managed: true),
                Role(Unread, "New", null, at),
                Role(Guild, "@everyone", 0, at, everyone: true));

            await context.SaveChangesAsync(Ct);
        }

        return (host, await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct));
    }

    private static DiscordRole Role(string id, string name, long? permissions, DateTimeOffset at, bool managed = false, bool everyone = false) => new()
    {
        RoleId = id,
        GuildId = Guild,
        Name = name,
        Permissions = permissions,
        Managed = managed,
        Everyone = everyone,
        BotCanAssign = !managed && !everyone,
        FirstSeenAt = at,
        UpdatedAt = at,
    };

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
    public async Task NothingChosen_IsOff_AtNineInTheMorning_WithTheBuiltInText()
    {
        var (host, cookie) = await StartAsync(_db);
        await using var _ = host;

        var view = await host.GetJsonAsync<DiscordBirthdaySettingsResponse>(Url, cookie, Ct);

        Assert.False(view.On);
        Assert.Null(view.ChannelId);
        Assert.Equal(9, view.Hour);
        Assert.Null(view.TimeZone);
        Assert.Equal(BirthdayRules.DefaultMessage, view.Message);
        Assert.Null(view.RoleId);
        Assert.Null(view.Problem);
    }

    [Fact]
    public async Task TurningItOn_NeedsAChannel()
    {
        var (host, cookie) = await StartAsync(_db);
        await using var _ = host;

        var refused = await host.PutJsonAsync(Url, new { on = true }, cookie, Ct);
        Assert.Contains("channel", await ErrorAsync(refused), StringComparison.OrdinalIgnoreCase);
        Assert.False((await StoredAsync(host)).BirthdaysOn);

        var saved = await ReadAsync<DiscordBirthdaySettingsResponse>(
            await host.PutJsonAsync(Url, new { on = true, channelId = Channel }, cookie, Ct));
        Assert.True(saved.On);
        Assert.Equal(Channel, saved.ChannelId);
    }

    [Fact]
    public async Task EverythingWrong_IsListedAtOnce()
    {
        var (host, cookie) = await StartAsync(_db);
        await using var _ = host;

        var response = await host.PutJsonAsync(
            Url,
            new { on = true, hour = 24, timeZone = "Mars/Olympus", message = new string('x', BirthdayRules.MaxMessageLength + 1) },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problems = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("problems").GetArrayLength();
        Assert.Equal(4, problems);
    }

    [Fact]
    public async Task ARole_WithADangerousPermission_ABotOwnedRole_Everyone_AnUnreadRole_AndAMissingRole_AreRefused()
    {
        var (host, cookie) = await StartAsync(_db);
        await using var _ = host;

        foreach (var role in new[] { Admin, Bot, Guild, Unread, "99999" })
        {
            var response = await host.PutJsonAsync(Url, new { roleId = role }, cookie, Ct);
            Assert.False(string.IsNullOrWhiteSpace(await ErrorAsync(response)), role);
        }

        var dangerous = await ErrorAsync(await host.PutJsonAsync(Url, new { roleId = Admin }, cookie, Ct));
        Assert.Contains("Boss", dangerous);

        Assert.Null((await StoredAsync(host)).BirthdayRoleId);
    }

    [Fact]
    public async Task AnOrdinaryRole_IsSaved_AndEmptyTakesItOff()
    {
        var (host, cookie) = await StartAsync(_db);
        await using var _ = host;

        var saved = await ReadAsync<DiscordBirthdaySettingsResponse>(await host.PutJsonAsync(Url, new { roleId = Party }, cookie, Ct));
        Assert.Equal(Party, saved.RoleId);

        var cleared = await ReadAsync<DiscordBirthdaySettingsResponse>(await host.PutJsonAsync(Url, new { roleId = "" }, cookie, Ct));
        Assert.Null(cleared.RoleId);
    }

    [Fact]
    public async Task TheText_IsKept_AndEmptyOrTheBuiltInTextStoresNothing()
    {
        var (host, cookie) = await StartAsync(_db);
        await using var _ = host;

        var saved = await ReadAsync<DiscordBirthdaySettingsResponse>(
            await host.PutJsonAsync(Url, new { message = "Cake for {members}" }, cookie, Ct));
        Assert.Equal("Cake for {members}", saved.Message);

        var reset = await ReadAsync<DiscordBirthdaySettingsResponse>(await host.PutJsonAsync(Url, new { message = "" }, cookie, Ct));
        Assert.Equal(BirthdayRules.DefaultMessage, reset.Message);
        Assert.Null((await StoredAsync(host)).BirthdayMessage);
    }

    [Fact]
    public async Task TurningItOn_NeverPostsForATimeAlreadyPast_AndMovingTheHourDoesNotEither()
    {
        var (host, cookie) = await StartAsync(_db);
        await using var _ = host;

        await ReadAsync<DiscordBirthdaySettingsResponse>(await host.PutJsonAsync(Url, new { on = true, channelId = Channel }, cookie, Ct));

        var stored = await StoredAsync(host);
        var marked = Assert.IsType<DateTimeOffset>(stored.BirthdayHandledThrough);
        Assert.Equal(BirthdayPass.LatestMoment(stored, host.Clock.UtcNow), marked);
        Assert.True(marked <= host.Clock.UtcNow);

        // The hour moved to one that already passed today: that time is dealt with too.
        var earlier = Math.Max(0, host.Clock.UtcNow.Hour - 1);
        await ReadAsync<DiscordBirthdaySettingsResponse>(await host.PutJsonAsync(Url, new { hour = earlier, timeZone = "" }, cookie, Ct));

        var moved = await StoredAsync(host);
        Assert.Equal(BirthdayPass.LatestMoment(moved, host.Clock.UtcNow), moved.BirthdayHandledThrough);
    }

    [Fact]
    public async Task SomebodyWithoutTheSettingsPermission_IsTurnedAway()
    {
        var (host, _) = await StartAsync(_db);
        await using var _ = host;
        var viewer = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync(Url, viewer, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.PutJsonAsync(Url, new { on = false }, viewer, Ct)).StatusCode);
    }
}
