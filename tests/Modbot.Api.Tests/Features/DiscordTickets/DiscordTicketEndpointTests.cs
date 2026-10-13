using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.DiscordTickets;

/// <summary>
/// Settings → Discord → Tickets: adding, changing, ordering and removing ticket types within their
/// limits, a staff role that must be in the server, the transcript channel, and posting the panel,
/// which says why when there is nothing to post, the bot is offline or Discord refuses.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordTicketEndpointTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Guild = "9200";
    private const string Staff = "9201";
    private const string Gone = "9202";
    private const string Path = "/api/settings/discord-tickets";

    /// <summary>Stands in for the bot: remembers what it was asked to post and answers as told.</summary>
    private sealed class FakePanel(DiscordTicketPanelOutcome outcome) : IDiscordTicketPanel
    {
        public List<(string ChannelId, string Text, IReadOnlyList<DiscordTicketPanelButton> Buttons)> Posted { get; } = [];

        public Task<DiscordTicketPanelOutcome> PostAsync(
            string channelId, string text, IReadOnlyList<DiscordTicketPanelButton> buttons, CancellationToken ct = default)
        {
            Posted.Add((channelId, text, buttons));
            return Task.FromResult(outcome);
        }
    }

    private async Task<ApiTestHost> StartAsync(IDiscordTicketPanel? panel = null)
    {
        var host = await ApiTestHost.StartAsync(
            db,
            configure: services =>
            {
                if (panel is not null)
                    services.AddSingleton(panel);
            });

        await using var context = db.NewContext();

        await context.DiscordTickets.ExecuteDeleteAsync(Ct);
        await context.DiscordTicketTypes.ExecuteDeleteAsync(Ct);
        await context.DiscordRoles.Where(r => r.GuildId == Guild).ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.DiscordGuildId = Guild;
        settings.DiscordTicketTranscriptChannelId = null;

        var now = host.Clock.UtcNow;

        context.DiscordRoles.AddRange(
            new DiscordRole { RoleId = Staff, GuildId = Guild, Name = "Staff", FirstSeenAt = now, UpdatedAt = now },
            new DiscordRole { RoleId = Gone, GuildId = Guild, Name = "Old", FirstSeenAt = now, UpdatedAt = now, RemovedAt = now },
            new DiscordRole { RoleId = Guild, GuildId = Guild, Name = "@everyone", Everyone = true, FirstSeenAt = now, UpdatedAt = now });

        await context.SaveChangesAsync(Ct);

        return host;
    }

    private static object Type(string name = "Help", string? staffRoleId = Staff, string opening = "Tell us what you need.", bool privateImages = false)
        => new { name, staffRoleId, openingMessage = opening, holdsPrivateImages = privateImages };

    private static Task<HttpResponseMessage> AddAsync(ApiTestHost host, string cookie, object body)
        => host.SendJsonAsync(HttpMethod.Post, $"{Path}/types", body, cookie, Ct);

    private static async Task<string> ErrorOf(HttpResponseMessage response)
        => (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString() ?? string.Empty;

    private static async Task<JsonElement> TypesOf(HttpResponseMessage response)
        => (await ApiTestHost.BodyOf(response, Ct)).GetProperty("types");

    [Fact]
    public async Task ManagingTicketsNeedsTheSettingsPermission()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(host, cookie, Type())).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, $"{Path}/panel", new { channelId = "1", text = "Open one" }, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task ATypeIsAddedWithItsStaffRoleNameAndOrder()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await AddAsync(host, cookie, Type("Help"));
        var response = await AddAsync(host, cookie, Type("Appeal", privateImages: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var types = await TypesOf(response);
        Assert.Equal(2, types.GetArrayLength());
        Assert.Equal("Help", types[0].GetProperty("name").GetString());
        Assert.Equal("Staff", types[0].GetProperty("staffRoleName").GetString());
        Assert.Equal("Appeal", types[1].GetProperty("name").GetString());
        Assert.True(types[1].GetProperty("holdsPrivateImages").GetBoolean());
    }

    [Fact]
    public async Task AddingTheSettingIsRecordedInTheAuditLog()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await AddAsync(host, cookie, Type("Help"));

        var facts = await host.FactsAsync(FactType.SettingsChanged, "settings", Ct);

        Assert.Contains(facts.Select(ApiTestHost.DataOf), data => data.GetProperty("setting").GetString() == "discordTickets");
    }

    [Theory]
    [InlineData("", "Tell us.")]
    [InlineData("   ", "Tell us.")]
    [InlineData("Help", "")]
    public async Task ATypeNeedsANameAndAnOpeningMessage(string name, string opening)
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await AddAsync(host, cookie, Type(name, opening: opening));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ALongNameOrOpeningMessageIsRefused()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await AddAsync(host, cookie, Type(new string('n', DiscordTicketLimits.MostNameLength + 1)))).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await AddAsync(host, cookie, Type(opening: new string('o', DiscordTicketLimits.MostOpeningLength + 1)))).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await AddAsync(host, cookie, Type(new string('n', DiscordTicketLimits.MostNameLength), opening: new string('o', DiscordTicketLimits.MostOpeningLength)))).StatusCode);
    }

    [Fact]
    public async Task AtMostFiveTypesCanBeAdded()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        for (var i = 0; i < DiscordTicketLimits.MostTypes; i++)
            Assert.Equal(HttpStatusCode.OK, (await AddAsync(host, cookie, Type($"Type {i}"))).StatusCode);

        var response = await AddAsync(host, cookie, Type("One too many"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("At most 5", await ErrorOf(response), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("9999")]
    [InlineData(Gone)]
    [InlineData(Guild)]
    public async Task AStaffRoleMustBeInTheServer(string roleId)
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(host, cookie, Type(staffRoleId: roleId))).StatusCode);
    }

    [Fact]
    public async Task ATypeWithNoStaffRoleIsAllowed()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await AddAsync(host, cookie, Type(staffRoleId: ""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await TypesOf(response))[0].GetProperty("staffRoleId").ValueKind);
    }

    [Fact]
    public async Task ATypeIsChanged()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var id = (await TypesOf(await AddAsync(host, cookie, Type("Help"))))[0].GetProperty("id").GetGuid();

        var response = await host.SendJsonAsync(
            HttpMethod.Put, $"{Path}/types/{id}", Type("Support", staffRoleId: "", opening: "Hello", privateImages: true), cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var type = (await TypesOf(response))[0];
        Assert.Equal("Support", type.GetProperty("name").GetString());
        Assert.Equal("Hello", type.GetProperty("openingMessage").GetString());
        Assert.True(type.GetProperty("holdsPrivateImages").GetBoolean());
    }

    [Fact]
    public async Task ChangingAMissingTypeIsNotFound()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/types/{Guid.NewGuid()}", Type(), cookie, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TypesAreReorderedAndEveryTypeMustBeSentOnce()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await AddAsync(host, cookie, Type("First"));
        var added = await TypesOf(await AddAsync(host, cookie, Type("Second")));
        var first = added[0].GetProperty("id").GetGuid();
        var second = added[1].GetProperty("id").GetGuid();

        var short1 = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/types/order", new { ids = new[] { first } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, short1.StatusCode);

        var twice = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/types/order", new { ids = new[] { first, first } }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);

        var swapped = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/types/order", new { ids = new[] { second, first } }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        Assert.Equal("Second", (await TypesOf(swapped))[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task RemovingATypeKeepsItsTickets()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var id = (await TypesOf(await AddAsync(host, cookie, Type("Help"))))[0].GetProperty("id").GetGuid();

        await using (var context = db.NewContext())
        {
            context.DiscordTickets.Add(new DiscordTicket
            {
                TypeId = id,
                TypeName = "Help",
                DiscordUserId = "9300",
                ThreadId = "9301",
                ChannelId = "9302",
                OpenedAt = host.Clock.UtcNow,
            });
            await context.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/types/{id}", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await TypesOf(response)).GetArrayLength());

        await using var check = db.NewContext();
        Assert.Equal(1, await check.DiscordTickets.CountAsync(t => t.TypeId == id, Ct));
    }

    [Fact]
    public async Task OpenTicketsAreCountedForEachType()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var id = (await TypesOf(await AddAsync(host, cookie, Type("Help"))))[0].GetProperty("id").GetGuid();

        await using (var context = db.NewContext())
        {
            context.DiscordTickets.AddRange(
                new DiscordTicket { TypeId = id, TypeName = "Help", DiscordUserId = "9310", ThreadId = "9311", ChannelId = "9302", OpenedAt = host.Clock.UtcNow },
                new DiscordTicket
                {
                    TypeId = id,
                    TypeName = "Help",
                    DiscordUserId = "9312",
                    ThreadId = "9313",
                    ChannelId = "9302",
                    State = DiscordTicketStates.Closed,
                    OpenedAt = host.Clock.UtcNow,
                });
            await context.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Get, Path, null, cookie, Ct);

        Assert.Equal(1, (await TypesOf(response))[0].GetProperty("openTickets").GetInt32());
    }

    [Fact]
    public async Task TheTranscriptChannelIsSavedAndCleared()
    {
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var saved = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/transcript-channel", new { channelId = " 4242 " }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("4242", (await ApiTestHost.BodyOf(saved, Ct)).GetProperty("transcriptChannelId").GetString());

        var cleared = await host.SendJsonAsync(HttpMethod.Put, $"{Path}/transcript-channel", new { channelId = "" }, cookie, Ct);

        Assert.Equal(JsonValueKind.Null, (await ApiTestHost.BodyOf(cleared, Ct)).GetProperty("transcriptChannelId").ValueKind);
    }

    [Fact]
    public async Task ThePanelNeedsAChannelTextAndAType()
    {
        var panel = new FakePanel(DiscordTicketPanelOutcome.Ok);
        await using var host = await StartAsync(panel);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.SendJsonAsync(HttpMethod.Post, $"{Path}/panel", new { channelId = "", text = "Open one" }, cookie, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.SendJsonAsync(HttpMethod.Post, $"{Path}/panel", new { channelId = "1", text = " " }, cookie, Ct)).StatusCode);

        var noTypes = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/panel", new { channelId = "1", text = "Open one" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, noTypes.StatusCode);
        Assert.Equal("Add a ticket type first.", await ErrorOf(noTypes));
        Assert.Empty(panel.Posted);
    }

    [Fact]
    public async Task ThePanelIsPostedWithOneButtonForEachTypeInOrder()
    {
        var panel = new FakePanel(DiscordTicketPanelOutcome.Ok);
        await using var host = await StartAsync(panel);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await AddAsync(host, cookie, Type("Help"));
        await AddAsync(host, cookie, Type("Appeal"));

        var response = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/panel", new { channelId = "1234", text = "Open one" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var posted = Assert.Single(panel.Posted);
        Assert.Equal("1234", posted.ChannelId);
        Assert.Equal("Open one", posted.Text);
        Assert.Equal(["Help", "Appeal"], posted.Buttons.Select(b => b.Label));
    }

    [Fact]
    public async Task ThePanelSaysWhenTheBotIsOffline()
    {
        // With no bot in the process the default panel answers that the bot is not connected.
        await using var host = await StartAsync();
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await AddAsync(host, cookie, Type("Help"));

        var response = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/panel", new { channelId = "1", text = "Open one" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ThePanelSaysWhatDiscordRefused()
    {
        var panel = new FakePanel(DiscordTicketPanelOutcome.Failed("Missing Permissions"));
        await using var host = await StartAsync(panel);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await AddAsync(host, cookie, Type("Help"));

        var response = await host.SendJsonAsync(HttpMethod.Post, $"{Path}/panel", new { channelId = "1", text = "Open one" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Missing Permissions", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}
