using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.AI.Chat;
using Modbot.Api.Tests.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// Dictate's two companion endpoints: whether the owner of a paired companion may use AI Chat, and
/// sending a dictated message as them. Written, not run (Docker needed).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CompanionChatTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SetChatAsync(bool aiOn, bool chatOn)
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.AiEnabled = aiOn;
        settings.AiChatEnabled = chatOn;
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// The send endpoint asks for the Chat page's own services, so the host needs them; none of these
    /// tests gets as far as a model call, so the client is a fake and the tool list is empty.
    /// </summary>
    private static Task<CompanionApiTestHost> StartHostAsync(PostgresFixture db)
        => CompanionApiTestHost.StartAsync(db, configure: services =>
        {
            services.AddModbotAi();
            services.AddScoped<IAiClients>(_ => new AutoModTests.FakeAi(_ => "ok"));
            services.AddSingleton(new ChatToolRegistry([]));
            services.AddSingleton<ChatLoop>();
        });

    private static async Task<bool> AvailableAsync(CompanionApiTestHost host, string? token)
    {
        using var request = host.WithToken(HttpMethod.Get, "/api/v1/companion/chat", token);
        var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("available").GetBoolean();
    }

    [Fact]
    public async Task WithoutAToken_BothEndpointsAreRefused()
    {
        await CompanionApiTestHost.ResetAsync(db, Ct);
        await using var host = await StartHostAsync(db);

        using var read = host.WithToken(HttpMethod.Get, "/api/v1/companion/chat", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(read, Ct)).StatusCode);

        using var send = host.WithToken(HttpMethod.Post, "/api/v1/companion/chat/messages", null);
        send.Content = JsonContent.Create(new { text = "hi" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(send, Ct)).StatusCode);
    }

    [Theory]
    [InlineData(true, true, ModbotPermissions.UseAiChat, true)]
    [InlineData(true, true, ModbotPermissions.Administrator, true)]
    [InlineData(true, true, ModbotPermissions.ViewMembers, false)]
    [InlineData(true, false, ModbotPermissions.UseAiChat, false)]
    [InlineData(false, true, ModbotPermissions.UseAiChat, false)]
    public async Task Available_IsChatOn_AndTheOwnerMayUseIt(bool aiOn, bool chatOn, ModbotPermissions owns, bool expected)
    {
        await CompanionApiTestHost.ResetAsync(db, Ct);
        await SetChatAsync(aiOn, chatOn);
        await using var host = await CompanionApiTestHost.StartAsync(db);

        var owner = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion | owns, Ct);
        var (token, _) = await host.PairDeviceToAsync(owner.Id, Ct);

        Assert.Equal(expected, await AvailableAsync(host, token));
    }

    [Fact]
    public async Task Send_WithoutUseAiChat_IsRefused_AndWithItButChatOff_IsTheChatPagesOwnConflict()
    {
        await CompanionApiTestHost.ResetAsync(db, Ct);
        await SetChatAsync(aiOn: false, chatOn: false);
        await using var host = await StartHostAsync(db);

        var without = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion, Ct);
        var (withoutToken, _) = await host.PairDeviceToAsync(without.Id, Ct);

        using var refused = host.WithToken(HttpMethod.Post, "/api/v1/companion/chat/messages", withoutToken);
        refused.Content = JsonContent.Create(new { text = "hi" });
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(refused, Ct)).StatusCode);

        var allowed = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion | ModbotPermissions.UseAiChat, Ct);
        var (allowedToken, _) = await host.PairDeviceToAsync(allowed.Id, Ct);

        using var off = host.WithToken(HttpMethod.Post, "/api/v1/companion/chat/messages", allowedToken);
        off.Content = JsonContent.Create(new { text = "hi" });
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.SendAsync(off, Ct)).StatusCode);
    }

    [Fact]
    public async Task Send_ToAnotherPersonsConversation_IsNotFound()
    {
        await CompanionApiTestHost.ResetAsync(db, Ct);
        await SetChatAsync(aiOn: true, chatOn: true);
        await using var host = await StartHostAsync(db);

        var owner = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion | ModbotPermissions.UseAiChat, Ct);
        var (token, _) = await host.PairDeviceToAsync(owner.Id, Ct);

        using var request = host.WithToken(HttpMethod.Post, "/api/v1/companion/chat/messages", token);
        request.Content = JsonContent.Create(new { conversationId = Guid.NewGuid(), text = "hi" });

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.SendAsync(request, Ct)).StatusCode);
    }
}
