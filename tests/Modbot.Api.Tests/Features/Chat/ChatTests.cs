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

    // ── Usage and spend limits ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryProviderCall_IsRecordedWithItsTokensAndCost()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var provider = new ScriptedProvider()
            .Then(StreamWithUsage([ToolCall("call_1", "find_person", """{"query":"x"}""")], Usage(2_000_000, 1_000_000, 100_000)))
            .Then(StreamWithUsage([Text("Nobody.")], Usage(3_000_000, 0, 1_000_000)));

        await using var host = await StartWithProviderAsync(provider);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile, Ct);

        // $1 per million in, $0.10 per million cached, $4 per million out.
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var priced = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/prices", new
        {
            prices = new[] { new { model = "test-model", inputPerMillion = 1m, cachedInputPerMillion = 0.1m, outputPerMillion = 4m } },
        }, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Who is x?" }, cookie, Ct);
        await response.Content.ReadAsStringAsync(Ct);

        // The stream asked for usage, or there would be none to record.
        Assert.Contains("\"include_usage\":true", provider.Bodies[0], StringComparison.Ordinal);

        await using var context = _db.NewContext();
        var rows = await context.AiUsage.OrderBy(u => u.Id).ToListAsync(Ct);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(("chat", user.Id, "test-model", "custom"), (r.Feature, r.UserId!.Value, r.Model, r.Provider)));
        Assert.Equal((2_000_000, 1_000_000, 100_000), (rows[0].InputTokens, rows[0].CachedInputTokens, rows[0].OutputTokens));
        Assert.Equal((3_000_000, 0, 1_000_000), (rows[1].InputTokens, rows[1].CachedInputTokens, rows[1].OutputTokens));

        // 1M uncached at $1 + 1M cached at $0.10 + 0.1M out at $4 = $1.50; then 3M in + 1M out = $7.
        var limits = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/settings/ai/limits", null, admin, Ct), Ct);
        Assert.Equal(8.5m, limits.GetProperty("today").GetProperty("cost").GetDecimal());
        Assert.Equal(5_000_000, limits.GetProperty("month").GetProperty("inputTokens").GetInt64());
        Assert.Equal(1_000_000, limits.GetProperty("month").GetProperty("cachedInputTokens").GetInt64());
    }

    [Fact]
    public async Task AUsersOwnLimit_StopsNewTurns_WithoutCallingTheProvider()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);

        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        await SpendAsync(host, user.Id, 2m);
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.User, UserId = user.Id, PerDay = 2m });

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("Your daily AI spend limit is reached.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        Assert.Empty(provider.Bodies);
    }

    [Fact]
    public async Task ChatsOwnFeatureLimit_IsHonouredToo()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);

        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.UseAiPastLimits, Ct);
        await SpendAsync(host, user.Id, 1m);

        await using (var context = _db.NewContext())
        {
            context.AiFeatureLimits.Add(new AiFeatureLimit { Feature = "chat", MonthlyTokenLimit = 1000 });
            await context.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(provider.Bodies);
    }

    [Fact]
    public async Task UnderEveryLimit_ATurnGoesAhead()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider().Then(Stream(Text("Hello.")));
        await using var host = await StartWithProviderAsync(provider);

        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        await SpendAsync(host, user.Id, 1.99m);
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.User, UserId = user.Id, PerDay = 2m });
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerMonth = 100m });

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The tightest limit that applies wins, whichever kind it is: here a role's daily limit is
    /// reached while the person's own monthly one and everyone's are not. Another member's spend
    /// does not count against this person's role limit.
    /// </summary>
    [Fact]
    public async Task ARolesLimit_AppliesToEachPersonHoldingIt_AndTheTightestLimitIsTheOneNamed()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartWithProviderAsync(new ScriptedProvider().Then(Stream(Text("Hi."))));

        var held = ModbotPermissions.UseAiChat | ModbotPermissions.ViewMembers;
        var (user, cookie) = await host.SignedInAsync(held, Ct);
        var (other, otherCookie) = await host.SignedInAsync(held, Ct);

        Guid role;
        await using (var context = _db.NewContext())
            role = await TestAccounts.RoleForAsync(context, held, Ct);

        await SpendAsync(host, user.Id, 3m);
        await SpendAsync(host, other.Id, 1m);
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.Role, RoleId = role, PerDay = 3m });
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.User, UserId = user.Id, PerMonth = 100m });
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerDay = 1000m });

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal($"The test:{(long)held} role's daily AI spend limit is reached.",
            (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());

        var others = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, otherCookie, Ct);
        Assert.Equal(HttpStatusCode.OK, others.StatusCode);
    }

    [Fact]
    public async Task UseAiPastLimits_GoesPastUserAndRoleLimits_ButNotTheLimitForEveryone()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider().Then(Stream(Text("Hello.")));
        await using var host = await StartWithProviderAsync(provider);

        var held = ModbotPermissions.UseAiChat | ModbotPermissions.UseAiPastLimits;
        var (user, cookie) = await host.SignedInAsync(held, Ct);

        Guid role;
        await using (var context = _db.NewContext())
            role = await TestAccounts.RoleForAsync(context, held, Ct);

        await SpendAsync(host, user.Id, 10m);
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.User, UserId = user.Id, PerDay = 1m });
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.Role, RoleId = role, PerMonth = 1m });

        var allowed = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await allowed.Content.ReadAsStringAsync(Ct);

        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.Everyone, PerMonth = 10m });

        var refused = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "again" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("This Modbot's monthly AI spend limit is reached.",
            (await ApiTestHost.BodyOf(refused, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task YesterdaysSpend_DoesNotCountTowardsToday_ButDoesTowardsTheMonth()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartWithProviderAsync(new ScriptedProvider());
        host.Clock.UtcNow = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        await SpendAsync(host, user.Id, 5m, host.Clock.UtcNow.AddDays(-1));
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.User, UserId = user.Id, PerDay = 5m, PerMonth = 5m });

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("Your monthly AI spend limit is reached.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task LimitsAndPrices_NeedManageSettings_AndReadBackWhatWasSaved()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartWithProviderAsync(new ScriptedProvider());

        var (_, chatter) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.UseAiPastLimits, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/settings/ai/limits", null, chatter, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/limits", new { limits = Array.Empty<object>() }, chatter, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/prices", new { prices = Array.Empty<object>() }, chatter, Ct)).StatusCode);

        var (admin, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var saved = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/limits", new
        {
            limits = new object[]
            {
                new { appliesTo = "everyone", perMonth = 50m },
                new { appliesTo = "user", userId = admin.Id, perDay = 1.25m },
            },
        }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var body = await ApiTestHost.BodyOf(saved, Ct);
        var limits = body.GetProperty("limits").EnumerateArray().ToList();
        Assert.Equal(["everyone", "user"], limits.Select(l => l.GetProperty("appliesTo").GetString()));
        Assert.Equal(50m, limits[0].GetProperty("perMonth").GetDecimal());
        Assert.Equal(admin.Username, limits[1].GetProperty("name").GetString());
        Assert.Contains("test-model", body.GetProperty("modelsUsed").EnumerateArray().Select(m => m.GetString()));

        var twice = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/limits", new
        {
            limits = new object[] { new { appliesTo = "everyone", perDay = 1m }, new { appliesTo = "everyone", perMonth = 2m } },
        }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);

        var negative = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/prices", new
        {
            prices = new[] { new { model = "m", inputPerMillion = -1m, outputPerMillion = 1m } },
        }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
    }

    // ── Pieces ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Usage worth <paramref name="cost"/> dollars: input tokens of a model priced at $1 per million.
    /// </summary>
    private static async Task SpendAsync(ApiTestHost host, Guid userId, decimal cost, DateTimeOffset? at = null)
    {
        const string model = "spend-model";

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        if (!await db.AiModelPrices.AnyAsync(p => p.Model == model, Ct))
            db.AiModelPrices.Add(new AiModelPrice { Model = model, InputPerMillion = 1m, OutputPerMillion = 1m, UpdatedAt = host.Clock.UtcNow });

        db.AiUsage.Add(new AiUsage
        {
            At = at ?? host.Clock.UtcNow,
            Feature = "chat",
            UserId = userId,
            Model = model,
            InputTokens = (int)(cost * 1_000_000m),
        });

        await db.SaveChangesAsync(Ct);
    }

    private static async Task LimitAsync(ApiTestHost host, AiSpendLimit limit)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        limit.UpdatedAt = host.Clock.UtcNow;
        db.AiSpendLimits.Add(limit);
        await db.SaveChangesAsync(Ct);
    }

    private static string StreamWithUsage(object[] deltas, object usage) =>
        Stream(deltas).Replace("data: [DONE]", "data: " + JsonSerializer.Serialize(usage) + "\n\ndata: [DONE]", StringComparison.Ordinal);

    private static object Usage(int input, int cached, int output) => new
    {
        id = "chatcmpl-1",
        @object = "chat.completion.chunk",
        created = 1_700_000_000,
        model = "test-model",
        choices = Array.Empty<object>(),
        usage = new
        {
            prompt_tokens = input,
            completion_tokens = output,
            total_tokens = input + output,
            prompt_tokens_details = new { cached_tokens = cached },
        },
    };

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

        // Roles outlive ResetDeploymentAsync, and a role limit with them; usage and prices too.
        await db.AiSpendLimits.ExecuteDeleteAsync(Ct);
        await db.AiUsage.ExecuteDeleteAsync(Ct);
        await db.AiModelPrices.ExecuteDeleteAsync(Ct);
        await db.AiFeatureLimits.ExecuteDeleteAsync(Ct);

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
