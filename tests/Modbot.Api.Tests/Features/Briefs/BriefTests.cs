using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Briefs;

/// <summary>
/// AI briefs (AI chat design §14): who may ask, what the model is given, what comes back, and what
/// is recorded -- against a scripted provider. Nothing here reaches a real one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class BriefTests
{
    private const string Endpoint = "https://llm.example.org/v1";

    private readonly PostgresFixture _db;

    public BriefTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Reader =
        ModbotPermissions.UseAiChat | ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewProfile;

    // ── Who may ask, and when ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BriefsStartOff_AndAreRefusedWithoutAskingTheProvider()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider, briefs: false);
        var (_, cookie) = await host.SignedInAsync(Reader, Ct);

        var person = await host.SendJsonAsync(HttpMethod.Post, "/api/briefs/people", new { vrchatUserId = "usr_off" }, cookie, Ct);
        var instance = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/instances/{Guid.NewGuid()}", new { }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Conflict, person.StatusCode);
        Assert.Equal("AI briefs are off.", (await ApiTestHost.BodyOf(person, Ct)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, instance.StatusCode);
        Assert.Empty(provider.Bodies);
    }

    [Fact]
    public async Task TheBriefSwitch_StartsOff_IsSavedWithChat_AndIsInTheSignedInPersonsInfo()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, admin) = await host.SignedInAsync(ModbotPermissions.ManageSettings | ModbotPermissions.UseAiChat, Ct);

        var fresh = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/settings/ai/chat", null, admin, Ct), Ct);
        Assert.False(fresh.GetProperty("briefs").GetBoolean());

        var saved = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/chat", new
        {
            enabled = true,
            briefs = true,
            model = (string?)null,
            instructions = (string?)null,
            maxToolCalls = 8,
            maxReplyTokens = 2000,
            timeLimitSeconds = 120,
            tools = new Dictionary<string, bool>(),
        }, admin, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True((await ApiTestHost.BodyOf(saved, Ct)).GetProperty("briefs").GetBoolean());

        // Chat and briefs on, but AI itself off: no briefs.
        var me = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, admin, Ct), Ct);
        Assert.False(me.GetProperty("briefsOn").GetBoolean());

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            (await db.GetSettingsAsync(Ct)).AiEnabled = true;
            await db.SaveChangesAsync(Ct);
        }

        me = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/auth/me", null, admin, Ct), Ct);
        Assert.True(me.GetProperty("briefsOn").GetBoolean());

        // Leaving the switch out of a save keeps it.
        var again = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/ai/chat", new
        {
            enabled = true,
            model = (string?)null,
            instructions = (string?)null,
            maxToolCalls = 8,
            maxReplyTokens = 2000,
            timeLimitSeconds = 120,
            tools = new Dictionary<string, bool>(),
        }, admin, Ct);
        Assert.True((await ApiTestHost.BodyOf(again, Ct)).GetProperty("briefs").GetBoolean());
    }

    [Fact]
    public async Task WithoutUseAiChat_BothBriefsAreRefused()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);
        var (_, cookie) = await host.SignedInAsync(Reader & ~ModbotPermissions.UseAiChat, Ct);

        var person = await host.SendJsonAsync(HttpMethod.Post, "/api/briefs/people", new { vrchatUserId = "usr_x" }, cookie, Ct);
        var instance = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/instances/{Guid.NewGuid()}", new { }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, person.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, instance.StatusCode);
        Assert.Empty(provider.Bodies);
    }

    /// <summary>The instance's Activity tab needs See the audit log, and so does its brief.</summary>
    [Fact]
    public async Task AnInstanceBrief_NeedsSeeTheAuditLog()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartWithProviderAsync(new ScriptedProvider());
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewAnalytics, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/instances/{Guid.NewGuid()}", new { }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── What the model is given, and what comes back ─────────────────────────────────────────

    [Fact]
    public async Task AnInstanceBrief_IsOneChatCall_WithNoTools_BuiltFromThatInstancesEntriesOnly()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);
        var (user, cookie) = await host.SignedInAsync(Reader, Ct);

        var world = $"wrld_{Guid.NewGuid():N}";
        var opened = host.Clock.UtcNow.AddHours(-3);
        var instance = await InstanceAsync(host, world, "39047", opened, opened.AddHours(2));

        var ada = $"usr_{Guid.NewGuid():N}";
        var bob = $"usr_{Guid.NewGuid():N}";

        var joined = await FactAsync(host, Presence(FactType.InstanceJoined, ada, opened.AddMinutes(5), world, "39047"));
        var left = await FactAsync(host, Presence(FactType.InstanceLeft, ada, opened.AddMinutes(35), world, "39047"));

        // The same number after the instance closed is another instance, and is not in its brief.
        var later = await FactAsync(host, Presence(FactType.InstanceJoined, bob, opened.AddHours(2).AddMinutes(30), world, "39047"));

        provider.Answer = $"{Hm(opened.AddMinutes(5))} {ada} joined [#{joined}]\n{Hm(opened.AddMinutes(35))} {ada} left [#{left}, #999999999]";

        var response = await host.SendJsonAsync(
            HttpMethod.Post, $"/api/briefs/instances/{instance}", new { timeZone = "UTC" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var brief = await ApiTestHost.BodyOf(response, Ct);

        Assert.Equal(provider.Answer, brief.GetProperty("text").GetString());
        Assert.Equal(2, brief.GetProperty("entries").GetInt32());
        Assert.False(brief.GetProperty("newest").GetBoolean());
        Assert.StartsWith("Written by AI from 2 audit log entries, ", brief.GetProperty("builtFrom").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("(UTC).", brief.GetProperty("builtFrom").GetString(), StringComparison.Ordinal);

        // An id the model wrote that it was not given is never a source.
        Assert.Equal(new[] { joined, left }, brief.GetProperty("sources").EnumerateArray().Select(s => s.GetInt64()).ToArray());

        // One call, no tools, the instance's two entries and not the later one.
        var sent = Assert.Single(provider.Bodies);
        using var body = JsonDocument.Parse(sent);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());

        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Contains("No opinions.", Text(messages[0]), StringComparison.Ordinal);

        var given = Text(messages[1]);
        Assert.Contains($"#{joined} |", given, StringComparison.Ordinal);
        Assert.Contains($"#{left} |", given, StringComparison.Ordinal);
        Assert.DoesNotContain($"#{later} |", given, StringComparison.Ordinal);
        Assert.Contains("Times are in UTC.", given, StringComparison.Ordinal);

        // Counted and logged as Chat, under the asker, with the text kept: somebody pressed a button.
        await using var context = _db.NewContext();
        var usage = await context.AiUsage.SingleAsync(u => u.UserId == user.Id, Ct);
        Assert.Equal("chat", usage.Feature);

        var call = await context.AiCalls.SingleAsync(c => c.UserId == user.Id, Ct);
        Assert.Equal(("chat", AiCallOutcomes.Answered), (call.Feature, call.Outcome));
        Assert.Contains($"#{joined} |", call.Prompt, StringComparison.Ordinal);
        Assert.Equal(provider.Answer, call.Answer);
        Assert.Equal(call.Id, brief.GetProperty("callId").GetGuid());

        // Who was read about, as a Chat question records it.
        var lookup = Assert.Single(await host.FactsAsync(FactType.ChatLookup, ada, Ct));
        Assert.Empty(await host.FactsAsync(FactType.ChatLookup, bob, Ct));
        Assert.Equal(user.Id.ToString(), lookup.ActorId);
        Assert.Equal("brief", ApiTestHost.DataOf(lookup).GetProperty("via").GetString());
        Assert.Equal(instance.ToString(), ApiTestHost.DataOf(lookup).GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task NothingRecorded_IsAnswered_WithoutAskingTheProvider()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);
        var (user, cookie) = await host.SignedInAsync(Reader, Ct);

        var opened = host.Clock.UtcNow.AddHours(-3);
        var instance = await InstanceAsync(host, $"wrld_{Guid.NewGuid():N}", "1", opened, opened.AddHours(1));

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/instances/{instance}", new { }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var brief = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal(JsonValueKind.Null, brief.GetProperty("text").ValueKind);
        Assert.Equal(0, brief.GetProperty("entries").GetInt32());
        Assert.Empty(provider.Bodies);

        await using var context = _db.NewContext();
        Assert.False(await context.AiCalls.AnyAsync(c => c.UserId == user.Id, Ct));
    }

    /// <summary>The asker's own audit log permissions decide what the model is given.</summary>
    [Fact]
    public async Task APersonBrief_IsBuiltOnlyFromEntriesTheAskerMayRead()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider { Answer = "Nothing much." };
        await using var host = await StartWithProviderAsync(provider);
        var (user, cookie) = await host.SignedInAsync(ModbotPermissions.UseAiChat | ModbotPermissions.ViewAuditLog, Ct);

        var person = $"usr_{Guid.NewGuid():N}";
        var at = host.Clock.UtcNow.AddHours(-1);

        var moderation = await FactAsync(host, Presence(FactType.InstanceJoined, person, at, "wrld_p", "5"));
        var operational = await FactAsync(host, new FactRecord
        {
            Type = FactType.DiscordLinkPrompted,
            OccurredAt = at.AddMinutes(1),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = person,
            Source = FactSource.Modbot,
            Data = new JsonObject(),
        });

        var response = await host.SendJsonAsync(
            HttpMethod.Post, "/api/briefs/people", new { vrchatUserId = person, timeZone = "Europe/London" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("entries").GetInt32());

        var given = Text(JsonDocument.Parse(Assert.Single(provider.Bodies)).RootElement.GetProperty("messages")[1]);
        Assert.Contains($"#{moderation} |", given, StringComparison.Ordinal);
        Assert.DoesNotContain($"#{operational} |", given, StringComparison.Ordinal);
        Assert.Contains("Times are in Europe/London.", given, StringComparison.Ordinal);

        var lookup = Assert.Single(await host.FactsAsync(FactType.ChatLookup, person, Ct));
        Assert.Equal("brief", ApiTestHost.DataOf(lookup).GetProperty("via").GetString());
    }

    [Fact]
    public async Task APersonBrief_TakesExactlyOneId()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        await using var host = await StartWithProviderAsync(new ScriptedProvider());
        var (_, cookie) = await host.SignedInAsync(Reader, Ct);

        var none = await host.SendJsonAsync(HttpMethod.Post, "/api/briefs/people", new { }, cookie, Ct);
        var two = await host.SendJsonAsync(HttpMethod.Post, "/api/briefs/people", new { vrchatUserId = "usr_a", discordUserId = "1" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, two.StatusCode);
    }

    // ── Limits ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AtTheMonthlyAllowance_ABriefIsRefused_AndLoggedAsLimited_WithNothingSent()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);
        host.Clock.UtcNow = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        var (user, cookie) = await host.SignedInAsync(Reader, Ct);
        var person = $"usr_{Guid.NewGuid():N}";
        await FactAsync(host, Presence(FactType.InstanceJoined, person, host.Clock.UtcNow.AddHours(-1), "wrld_l", "5"));

        await SpendAsync(host, user.Id, 2m);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            (await db.GetSettingsAsync(Ct)).AiMemberMonthlyTokens = 1_000_000;
            await db.SaveChangesAsync(Ct);
        }

        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/briefs/people", new { vrchatUserId = person }, cookie, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(
            "You have used your monthly AI allowance (1,000,000 tokens). It starts again on 1 October.",
            (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        Assert.Empty(provider.Bodies);

        await using var context = _db.NewContext();
        var row = await context.AiCalls.SingleAsync(c => c.UserId == user.Id, Ct);
        Assert.Equal(("chat", AiCallOutcomes.Limited), (row.Feature, row.Outcome));

        // Nothing was sent, so nobody was read about.
        Assert.Empty(await host.FactsAsync(FactType.ChatLookup, person, Ct));
    }

    // ── Saving one as a note ─────────────────────────────────────────────────────────────────

    /// <summary>The server writes the note: the brief exactly as logged, a blank line, and its source line.</summary>
    [Fact]
    public async Task ABriefSavedAsANote_IsTheBriefExactly_AboutItsPerson_MarkedAsWrittenByAi()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);
        var (_, cookie) = await host.SignedInAsync(Reader | ModbotPermissions.WriteNotes, Ct);

        var person = $"usr_{Guid.NewGuid():N}";
        var joined = await FactAsync(host, Presence(FactType.InstanceJoined, person, host.Clock.UtcNow.AddHours(-1), "wrld_n", "5"));
        provider.Answer = $"  Joined an instance [#{joined}]  ";

        var brief = await BriefAboutAsync(host, cookie, person);
        var saved = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/{brief.GetProperty("callId").GetGuid()}/note", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var expected = $"Joined an instance [#{joined}]\n\n{brief.GetProperty("builtFrom").GetString()}";
        var note = await ApiTestHost.BodyOf(saved, Ct);
        Assert.True(note.GetProperty("writtenByAi").GetBoolean());
        Assert.Equal(expected, note.GetProperty("text").GetString());
        Assert.Equal(person, note.GetProperty("subjectId").GetString());

        // The brief shown, the call log and the note are the same words.
        Assert.Equal($"Joined an instance [#{joined}]", brief.GetProperty("text").GetString());
        await using (var context = _db.NewContext())
            Assert.Equal(brief.GetProperty("text").GetString(), (await context.AiCalls.SingleAsync(c => c.Id == brief.GetProperty("callId").GetGuid(), Ct)).Answer);

        var listed = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Get, $"/api/notes?userId={person}", null, cookie, Ct), Ct);
        var row = Assert.Single(listed.GetProperty("notes").EnumerateArray());
        Assert.True(row.GetProperty("writtenByAi").GetBoolean());
        Assert.Equal(expected, row.GetProperty("text").GetString());

        var fact = Assert.Single(await host.FactsAsync(FactType.NoteAdded, person, Ct));
        Assert.Equal("ai", ApiTestHost.DataOf(fact).GetProperty("writtenBy").GetString());
    }

    /// <summary>One brief, one note: pressing Save again, or racing it, saves nothing more.</summary>
    [Fact]
    public async Task ABriefCanBeSavedOnlyOnce()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);
        var (_, cookie) = await host.SignedInAsync(Reader | ModbotPermissions.WriteNotes, Ct);

        var person = $"usr_{Guid.NewGuid():N}";
        var joined = await FactAsync(host, Presence(FactType.InstanceJoined, person, host.Clock.UtcNow.AddHours(-1), "wrld_o", "5"));
        provider.Answer = $"Joined an instance [#{joined}]";

        var callId = (await BriefAboutAsync(host, cookie, person)).GetProperty("callId").GetGuid();

        var first = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/{callId}/note", null, cookie, Ct);
        var again = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/{callId}/note", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Single(await host.FactsAsync(FactType.NoteAdded, person, Ct));
    }

    /// <summary>
    /// Only a person's brief, only by the person who asked for it, and only about that person. A
    /// note written the ordinary way never carries the mark, whatever it sends.
    /// </summary>
    [Fact]
    public async Task OnlyYourOwnPersonBrief_CanBeSaved_AndAnOrdinaryNoteIsNeverMarked()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);
        var (_, cookie) = await host.SignedInAsync(Reader | ModbotPermissions.WriteNotes, Ct);
        var (_, other) = await host.SignedInAsync(Reader | ModbotPermissions.WriteNotes, Ct);

        var person = $"usr_{Guid.NewGuid():N}";
        var world = $"wrld_{Guid.NewGuid():N}";
        var opened = host.Clock.UtcNow.AddHours(-3);
        var instance = await InstanceAsync(host, world, "7", opened, opened.AddHours(2));
        var joined = await FactAsync(host, Presence(FactType.InstanceJoined, person, opened.AddMinutes(5), world, "7"));
        provider.Answer = $"Joined an instance [#{joined}]";

        var personCall = (await BriefAboutAsync(host, cookie, person)).GetProperty("callId").GetGuid();
        var instanceCall = (await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/instances/{instance}", new { }, cookie, Ct), Ct)).GetProperty("callId").GetGuid();

        // Somebody else's brief, an instance's brief (which also read this person), and no brief.
        var notTheirs = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/{personCall}/note", null, other, Ct);
        var instanceBrief = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/{instanceCall}/note", null, cookie, Ct);
        var noSuchCall = await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/{Guid.NewGuid()}/note", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.NotFound, notTheirs.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, instanceBrief.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noSuchCall.StatusCode);

        // The brief's words with more added, sent as an ordinary note naming the brief: saved as the
        // moderator's own words, never under the AI mark.
        var ordinary = await host.SendJsonAsync(HttpMethod.Post, "/api/notes", new
        {
            userId = person,
            text = provider.Answer + " and was rude to everyone",
            briefCallId = personCall,
            writtenBy = "ai",
        }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        Assert.False((await ApiTestHost.BodyOf(ordinary, Ct)).GetProperty("writtenByAi").GetBoolean());

        var fact = Assert.Single(await host.FactsAsync(FactType.NoteAdded, person, Ct));
        Assert.False(ApiTestHost.DataOf(fact).TryGetProperty("writtenBy", out _));
    }

    /// <summary>The note is written about the person the brief was about, never about anyone else.</summary>
    [Fact]
    public async Task ABriefIsSavedAboutItsOwnPerson_NotAnyoneElse()
    {
        await ApiTestHost.ResetDeploymentAsync(_db, Ct);
        var provider = new ScriptedProvider();
        await using var host = await StartWithProviderAsync(provider);
        var (_, cookie) = await host.SignedInAsync(Reader | ModbotPermissions.WriteNotes, Ct);

        var ada = $"usr_{Guid.NewGuid():N}";
        var bob = $"usr_{Guid.NewGuid():N}";
        var joined = await FactAsync(host, Presence(FactType.InstanceJoined, ada, host.Clock.UtcNow.AddHours(-1), "wrld_q", "5"));
        await FactAsync(host, Presence(FactType.InstanceJoined, bob, host.Clock.UtcNow.AddHours(-1), "wrld_q", "6"));
        provider.Answer = $"Joined an instance [#{joined}]";

        var callId = (await BriefAboutAsync(host, cookie, ada)).GetProperty("callId").GetGuid();
        var saved = await ApiTestHost.BodyOf(
            await host.SendJsonAsync(HttpMethod.Post, $"/api/briefs/{callId}/note", null, cookie, Ct), Ct);

        Assert.Equal(ada, saved.GetProperty("subjectId").GetString());
        Assert.Single(await host.FactsAsync(FactType.NoteAdded, ada, Ct));
        Assert.Empty(await host.FactsAsync(FactType.NoteAdded, bob, Ct));
    }

    private static async Task<JsonElement> BriefAboutAsync(ApiTestHost host, string cookie, string person)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, "/api/briefs/people", new { vrchatUserId = person }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Hm(DateTimeOffset at) => at.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string Text(JsonElement message) => message.GetProperty("content") switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString()!,
        var parts => string.Concat(parts.EnumerateArray().Select(p => p.GetProperty("text").GetString())),
    };

    private static FactRecord Presence(string type, string subject, DateTimeOffset at, string world, string number) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        WorldId = world,
        InstanceId = number,
        Source = FactSource.Companion,
        Data = new JsonObject { ["deviceId"] = $"device-{Guid.NewGuid():N}" },
    };

    private static async Task<long> FactAsync(ApiTestHost host, FactRecord fact)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>().EnsureForAsync(fact.OccurredAt, Ct);
        var written = await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(fact, Ct);
        return written.Id;
    }

    private static async Task<Guid> InstanceAsync(ApiTestHost host, string world, string number, DateTimeOffset opened, DateTimeOffset closed)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var instance = new VRChatInstance
        {
            Id = Guid.NewGuid(),
            Location = $"{world}:{number}",
            WorldId = world,
            VRChatInstanceId = number,
            GroupId = "grp_1",
            Type = "group",
            OpenedAt = opened,
            LastSeenAt = closed,
            ClosedAt = closed,
            ClosedBy = "list",
            SeenInGroupList = true,
        };

        db.VRChatInstances.Add(instance);
        await db.SaveChangesAsync(Ct);
        return instance.Id;
    }

    /// <summary>Usage worth <paramref name="cost"/> dollars: input tokens of a model priced at $1 per million.</summary>
    private static async Task SpendAsync(ApiTestHost host, Guid userId, decimal cost)
    {
        const string model = "spend-model";

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        if (!await db.AiModelPrices.AnyAsync(p => p.Model == model, Ct))
            db.AiModelPrices.Add(new AiModelPrice { Model = model, InputPerMillion = 1m, OutputPerMillion = 1m, UpdatedAt = host.Clock.UtcNow });

        db.AiUsage.Add(new AiUsage
        {
            At = host.Clock.UtcNow,
            Feature = "chat",
            UserId = userId,
            Model = model,
            InputTokens = (int)(cost * 1_000_000m),
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A host whose AI requests all go to <paramref name="provider"/>, with AI, Chat and briefs on.</summary>
    private async Task<ApiTestHost> StartWithProviderAsync(HttpMessageHandler provider, bool briefs = true)
    {
        var host = await ApiTestHost.StartAsync(_db, configure: services =>
            services.AddHttpClient(AiClients.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new Forwarding(provider)));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // Usage, prices and limits outlive ResetDeploymentAsync.
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
        settings.AiBriefsEnabled = briefs;

        await db.SaveChangesAsync(Ct);
        return host;
    }

    /// <summary>Answers every call with one whole (not streamed) completion of <see cref="Answer"/>.</summary>
    private sealed class ScriptedProvider : HttpMessageHandler
    {
        public string Answer { get; set; } = "Nothing.";

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-brief",
                    @object = "chat.completion",
                    created = 1_700_000_000,
                    model = "test-model",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = Answer }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 900, completion_tokens = 120, total_tokens = 1020 },
                }), Encoding.UTF8, "application/json"),
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
