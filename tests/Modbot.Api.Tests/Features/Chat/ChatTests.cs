using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.Analytics.Messages;
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
                             | ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewLiveInstances | ModbotPermissions.ManageSettings;
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
        Assert.Equal(23, tools.Count);
        Assert.All(tools, t => Assert.True(t.GetProperty("onlyReads").GetBoolean() && t.GetProperty("enabled").GetBoolean()));
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "list_live_instances"
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

        // A name no other test uses: the vrchat_user table is shared by the assembly, and
        // VRChatLinkTests records a "Gunner24" of its own, which find_person would also return.
        var name = $"Gunner{Guid.NewGuid():N}";
        var provider = new ScriptedProvider()
            .Then(Stream(ToolCall("call_1", "find_person", JsonSerializer.Serialize(new { query = name }))))
            .Then(Stream(Text("Gunner24 is "), Text(person + ".")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedPersonAsync(host, person, name);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Who is Gunner?" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = ParseEvents(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("conversation", events[0].Name);
        Assert.Equal("Answered", events[^1].Data.GetProperty("outcome").GetString());
        Assert.Contains(events, e => e.Name == "message"
            && e.Data.GetProperty("role").GetString() == "tool"
            && e.Data.GetProperty("toolName").GetString() == "find_person");

        // What was offered: this person's tools, nothing needing a permission they lack.
        var offered = provider.ToolNames(0);
        Assert.Contains("find_person", offered);
        Assert.Contains("get_person_cases", offered);
        Assert.DoesNotContain("search_audit_log", offered);
        Assert.DoesNotContain("list_live_instances", offered);
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
        Assert.DoesNotContain("SecretName", provider.Bodies[1], StringComparison.Ordinal);

        // The refusal still shows up as a tool turn -- just one that did not work -- rather than
        // as a separate event type, so this is where "the tool did not run" is actually checked.
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

    /// <summary>Chat's own limit is the operator's share of the bill for Chat, so the permission does not lift it.</summary>
    [Fact]
    public async Task ChatsOwnFeatureLimit_IsHonouredEvenPastPersonalLimits()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);

        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.UseAiPastLimits, Ct);
        await SpendAsync(host, user.Id, 1m);
        await LimitAsync(host, new AiSpendLimit { AppliesTo = AiSpendLimit.ForFeature, Feature = "chat", PerMonth = 1m });

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("The monthly AI spend limit for Chat is reached.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        Assert.Empty(provider.Bodies);
    }

    [Fact]
    public async Task ATokenLimitKeptFromBeforePrices_StillStopsChat()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);

        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        await SpendAsync(host, user.Id, 0.001m);

        await using (var context = _db.NewContext())
        {
            context.AiFeatureLimits.Add(new AiFeatureLimit { Feature = "chat", MonthlyTokenLimit = 1000 });
            await context.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "hi" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("The monthly AI token limit for Chat is reached.", (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
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

    // ── Versions of a question and of a reply ────────────────────────────────────────────────

    [Fact]
    public async Task RenamingReadingAVersionAndSpend_NeedUseAiChat_AndAreOnlyEverTheOwners()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartWithProviderAsync(new ScriptedProvider());

        var (owner, ownerCookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        var (_, otherCookie) = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var (_, withoutChat) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var id = await ConversationAsync(host, owner.Id, "Who is Gunner24?");

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Put, $"/api/chat/conversations/{id}/title", new { title = "Gunner" }),
                     (HttpMethod.Post, $"/api/chat/conversations/{id}/version", new { messageId = 1 }),
                     (HttpMethod.Get, $"/api/chat/conversations/{id}/spend", null),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(method, path, body, withoutChat, Ct)).StatusCode);

            // Somebody else's conversation is a conversation that does not exist.
            Assert.Equal(HttpStatusCode.NotFound, (await host.SendJsonAsync(method, path, body, otherCookie, Ct)).StatusCode);
        }

        var renamed = await host.SendJsonAsync(HttpMethod.Put, $"/api/chat/conversations/{id}/title",
            new { title = "Gunner's bans" }, ownerCookie, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Gunner's bans", (await ApiTestHost.BodyOf(renamed, Ct)).GetProperty("title").GetString());

        var empty = await host.SendJsonAsync(HttpMethod.Put, $"/api/chat/conversations/{id}/title",
            new { title = "   " }, ownerCookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    /// <summary>
    /// Asking again writes a second reply beside the first. Both are kept, the new one is what the
    /// conversation shows, and either can be read back.
    /// </summary>
    [Fact]
    public async Task AskingAgain_KeepsBothReplies_AndEitherCanBeRead()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var provider = new ScriptedProvider()
            .Then(Stream(Text("Three people.")))
            // The naming call a new conversation makes, between the two replies.
            .ThenJson(Completion("How many joined"))
            .Then(Stream(Text("Four people.")));

        await using var host = await StartWithProviderAsync(provider);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        var first = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "How many?" }, cookie, Ct);
        var events = ParseEvents(await first.Content.ReadAsStringAsync(Ct));
        var id = events[0].Data.GetProperty("id").GetString();

        var question = events.Where(e => e.Name == "message").Select(e => e.Data)
            .First(m => m.GetProperty("role").GetString() == "user").GetProperty("id").GetInt64();

        var again = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages",
            new { conversationId = id, retryAfterMessageId = question }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        await again.Content.ReadAsStringAsync(Ct);

        // Nothing was asked twice: the second reply answers the same question.
        var shown = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/chat/conversations/{id}", null, cookie, Ct), Ct);

        var messages = shown.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Four people.", messages[1].GetProperty("content").GetString());

        var versions = messages[1].GetProperty("versions").EnumerateArray().Select(v => v.GetInt64()).ToList();
        Assert.Equal(2, versions.Count);
        Assert.Equal(messages[1].GetProperty("id").GetInt64(), versions[1]);

        var older = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/chat/conversations/{id}/version",
                new { messageId = versions[0] }, cookie, Ct), Ct);

        Assert.Equal("Three people.",
            older.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString());
    }

    /// <summary>An edited question hangs where the old one hung, and keeps it beside it.</summary>
    [Fact]
    public async Task AnEditedQuestion_IsKeptBesideTheOldOne_WithItsOwnReply()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var provider = new ScriptedProvider()
            .Then(Stream(Text("Nobody.")))
            // The naming call a new conversation makes, between the two replies.
            .ThenJson(Completion("Who joined"))
            .Then(Stream(Text("Two people.")));

        await using var host = await StartWithProviderAsync(provider);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        var first = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Who joined?" }, cookie, Ct);
        var events = ParseEvents(await first.Content.ReadAsStringAsync(Ct));
        var id = events[0].Data.GetProperty("id").GetString();
        var question = events.Where(e => e.Name == "message").Select(e => e.Data)
            .First(m => m.GetProperty("role").GetString() == "user").GetProperty("id").GetInt64();

        var edited = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages",
            new { conversationId = id, text = "Who joined this week?", replaceMessageId = question }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        await edited.Content.ReadAsStringAsync(Ct);

        var shown = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/chat/conversations/{id}", null, cookie, Ct), Ct);

        var messages = shown.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("Who joined this week?", messages[0].GetProperty("content").GetString());
        Assert.Equal("Two people.", messages[1].GetProperty("content").GetString());

        var versions = messages[0].GetProperty("versions").EnumerateArray().Select(v => v.GetInt64()).ToList();
        Assert.Equal([question, messages[0].GetProperty("id").GetInt64()], versions);

        // The old question, and the reply it got, are still there to go back to.
        var older = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/chat/conversations/{id}/version",
                new { messageId = question }, cookie, Ct), Ct);

        Assert.Equal(["Who joined?", "Nobody."],
            older.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));

        // Both versions of the question, and both replies, are in the conversation.
        await using var context = _db.NewContext();
        Assert.Equal(4, await context.AiChatMessages.CountAsync(m => m.ConversationId == Guid.Parse(id!), Ct));
    }

    [Fact]
    public async Task AMessageOfSomebodyElsesConversation_CannotBeAskedAgainOrEdited()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartWithProviderAsync(new ScriptedProvider());

        var (owner, _) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        var (_, ownerOfNothing) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        var id = await ConversationAsync(host, owner.Id, "Mine");

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages",
            new { conversationId = id, retryAfterMessageId = 1 }, ownerOfNothing, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── The name a conversation is listed under ──────────────────────────────────────────────

    [Fact]
    public async Task ANewConversation_IsNamedByTheModel_AndTheNamingCallIsCounted()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var provider = new ScriptedProvider()
            .Then(StreamWithUsage([Text("Nobody was banned.")], Usage(100, 0, 20)))
            .ThenJson(Completion("\"Bans in March\"\n"));

        await using var host = await StartWithProviderAsync(provider);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages",
            new { text = "Was anybody banned in March?" }, cookie, Ct);

        var events = ParseEvents(await response.Content.ReadAsStringAsync(Ct));
        var named = events.Last(e => e.Name == "conversation");

        // Quotation marks and the line break the model wrapped it in are not part of the name.
        Assert.Equal("Bans in March", named.Data.GetProperty("title").GetString());

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/chat", null, cookie, Ct), Ct);
        Assert.Equal("Bans in March",
            Assert.Single(list.GetProperty("conversations").EnumerateArray()).GetProperty("title").GetString());

        // The naming call is a call like any other: counted under Chat, for the person who asked.
        await using var context = _db.NewContext();
        var rows = await context.AiUsage.Where(u => u.Feature == "chat" && u.UserId == user.Id).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task WhenTheModelCannotNameIt_TheFirstWordsOfTheQuestionStay()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        // One answer only: the naming call that follows gets the script's refusal.
        var provider = new ScriptedProvider().Then(Stream(Text("Nobody.")));

        await using var host = await StartWithProviderAsync(provider);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages",
            new { text = "Was anybody banned in March?" }, cookie, Ct);

        await response.Content.ReadAsStringAsync(Ct);

        var list = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/chat", null, cookie, Ct), Ct);
        Assert.Equal("Was anybody banned in March?",
            Assert.Single(list.GetProperty("conversations").EnumerateArray()).GetProperty("title").GetString());
    }

    // ── Stopping a reply ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stop means stop at the provider, not just in the browser: the request the reply is being
    /// written by is cancelled, and what the model had written by then is kept, marked stopped.
    /// </summary>
    [Fact]
    public async Task StoppingAReply_CancelsTheProviderCall_AndKeepsWhatWasWritten()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var provider = new HangingProvider(Stream(Text("Looking, ")).Replace("data: [DONE]\n\n", "", StringComparison.Ordinal));
        await using var host = await StartWithProviderAsync(provider);

        // The reply cannot outlive this even if the browser's own stop never arrives.
        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.AiChatTimeLimitSeconds = AI.Chat.ChatSettingsRules.MinTimeLimitSeconds;
            await context.SaveChangesAsync(Ct);
        }

        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);

        using var stop = new CancellationTokenSource();
        var request = host.Authenticated(HttpMethod.Post, "/api/chat/messages", cookie);
        request.Content = new StringContent("""{"text":"Who is online?"}""", Encoding.UTF8, "application/json");

        var sending = host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);

        // Headers arrive as soon as the "conversation" event is sent -- well before the model has
        // written anything -- so by the time the model starts writing, the send above has already
        // finished normally with a 200. That is expected: a real browser's fetch resolves on
        // headers the same way, and Stop is a browser-side decision to stop reading, not something
        // the initial response promise could ever observe.
        Assert.Equal(HttpStatusCode.OK, (await sending).StatusCode);

        // The model has started writing; the person presses Stop.
        await provider.Writing.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await stop.CancelAsync();

        // The provider call itself was cancelled, rather than left running to the end -- this, not
        // the send above, is where "Stop means stop at the provider" is actually checked.
        await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var stored = await EventuallyAsync(async () =>
        {
            await using var context = _db.NewContext();
            var messages = await context.AiChatMessages
                .Where(m => m.Conversation.UserId == user.Id)
                .OrderBy(m => m.Id)
                .ToListAsync(Ct);

            return messages.Count == 2 ? messages : null;
        });

        Assert.Equal(["user", "assistant"], stored.Select(m => m.Role));
        Assert.Equal("Looking, ", stored[1].Content);
        Assert.True(stored[1].Stopped);
    }

    // ── What a conversation cost ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AConversationsSpend_AddsUpTheRoundsItIsMadeOf()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var provider = new ScriptedProvider()
            .Then(StreamWithUsage([ToolCall("call_1", "find_person", """{"query":"x"}""")], Usage(1_000_000, 0, 100_000)))
            .Then(StreamWithUsage([Text("Nobody.")], Usage(2_000_000, 0, 200_000)));

        await using var host = await StartWithProviderAsync(provider);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile, Ct);

        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/prices", new
        {
            prices = new[] { new { model = "test-model", inputPerMillion = 1m, outputPerMillion = 4m } },
        }, admin, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Who is x?" }, cookie, Ct);
        var events = ParseEvents(await response.Content.ReadAsStringAsync(Ct));
        var id = events[0].Data.GetProperty("id").GetString();

        var spend = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/chat/conversations/{id}/spend", null, cookie, Ct), Ct);

        // 3M in at $1 and 0.3M out at $4 = $4.20.
        Assert.Equal(4.2m, spend.GetProperty("cost").GetDecimal());
        Assert.Equal(3_000_000, spend.GetProperty("inputTokens").GetInt64());
        Assert.Equal(300_000, spend.GetProperty("outputTokens").GetInt64());
        Assert.Equal(0, spend.GetProperty("unpricedTokens").GetInt64());
    }

    // ── The wider set of tools (design §3.2, added with Discord, flags and the calendar) ──────

    [Fact]
    public async Task ANewTool_IsOnlyOfferedToSomebodyHoldingItsPermission()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var provider = new ScriptedProvider()
            .Then(Stream(Text("Hello.")))
            .Then(Stream(Text("Hello.")))
            .Then(Stream(Text("Hello.")));

        await using var host = await StartWithProviderAsync(provider);

        var (_, plain) = await host.SignedInAsync(ModbotPermissions.UseAiChat, Ct);
        var (_, reader) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ReadDiscordMessages, Ct);
        var (_, calendar) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewCalendar, Ct);

        await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Question one" }, plain, Ct);
        await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Question two" }, reader, Ct);
        await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Question three" }, calendar, Ct);

        // Naming a new conversation is a provider call of its own, so the request a question was
        // sent in is found by the question rather than counted.
        List<string?> Offered(string question) =>
            provider.ToolNames(provider.Bodies.FindIndex(b => b.Contains(question, StringComparison.Ordinal)));

        // Every tool needs something, so somebody holding only UseAiChat is offered none of them.
        Assert.Empty(Offered("Question one"));

        Assert.Equal(
            ["discord_messages_around", "search_discord_messages"],
            Offered("Question two").Order(StringComparer.Ordinal));

        Assert.Equal(
            ["get_calendar_event", "list_calendar_events"],
            Offered("Question three").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SearchingDiscordMessages_MarksDeletedOnes_CapsTheRows_AndSaysThereWereMore()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var author = $"d_{Guid.NewGuid():N}";
        var provider = new ScriptedProvider()
            .Then(Stream(ToolCall("call_1", "search_discord_messages", JsonSerializer.Serialize(new { discordUserId = author, limit = 2 }))))
            .Then(Stream(Text("Three messages, two shown.")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedDiscordMessagesAsync(host, author);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ReadDiscordMessages, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "What did they say?" }, cookie, Ct);
        var events = ParseEvents(await response.Content.ReadAsStringAsync(Ct));

        var result = ToolResult(events, "search_discord_messages");

        Assert.Equal(2, result.GetProperty("count").GetInt32());
        Assert.True(result.GetProperty("more").GetBoolean());

        var messages = result.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(
            [$"{author}_m3", $"{author}_m2"],
            messages.Select(m => m.GetProperty("messageId").GetString()));
        Assert.True(messages[0].GetProperty("deleted").GetBoolean());
        Assert.False(messages[1].GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task SearchingDiscordMessages_OnlyEverReturnsMessagesOfTheServerModbotWatches()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var author = $"d_{Guid.NewGuid():N}";
        var provider = new ScriptedProvider()
            .Then(Stream(ToolCall("call_1", "search_discord_messages", JsonSerializer.Serialize(new { discordUserId = author }))))
            .Then(Stream(Text("Three.")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedDiscordMessagesAsync(host, author, elsewhere: "somewhere else entirely");

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ReadDiscordMessages, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "What did they say?" }, cookie, Ct);
        var result = ToolResult(ParseEvents(await response.Content.ReadAsStringAsync(Ct)), "search_discord_messages");

        Assert.Equal(3, result.GetProperty("count").GetInt32());
        Assert.DoesNotContain("somewhere else entirely", result.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlagsTool_ReturnsOnlyTheFlagsOfThePersonAskedAbout()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var person = $"usr_{Guid.NewGuid():N}";
        var somebodyElse = $"usr_{Guid.NewGuid():N}";

        var provider = new ScriptedProvider()
            .Then(Stream(ToolCall("call_1", "get_person_flags", JsonSerializer.Serialize(new { userId = person }))))
            .Then(Stream(Text("One flag.")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedFlagAsync(host, person, "theirs-only");
        await SeedFlagAsync(host, somebodyElse, "not-theirs");

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Any flags?" }, cookie, Ct);
        var result = ToolResult(ParseEvents(await response.Content.ReadAsStringAsync(Ct)), "get_person_flags");

        Assert.Equal(1, result.GetProperty("count").GetInt32());
        Assert.Contains("theirs-only", result.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("not-theirs", result.GetRawText(), StringComparison.Ordinal);
    }

    // ── Sources (design §3.2.1) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASource_IsStoredWithTheToolResult_WithWhatItOpens()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var author = $"d_{Guid.NewGuid():N}";
        var provider = new ScriptedProvider()
            .Then(Stream(ToolCall("call_1", "search_discord_messages", JsonSerializer.Serialize(new { discordUserId = author }))))
            .Then(Stream(Text("They said three things.")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedDiscordMessagesAsync(host, author);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ReadDiscordMessages, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "What did they say?" }, cookie, Ct);
        var events = ParseEvents(await response.Content.ReadAsStringAsync(Ct));

        var id = events[0].Data.GetProperty("id").GetString();
        var conversation = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/chat/conversations/{id}", null, cookie, Ct), Ct);

        var tool = conversation.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "tool");

        var references = tool.GetProperty("references").EnumerateArray().ToList();

        var message = references.Single(r => r.GetProperty("kind").GetString() == "message"
                                             && r.GetProperty("id").GetString() == $"{author}_m3");

        // A message opens in its author's messages, so the chip carries whose it is.
        Assert.Equal(author, message.GetProperty("author").GetString());
        Assert.Contains(references, r => r.GetProperty("kind").GetString() == "discord-person"
                                         && r.GetProperty("id").GetString() == author);
    }

    // ── Who asked about whom (design §13) ────────────────────────────────────────────────────

    [Fact]
    public async Task AQuestionThatReadsAboutPeople_RecordsOneFactForEachOfThem_NotOnePerTool()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var one = $"usr_{Guid.NewGuid():N}";
        var two = $"usr_{Guid.NewGuid():N}";

        // Three tool calls, two people: the question is one lookup of each, not three of anything.
        var provider = new ScriptedProvider()
            .Then(Stream(
                ToolCall("call_1", "find_person", JsonSerializer.Serialize(new { query = one })),
                MoreCall(1, "call_2", "get_person", JsonSerializer.Serialize(new { userId = one })),
                MoreCall(2, "call_3", "find_person", JsonSerializer.Serialize(new { query = two }))))
            .Then(Stream(Text("Both are members.")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedPersonAsync(host, one, $"One{Guid.NewGuid():N}");
        await SeedPersonAsync(host, two, $"Two{Guid.NewGuid():N}");

        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile, Ct);

        await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Who are these two?" }, cookie, Ct);

        var facts = await LookupFactsAsync(host, [one, two]);

        Assert.Equal(2, facts.Count);
        Assert.Contains(one, facts.Select(f => f.SubjectId));
        Assert.Contains(two, facts.Select(f => f.SubjectId));
        Assert.All(facts, f => Assert.Equal(user.Id.ToString(), f.ActorId));
        Assert.All(facts, f => Assert.Equal(FactPlatform.Modbot, f.ActorPlatform));

        // Both carry the whole list, so it still reads as one question.
        foreach (var fact in facts)
        {
            Assert.Contains(one, fact.Data, StringComparison.Ordinal);
            Assert.Contains(two, fact.Data, StringComparison.Ordinal);
            Assert.Contains("get_person", fact.Data, StringComparison.Ordinal);

            // The question itself is never stored: this is a record of access, not of what was said.
            Assert.DoesNotContain("Who are these two?", fact.Data, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AQuestionThatReadsNobody_RecordsNothing()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var provider = new ScriptedProvider().Then(Stream(Text("Nothing to look up.")));
        await using var host = await StartWithProviderAsync(provider);

        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile, Ct);

        await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = "Hello" }, cookie, Ct);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // Scoped to this test's own asker: ResetDeploymentAsync does not clear the facts table
        // (other tests' audit history has to survive a reset too), so a plain count of every
        // ChatLookup fact in the shared database is one other test's lookups away from failing.
        Assert.Equal(0, await db.Events.CountAsync(
            e => e.Type == FactType.ChatLookup && e.ActorId == user.Id.ToString(), Ct));
    }

    [Fact]
    public async Task AskingAgainAndEditing_EachRecordTheLookupAgain()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var person = $"usr_{Guid.NewGuid():N}";
        var name = $"Asked{Guid.NewGuid():N}";

        var provider = new ScriptedProvider()
            .Then(Stream(ToolCall("call_1", "find_person", JsonSerializer.Serialize(new { query = name }))))
            .Then(Stream(Text("A member.")))
            .ThenJson(Completion("Who they are"))
            .Then(Stream(ToolCall("call_2", "find_person", JsonSerializer.Serialize(new { query = name }))))
            .Then(Stream(Text("Still a member.")))
            .Then(Stream(ToolCall("call_3", "find_person", JsonSerializer.Serialize(new { query = name }))))
            .Then(Stream(Text("A member, again.")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedPersonAsync(host, person, name);

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile, Ct);

        var first = ParseEvents(await (await host.SendJsonAsync(
            HttpMethod.Post, "/api/chat/messages", new { text = $"Who is {name}?" }, cookie, Ct)).Content.ReadAsStringAsync(Ct));

        var conversationId = first[0].Data.GetProperty("id").GetString();
        var question = first.Where(e => e.Name == "message").Select(e => e.Data)
            .First(m => m.GetProperty("role").GetString() == "user").GetProperty("id").GetInt64();

        Assert.Single(await LookupFactsAsync(host, [person]));

        await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/chat/messages",
            new { conversationId, retryAfterMessageId = question },
            cookie,
            Ct);

        Assert.Equal(2, (await LookupFactsAsync(host, [person])).Count);

        await host.SendJsonAsync(
            HttpMethod.Post,
            "/api/chat/messages",
            new { conversationId, text = $"And who is {name}, really?", replaceMessageId = question },
            cookie,
            Ct);

        Assert.Equal(3, (await LookupFactsAsync(host, [person])).Count);
    }

    [Fact]
    public async Task TheLookupFact_IsInThePersonsOwnHistory()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);

        var person = $"usr_{Guid.NewGuid():N}";
        var name = $"Seen{Guid.NewGuid():N}";

        var provider = new ScriptedProvider()
            .Then(Stream(ToolCall("call_1", "find_person", JsonSerializer.Serialize(new { query = name }))))
            .Then(Stream(Text("A member.")));

        await using var host = await StartWithProviderAsync(provider);
        await SeedPersonAsync(host, person, name);

        var (asker, cookie) = await host.SignedInAsync(
            ModbotPermissions.UseAiChat | ModbotPermissions.ViewProfile | ModbotPermissions.ViewAuditLog, Ct);

        await host.SendJsonAsync(HttpMethod.Post, "/api/chat/messages", new { text = $"Who is {name}?" }, cookie, Ct);

        var history = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/audit?subject={person}", null, cookie, Ct), Ct);

        var entry = history.GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("type").GetString() == FactType.ChatLookup);

        Assert.Equal(person, entry.GetProperty("subjectId").GetString());
        Assert.Equal(asker.Id.ToString(), entry.GetProperty("actorId").GetString());
    }

    // ── Seeds for the wider tools ────────────────────────────────────────────────────────────

    /// <summary>Three messages by one person, the newest deleted, and one in another server.</summary>
    private static async Task SeedDiscordMessagesAsync(ApiTestHost host, string author, string? elsewhere = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var guild = $"g_{Guid.NewGuid():N}";
        var settings = await db.GetSettingsAsync(Ct);
        settings.DiscordGuildId = guild;

        var at = host.Clock.UtcNow;
        var times = new[] { at.AddHours(-3), at.AddHours(-2), at.AddHours(-1) };
        await new MessagePartitionMaintainer(db).EnsureForAsync(times, Ct);

        DiscordMessage Message(string id, DateTimeOffset sent, string text, string inGuild) => new()
        {
            MessageId = id,
            SentAt = sent,
            GuildId = inGuild,
            ChannelId = "500",
            AuthorId = author,
            AuthorName = "Somebody",
            Text = text,
            StoredAt = at,
        };

        // The message ids are per-author rather than literally "m1".."m4": the table is shared by
        // the whole assembly, and a fixed id collides with the same seed running in another test.
        var deleted = Message($"{author}_m3", times[2], "the last thing", guild);
        deleted.DeletedAt = times[2].AddMinutes(1);

        db.DiscordMessages.AddRange(
            Message($"{author}_m1", times[0], "the first thing", guild),
            Message($"{author}_m2", times[1], "the second thing", guild),
            deleted);

        if (elsewhere is not null)
            db.DiscordMessages.Add(Message($"{author}_m4", times[2], elsewhere, $"other_{Guid.NewGuid():N}"));

        await db.SaveChangesAsync(Ct);
    }

    private static async Task SeedFlagAsync(ApiTestHost host, string userId, string matched)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.ModerationFlags.Add(new ModerationFlag
        {
            Id = Guid.CreateVersion7(),
            FlaggedAt = host.Clock.UtcNow,
            RuleKind = "term",
            RuleId = Guid.CreateVersion7(),
            RuleName = "A rule",
            TermKey = "key",
            Term = "term",
            Target = "discordMessage",
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = userId,
            SubjectName = "Somebody",
            Matched = matched,
            State = ModerationFlagState.Open,
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The lookup facts recorded about these people, oldest first.</summary>
    private static async Task<List<LookupFactRow>> LookupFactsAsync(ApiTestHost host, IReadOnlyList<string> people)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.ChatLookup && people.Contains(e.SubjectId))
            .OrderBy(e => e.Id)
            .Select(e => new LookupFactRow(e.SubjectId, e.ActorId, e.ActorPlatform, e.Data))
            .ToListAsync(Ct);
    }

    private sealed record LookupFactRow(string SubjectId, string? ActorId, FactPlatform? ActorPlatform, string Data);

    /// <summary>What one tool sent back, out of the stream.</summary>
    private static JsonElement ToolResult(IReadOnlyList<SseEvent> events, string tool)
    {
        var message = events.Where(e => e.Name == "message").Select(e => e.Data)
            .Single(m => m.TryGetProperty("toolName", out var name) && name.GetString() == tool);

        return JsonDocument.Parse(message.GetProperty("content").GetString()!).RootElement.Clone();
    }

    /// <summary>A second or third tool call in the same round, as a provider streams them.</summary>
    private static object MoreCall(int index, string id, string name, string arguments) => new
    {
        role = "assistant",
        tool_calls = new[] { new { index, id, type = "function", function = new { name, arguments } } },
    };

    // ── Pieces ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Waits for something the reply's own task stores after the browser has gone.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read) where T : class
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await read() is { } value)
                return value;

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("Nothing was stored.");
    }

    /// <summary>A whole (non-streamed) answer, which is what the naming call asks for.</summary>
    private static string Completion(string text) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-name",
        @object = "chat.completion",
        created = 1_700_000_000,
        model = "test-model",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 200, completion_tokens = 5, total_tokens = 205 },
    });

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
    private async Task<ApiTestHost> StartWithProviderAsync(HttpMessageHandler provider)
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

        /// <summary>A whole answer rather than a stream: what a call that does not stream gets.</summary>
        public ScriptedProvider ThenJson(string json)
        {
            _answers.Enqueue(json);
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

            var streamed = sse.StartsWith("data: ", StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, streamed ? "text/event-stream" : "application/json"),
            };
        }
    }

    /// <summary>
    /// A provider that writes the start of a reply and then stops answering until the call is
    /// cancelled -- a model still thinking when somebody presses Stop.
    /// </summary>
    private sealed class HangingProvider(string first) : HttpMessageHandler
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(first);

        /// <summary>Completes once the first piece of the reply has been handed over.</summary>
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the request Modbot made was cancelled.</summary>
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => Cancelled.TrySetResult());

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream(_first, Writing, Cancelled)),
            };

            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class HangingStream(byte[] first, TaskCompletionSource writing, TaskCompletionSource cancelled) : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                first.CopyTo(buffer);
                writing.TrySetResult();
                return first.Length;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Hands requests to the shared script without letting the client factory dispose it.</summary>
    private sealed class Forwarding(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(inner, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _invoker.SendAsync(request, cancellationToken);
    }
}
