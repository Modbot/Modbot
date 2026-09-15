using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Chat;

/// <summary>
/// The Chat page's API: who may use it, whose conversations are whose, and one reply end to end
/// against a scripted provider.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ChatTests
{
    private const string Endpoint = "https://llm.example.org/v1";

    private readonly PostgresFixture _db;

    public ChatTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Who may use it ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutUseAiChat_EveryChatEndpointIsRefused()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);

        var everythingElse = ModbotPermissions.ViewMembers | ModbotPermissions.ViewProfile | ModbotPermissions.ViewAnalytics
                             | ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewLiveRooms | ModbotPermissions.ManageSettings;
        var (_, cookie) = await host.SignedInAsync(everythingElse, Ct);

        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/chat", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, $"/api/chat/conversations/{id}", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/chat/conversations/{id}", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct)).StatusCode);
    }

    [Theory]
    [InlineData(ModbotPermissions.UseAiChat)]
    [InlineData(ModbotPermissions.Administrator)]
    public async Task UseAiChat_OrAdministrator_MayOpenIt_AndItIsOffUntilSwitchedOn(ModbotPermissions held)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(held, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/chat", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(response, Ct)).GetProperty("available").GetBoolean());

        var send = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.Conflict, send.StatusCode);
    }

    [Fact]
    public async Task ChatSettings_NeedManageSettings()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/settings/ai/chat", null, cookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/chat", Settings(), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task ChatSettings_StartOff_WithEveryReadToolOn_AndReadBackWhatWasSaved()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var fresh = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/settings/ai/chat", null, cookie, Ct), Ct);

        Assert.False(fresh.GetProperty("enabled").GetBoolean());
        Assert.Equal(8, fresh.GetProperty("maxToolCalls").GetInt32());
        Assert.Equal(2000, fresh.GetProperty("maxReplyTokens").GetInt32());
        Assert.Equal(120, fresh.GetProperty("timeLimitSeconds").GetInt32());

        var tools = fresh.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(12, tools.Count);
        Assert.All(tools, t => Assert.True(t.GetProperty("onlyReads").GetBoolean() && t.GetProperty("enabled").GetBoolean()));
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "list_live_rooms"
                                    && t.GetProperty("needs").EnumerateArray().Single().GetString() == "See live instances");

        var saved = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/chat",
            Settings(enabled: true, model: "small-model", instructions: "Answer in Spanish.", maxToolCalls: 3,
                tools: new Dictionary<string, bool> { ["search_audit_log"] = false }),
            cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var back = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/settings/ai/chat", null, cookie, Ct), Ct);
        Assert.True(back.GetProperty("enabled").GetBoolean());
        Assert.Equal("small-model", back.GetProperty("model").GetString());
        Assert.Equal("Answer in Spanish.", back.GetProperty("instructions").GetString());
        Assert.Equal(3, back.GetProperty("maxToolCalls").GetInt32());
        Assert.False(back.GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "search_audit_log").GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData(51, 2000, 120)]
    [InlineData(8, 100, 120)]
    [InlineData(8, 2000, 5)]
    public async Task ChatSettings_OutsideTheLimits_AreRefused(int toolCalls, int tokens, int seconds)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/chat",
            Settings(maxToolCalls: toolCalls, maxReplyTokens: tokens, timeLimitSeconds: seconds), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChatSettings_AToolThatDoesNotExist_IsRefused()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/chat",
            Settings(tools: new Dictionary<string, bool> { ["ban_person"] = true }), cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Whose conversations are whose ────────────────────────────────────────────────────────

    [Fact]
    public async Task AConversation_IsOnlyEverVisibleToItsOwner()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);

        var (owner, ownerCookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        var (_, otherCookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var id = await ConversationAsync(host, owner.Id, "Who is Gunner24?");

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Get, $"/api/chat/conversations/{id}", null, ownerCookie, Ct)).StatusCode);

        // Not even an administrator: a 404, the same as for a conversation that does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendJsonAsync(HttpMethod.Get, $"/api/chat/conversations/{id}", null, otherCookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/chat/conversations/{id}", null, otherCookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages",
            new { conversationId = id, text = "and now?" }, otherCookie, Ct)).StatusCode);

        var otherList = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/chat", null, otherCookie, Ct), Ct);
        Assert.Empty(otherList.GetProperty("conversations").EnumerateArray());

        var ownerList = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/chat", null, ownerCookie, Ct), Ct);
        Assert.Equal(id.ToString(), Assert.Single(ownerList.GetProperty("conversations").EnumerateArray()).GetProperty("id").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"/api/chat/conversations/{id}", null, ownerCookie, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendJsonAsync(HttpMethod.Get, $"/api/chat/conversations/{id}", null, ownerCookie, Ct)).StatusCode);
    }

    // ── A reply, end to end ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AQuestion_RunsATool_AndTheWholeExchangeIsStored()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var person = $"usr_{Guid.NewGuid():N}";
        var provider = new ScriptedProvider()
            .Then(Stream(ToolCall("call_1", "find_person", JsonSerializer.Serialize(new { query = "Gunner" }))))
            .Then(Stream(Text("Gunner24 is "), Text(person + ".")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedPersonAsync(host, person, "Gunner24");

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Who is Gunner?" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = ParseEvents(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("conversation", events[0].Name);
        Assert.Equal("Answered", events[^1].Data.GetProperty("outcome").GetString());
        Assert.Contains(events, e => e.Name == "tool" && e.Data.GetProperty("name").GetString() == "find_person");

        // What was offered: this person's tools, nothing needing a permission they lack.
        var offered = provider.ToolNames(0);
        Assert.Contains("find_person", offered);
        Assert.Contains("get_person_cases", offered);
        Assert.DoesNotContain("search_audit_log", offered);
        Assert.DoesNotContain("list_live_rooms", offered);
        Assert.DoesNotContain("search_members", offered);
        Assert.DoesNotContain("group_analytics", offered);

        // The tool's answer went back to the model, from the real database.
        Assert.Contains(person, provider.Bodies[1], StringComparison.Ordinal);

        var id = events[0].Data.GetProperty("id").GetString();
        var conversation = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/chat/conversations/{id}", null, cookie, Ct), Ct);

        var messages = conversation.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "tool", "assistant"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("find_person", messages[1].GetProperty("toolCalls")[0].GetProperty("name").GetString());
        Assert.True(messages[2].GetProperty("worked").GetBoolean());
        Assert.Equal(person, messages[2].GetProperty("references")[0].GetProperty("id").GetString());
        Assert.Equal($"Gunner24 is {person}.", messages[3].GetProperty("content").GetString());
    }

    /// <summary>
    /// The tool is not offered, and a model that asks for it anyway gets nothing: the person
    /// cannot see through Chat what the app would not show them.
    /// </summary>
    [Fact]
    public async Task AToolThePersonMayNotUse_DoesNotRun_EvenWhenTheModelAsksForIt()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var person = $"usr_{Guid.NewGuid():N}";
        var provider = new ScriptedProvider()
            .Then(Stream(ToolCall("call_1", "find_person", JsonSerializer.Serialize(new { query = person }))))
            .Then(Stream(Text("I can't look that up.")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedPersonAsync(host, person, "SecretName");

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewAnalytics, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Look them up" }, cookie, Ct);
        var events = ParseEvents(await response.Content.ReadAsStringAsync(Ct));

        Assert.DoesNotContain("find_person", provider.ToolNames(0));
        Assert.DoesNotContain(events, e => e.Name == "tool");
        Assert.DoesNotContain("SecretName", provider.Bodies[1], StringComparison.Ordinal);

        var tool = events.Where(e => e.Name == "message").Select(e => e.Data)
            .Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.False(tool.GetProperty("worked").GetBoolean());
    }

    [Fact]
    public async Task AFullConversation_TakesNoMoreMessages()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartWithProviderAsync(new ScriptedProvider());

        var (owner, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        var id = await ConversationAsync(host, owner.Id, "first", messages: AI.Chat.ChatSettingsRules.MaxMessagesPerConversation);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { conversationId = id, text = "more" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyMessage_IsRefused(string text)
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartWithProviderAsync(new ScriptedProvider());
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Pieces ───────────────────────────────────────────────────────────────────────────────

    private static object Settings(
        bool enabled = false,
        string? model = null,
        string? instructions = null,
        int maxToolCalls = 8,
        int maxReplyTokens = 2000,
        int timeLimitSeconds = 120,
        Dictionary<string, bool>? tools = null) =>
        new { enabled, model, instructions, maxToolCalls, maxReplyTokens, timeLimitSeconds, tools };

    /// <summary>A host whose AI requests all go to <paramref name="provider"/>, with AI and Chat switched on.</summary>
    private async Task<ApiTestHost> StartWithProviderAsync(ScriptedProvider provider)
    {
        var host = await ApiTestHost.StartAsync(_db, configure: services =>
            services.AddHttpClient(AiClients.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new Forwarding(provider)));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(Ct);

        settings.AiEnabled = true;
        settings.AiProvider = "custom";
        settings.AiEndpoint = Endpoint;
        settings.AiModel = "test-model";
        settings.AiChatEnabled = true;

        await db.SaveChangesAsync(Ct);
        return host;
    }

    private static async Task SeedPersonAsync(ApiTestHost host, string id, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.VRChatUsers.Add(new VRChatUser
        {
            UserId = id,
            DisplayName = name,
            RawProfile = "{}",
            FirstSeenAt = host.Clock.UtcNow.AddDays(-1),
            LastSeenAt = host.Clock.UtcNow,
        });

        await db.SaveChangesAsync(Ct);
    }

    private static async Task<Guid> ConversationAsync(ApiTestHost host, Guid owner, string title, int messages = 1)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var now = host.Clock.UtcNow;

        var conversation = new AiChatConversation { UserId = owner, Title = title, CreatedAt = now, UpdatedAt = now };
        for (var i = 0; i < messages; i++)
            conversation.Messages.Add(new AiChatMessage { Role = "user", Content = title, CreatedAt = now });

        db.AiChatConversations.Add(conversation);
        await db.SaveChangesAsync(Ct);
        return conversation.Id;
    }

    private sealed record SseEvent(string Name, JsonElement Data);

    private static List<SseEvent> ParseEvents(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block =>
            {
                var lines = block.Split('\n');
                var name = lines.First(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
                var data = lines.First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
                return new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone());
            })
            .ToList();

    private static string Stream(params object[] deltas) =>
        string.Concat(deltas.Select(d => "data: " + JsonSerializer.Serialize(new
        {
            id = "chatcmpl-1",
            @object = "chat.completion.chunk",
            created = 1_700_000_000,
            model = "test-model",
            choices = new[] { new { index = 0, delta = d, finish_reason = (string?)null } },
        }) + "\n\n")) + "data: [DONE]\n\n";

    private static object Text(string text) => new { role = "assistant", content = text };

    private static object ToolCall(string id, string name, string arguments) => new
    {
        role = "assistant",
        tool_calls = new[] { new { index = 0, id, type = "function", function = new { name, arguments } } },
    };

    private sealed class ScriptedProvider : HttpMessageHandler
    {
        private readonly Queue<string> _answers = new();

        public List<string> Bodies { get; } = [];

        public ScriptedProvider Then(string sse)
        {
            _answers.Enqueue(sse);
            return this;
        }

        public List<string?> ToolNames(int request)
        {
            using var body = JsonDocument.Parse(Bodies[request]);
            return body.RootElement.TryGetProperty("tools", out var tools)
                ? tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList()
                : [];
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

            if (!_answers.TryDequeue(out var sse))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    /// <summary>Hands requests to the shared script without letting the client factory dispose it.</summary>
    private sealed class Forwarding(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(inner, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _invoker.SendAsync(request, cancellationToken);
    }
}
