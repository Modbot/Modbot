using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.AI;
using Modbot.AI.Moderation;
using Modbot.Analytics.Facts;
using Modbot.Moderation;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Moderation;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>
/// Settings → AutoMod: the switch, term lists, Hub subscriptions, AI topics, the AI tools, the daily
/// AI call limit and "Try it" (AutoMod design; AI moderation design).
/// </summary>
/// <remarks>
/// <para>
/// Every change to a rule writes a <c>modbot.ai-moderation.rule.change</c> fact naming the account,
/// and a rule set to act remembers who set it, because M8 §2 requires every action to name the
/// operator who allowed it.
/// </para>
/// <para>
/// The routes live at <c>/api/settings/automod</c>. The old <c>/api/settings/ai/moderation</c> is
/// rewritten to them (<see cref="ApiSurface.UseOldApiPaths"/>), so a client written against the
/// old name keeps working.
/// </para>
/// </remarks>
public static class AutoModEndpoints
{
    public const string Path = "/api/settings/automod";

    /// <summary>Where these routes were before AutoMod had its own tab.</summary>
    public const string OldPath = "/api/settings/ai/moderation";

    public const int MaxNameLength = 100;
    public const int MaxTermLength = 200;
    public const int MaxTerms = 5000;
    public const int MaxInstructionsLength = 2000;
    public const int MaxTryLength = 4000;
    public const int MaxDailyAiCalls = 100_000;

    /// <summary>Discord's own longest timeout: 28 days.</summary>
    public const int MaxTimeoutMinutes = 28 * 24 * 60;

    /// <summary>How many channels or roles one rule's scope may name.</summary>
    public const int MaxScopeIds = 200;

    public const int MaxSampleLength = 4000;

    public const int MaxSampleNoteLength = 500;

    /// <summary>How many samples one rule's test set may hold.</summary>
    public const int MaxSamples = 200;

    /// <summary>How many runs the tests screen shows.</summary>
    public const int RunsShown = 10;

    private static readonly string[] Sensitivities = ["low", "medium", "high"];

    public static IEndpointRouteBuilder MapAutoModSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(Path).WithTags("AutoMod settings");

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                [FromServices] IModbotClock clock,
                CancellationToken ct) => Results.Ok(await ViewAsync(db, ai, clock, ct)))
            .WithName("GetAutoMod")
            .WithSummary("The AutoMod switch, term lists, AI topics, the AI tools and the daily AI call limit")
            .Produces<AutoModResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] AutoModUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var switched = settings.AutoModEnabled != body.Enabled;
                var limit = body.DailyAiCallLimit ?? settings.AiModerationDailyCallLimit;

                if (limit is < 0 or > MaxDailyAiCalls)
                    return Error($"The daily AI call limit must be between 0 and {MaxDailyAiCalls}.");

                var tools = new Dictionary<string, bool>(AutoModAiTools.Parse(settings.AutoModAiTools), StringComparer.Ordinal);

                foreach (var (name, on) in body.AiTools ?? new Dictionary<string, bool>())
                {
                    if (!AutoModAiTools.IsTool(name))
                        return Error($"'{name}' is not an AI tool.");

                    tools[name] = on;
                }

                settings.AutoModEnabled = body.Enabled;
                settings.AiModerationDailyCallLimit = limit;
                settings.AutoModAiTools = AutoModAiTools.Serialize(tools);
                await db.SaveChangesAsync(ct);

                var data = new JsonObject
                {
                    ["enabled"] = body.Enabled,
                    ["dailyAiCallLimit"] = limit,
                    ["aiTools"] = JsonNode.Parse(settings.AutoModAiTools),
                };

                await RuleChangedAsync(http, facts, partitions, clock, "settings", null, "AutoMod",
                    switched ? (body.Enabled ? "switched-on" : "switched-off") : "changed", data, ct);

                return Results.Ok(await ViewAsync(db, ai, clock, ct));
            })
            .WithName("SetAutoMod")
            .WithSummary("Switch AutoMod on or off, set the daily AI call limit and switch AI tools")
            .Produces<AutoModResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        // ── Term lists ──────────────────────────────────────────────────────────────────────

        group.MapGet("/lists/{id:guid}", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var list = await db.ModerationTermLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
                if (list is null)
                    return NotFound("No such term list.");

                return Results.Ok(await DetailAsync(db, list, ct));
            })
            .WithName("GetTermList")
            .WithSummary("One term list with its terms")
            .Produces<TermListDetail>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/lists", async (
                HttpContext http,
                [FromBody] TermListInput body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var now = clock.UtcNow;
                var list = new ModerationTermList { Id = Guid.CreateVersion7(now), Source = TermListSource.Local, CreatedAt = now };

                if (ApplyList(list, body, http.User, now) is { } problem)
                    return Error(problem);

                AutoModRuleHistory.Version(
                    db, list, null, AutoModRuleHistory.SnapshotOf(list), AutoModRuleHistory.TextOf(list),
                    ModbotAuth.UserIdOf(http.User), http.User.Identity?.Name, now);

                var gate = await ActingAsync(db, list, wasActing: false, body.TrialDays, body.ActWithoutTest, now, ct);
                if (gate.Problem is { } why)
                    return Error(why);

                db.ModerationTermLists.Add(list);
                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list.Id, list.Name, "created", ListData(list), ct);
                await OverrideFactAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list, gate, ct);

                return Results.Ok(await DetailAsync(db, list, ct));
            })
            .WithName("CreateTermList")
            .WithSummary("Create a local term list")
            .Produces<TermListDetail>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("/lists/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] TermListInput body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var list = await db.ModerationTermLists.FirstOrDefaultAsync(l => l.Id == id, ct);
                if (list is null)
                    return NotFound("No such term list.");

                var now = clock.UtcNow;
                var before = AutoModRuleHistory.SnapshotOf(list);
                var wasActing = RuleGuards.WantsAction(list);

                if (ApplyList(list, body, http.User, now) is { } problem)
                    return Error(problem);

                AutoModRuleHistory.Version(
                    db, list, before, AutoModRuleHistory.SnapshotOf(list), AutoModRuleHistory.TextOf(list),
                    ModbotAuth.UserIdOf(http.User), http.User.Identity?.Name, now);

                var gate = await ActingAsync(db, list, wasActing, body.TrialDays, body.ActWithoutTest, now, ct);
                if (gate.Problem is { } why)
                    return Error(why);

                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list.Id, list.Name, "changed", ListData(list), ct);
                await OverrideFactAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list, gate, ct);

                return Results.Ok(await DetailAsync(db, list, ct));
            })
            .WithName("UpdateTermList")
            .WithSummary("Change a term list. A Hub list keeps its name and terms; only its switches, targets, action and switched-off terms change.")
            .Produces<TermListDetail>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapDelete("/lists/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                var list = await db.ModerationTermLists.FirstOrDefaultAsync(l => l.Id == id, ct);
                if (list is null)
                    return NotFound("No such term list.");

                // The test set goes with the rule; the versions and the runs stay, because a flag
                // that named this rule still has to be able to show the rule as it was.
                await db.ModerationTestSamples.Where(s => s.RuleId == id).ExecuteDeleteAsync(ct);

                db.ModerationTermLists.Remove(list);
                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list.Id, list.Name, "deleted", ListData(list), ct);

                return Results.NoContent();
            })
            .WithName("DeleteTermList")
            .WithSummary("Delete a term list. Its flags stay.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        // ── Modbot Hub ──────────────────────────────────────────────────────────────────────

        group.MapGet("/hub", async (
                [FromServices] HubTermLists hub,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var (lists, error) = await hub.IndexAsync(ct);
                var subscribed = await db.ModerationTermLists.AsNoTracking()
                    .Where(l => l.HubId != null)
                    .Select(l => l.HubId!)
                    .ToListAsync(ct);

                return Results.Ok(new HubIndexResponse(
                    [.. lists.Select(l => new HubListView(l.Id, l.Name, l.Description, l.Version, l.RuleCount, l.SuitableFor, subscribed.Contains(l.Id)))],
                    error));
            })
            .WithName("ListHubTermLists")
            .WithSummary("The term lists Modbot Hub offers")
            .Produces<HubIndexResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/lists/hub", async (
                HttpContext http,
                [FromBody] HubSubscribe body,
                [FromServices] HubTermLists hub,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (!HubTermLists.IsHubId(body.HubId))
                    return Error("That is not a Modbot Hub list id.");

                if (await db.ModerationTermLists.AnyAsync(l => l.HubId == body.HubId, ct))
                    return Results.Conflict(new { error = "That list is already added." });

                var fetched = await hub.FetchAsync(body.HubId, ct);
                if (fetched.Error is not null)
                    return Error(fetched.Error);

                if (fetched.Terms.Count == 0)
                    return Error("That list has no terms.");

                var now = clock.UtcNow;
                var list = new ModerationTermList
                {
                    Id = Guid.CreateVersion7(now),
                    Name = Clip(fetched.Name ?? body.HubId, MaxNameLength),
                    Source = TermListSource.Cloud,
                    Enabled = true,
                    Targets = (int)ModerationTargetNames.All,
                    Terms = StoredTerm.Serialize(fetched.Terms),
                    HubId = body.HubId,
                    HubVersion = fetched.Version,
                    HubFetchedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                AutoModRuleHistory.Version(
                    db, list, null, AutoModRuleHistory.SnapshotOf(list), AutoModRuleHistory.TextOf(list),
                    ModbotAuth.UserIdOf(http.User), http.User.Identity?.Name, now);

                db.ModerationTermLists.Add(list);
                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list.Id, list.Name, "added-from-hub", ListData(list), ct);

                return Results.Ok(await DetailAsync(db, list, ct));
            })
            .WithName("AddHubTermList")
            .WithSummary("Add a term list from Modbot Hub")
            .Produces<TermListDetail>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/lists/{id:guid}/refresh", async (
                [FromRoute] Guid id,
                [FromServices] HubTermListUpdates updates,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var list = await db.ModerationTermLists.FirstOrDefaultAsync(l => l.Id == id, ct);
                if (list is null)
                    return NotFound("No such term list.");
                if (list.Source != TermListSource.Cloud)
                    return Error("Only a Modbot Hub list can be refreshed.");

                await updates.RefreshAsync(list, ct);
                await db.SaveChangesAsync(ct);

                return Results.Ok(await DetailAsync(db, list, ct));
            })
            .WithName("RefreshHubTermList")
            .WithSummary("Fetch a Hub list again. A newer version waits to be applied.")
            .Produces<TermListDetail>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/lists/{id:guid}/update", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] HubTermListUpdates updates,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                var list = await db.ModerationTermLists.FirstOrDefaultAsync(l => l.Id == id, ct);
                if (list is null)
                    return NotFound("No such term list.");

                var changes = list.HubAvailableChanges;
                var before = AutoModRuleHistory.SnapshotOf(list);

                if (!updates.Apply(list))
                    return Error("There is no newer version to apply.");

                // A Hub update changes what the rule catches, so it is a new version of the rule
                // like any other change -- and one that costs an acting rule its passing test run.
                AutoModRuleHistory.Version(
                    db, list, before, AutoModRuleHistory.SnapshotOf(list), AutoModRuleHistory.TextOf(list),
                    ModbotAuth.UserIdOf(http.User), http.User.Identity?.Name, clock.UtcNow);

                await db.SaveChangesAsync(ct);

                var data = ListData(list);
                data["changes"] = changes is null ? null : JsonNode.Parse(changes);
                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list.Id, list.Name, "updated-from-hub", data, ct);

                return Results.Ok(await DetailAsync(db, list, ct));
            })
            .WithName("ApplyHubTermListUpdate")
            .WithSummary("Put the newer version of a Hub list into use")
            .Produces<TermListDetail>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        // ── AI topics ───────────────────────────────────────────────────────────────────────

        group.MapPost("/topics", async (
                HttpContext http,
                [FromBody] TopicInput body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var now = clock.UtcNow;
                var topic = new ModerationTopic { Id = Guid.CreateVersion7(now), CreatedAt = now };

                if (ApplyTopic(topic, body, http.User, now) is { } problem)
                    return Error(problem);

                AutoModRuleHistory.Version(
                    db, topic, null, AutoModRuleHistory.SnapshotOf(topic), AutoModRuleHistory.TextOf(topic),
                    ModbotAuth.UserIdOf(http.User), http.User.Identity?.Name, now);

                var gate = await ActingAsync(db, topic, wasActing: false, body.TrialDays, body.ActWithoutTest, now, ct);
                if (gate.Problem is { } why)
                    return Error(why);

                db.ModerationTopics.Add(topic);

                // Every new topic starts with the injection samples, so a topic that can be talked
                // out of its instructions is caught by its own test set (design §15.5).
                AutoModRuleHistory.SeedInjectionSamples(db, topic, now);

                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.Topic, topic.Id, topic.Name, "created", TopicData(topic), ct);
                await OverrideFactAsync(http, facts, partitions, clock, ModerationRuleKind.Topic, topic, gate, ct);

                return Results.Ok(await TopicViewAsync(db, topic, ct));
            })
            .WithName("CreateAiTopic")
            .WithSummary("Create an AI topic")
            .Produces<TopicView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("/topics/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] TopicInput body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var topic = await db.ModerationTopics.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (topic is null)
                    return NotFound("No such topic.");

                var now = clock.UtcNow;
                var before = AutoModRuleHistory.SnapshotOf(topic);
                var wasActing = RuleGuards.WantsAction(topic);

                if (ApplyTopic(topic, body, http.User, now) is { } problem)
                    return Error(problem);

                AutoModRuleHistory.Version(
                    db, topic, before, AutoModRuleHistory.SnapshotOf(topic), AutoModRuleHistory.TextOf(topic),
                    ModbotAuth.UserIdOf(http.User), http.User.Identity?.Name, now);

                var gate = await ActingAsync(db, topic, wasActing, body.TrialDays, body.ActWithoutTest, now, ct);
                if (gate.Problem is { } why)
                    return Error(why);

                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.Topic, topic.Id, topic.Name, "changed", TopicData(topic), ct);
                await OverrideFactAsync(http, facts, partitions, clock, ModerationRuleKind.Topic, topic, gate, ct);

                return Results.Ok(await TopicViewAsync(db, topic, ct));
            })
            .WithName("UpdateAiTopic")
            .WithSummary("Change an AI topic")
            .Produces<TopicView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapDelete("/topics/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                var topic = await db.ModerationTopics.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (topic is null)
                    return NotFound("No such topic.");

                await db.ModerationTestSamples.Where(s => s.RuleId == id).ExecuteDeleteAsync(ct);

                db.ModerationTopics.Remove(topic);
                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.Topic, topic.Id, topic.Name, "deleted", TopicData(topic), ct);

                return Results.NoContent();
            })
            .WithName("DeleteAiTopic")
            .WithSummary("Delete an AI topic. Its flags stay.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        // ── Try it ──────────────────────────────────────────────────────────────────────────

        group.MapPost("/try", async (
                HttpContext http,
                [FromBody] TryRequest body,
                [FromServices] ModerationEngine engine,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (string.IsNullOrWhiteSpace(body.Text))
                    return Error("Enter some text.");
                if (body.Text.Length > MaxTryLength)
                    return Error($"The text is too long (at most {MaxTryLength} characters).");
                if (ModerationTargetNames.Parse(body.Target) is not { } target)
                    return Error("Choose a target.");

                var result = await engine.TryAsync(
                    body.Text, target, body.IncludeAi,
                    ModbotAuth.UserIdOf(http.User), ModbotAuth.UsernameOf(http.User), ct);

                return Results.Ok(new TryResponse(
                    [.. result.Matches.Select(m => new TryMatchView(
                        m.Match.RuleKind, m.Match.RuleId, m.Match.RuleName, m.RuleEnabled, m.Match.Term, m.Match.Matched,
                        m.Match.Reason, m.Match.DeleteMessage, m.Match.TimeoutMinutes, m.Match.GroupBan, m.Match.GroupRemove))],
                    result.WouldDeleteMessage,
                    result.WouldTimeOutMinutes,
                    result.AiSkipped,
                    result.CallId,
                    result.WouldGroupBan,
                    result.WouldGroupRemove));
            })
            .WithName("TryAutoMod")
            .WithSummary("Check some text against every rule. Nothing is recorded and nothing is done.")
            .Produces<TryResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        // ── Test sets, the trial and pauses ─────────────────────────────────────────────────
        //
        // These work the same way for both kinds of rule, so they share one set of routes rather
        // than being written twice with "list" and "topic" in the path.

        var rules = group.MapGroup("/rules/{kind}/{id:guid}");

        rules.MapGet("/tests", async (
                [FromRoute] string kind,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                if (await FindRuleAsync(db, kind, id, ct) is not { } rule)
                    return NotFound("No such rule.");

                return Results.Ok(new RuleTestsResponse(
                    kind, id, rule.Name, rule.Version, RuleGuards.ActsNow(rule),
                    await SamplesAsync(db, id, ct),
                    await RunsAsync(db, id, ct)));
            })
            .WithName("GetRuleTestSet")
            .WithSummary("A rule's sample texts and its last runs")
            .Produces<RuleTestsResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        rules.MapPost("/samples", async (
                [FromRoute] string kind,
                [FromRoute] Guid id,
                [FromBody] TestSampleInput body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (await FindRuleAsync(db, kind, id, ct) is null)
                    return NotFound("No such rule.");

                if (await db.ModerationTestSamples.CountAsync(s => s.RuleId == id, ct) >= MaxSamples)
                    return Error($"A test set can hold at most {MaxSamples} samples.");

                var now = clock.UtcNow;
                var sample = new ModerationTestSample
                {
                    Id = Guid.CreateVersion7(now),
                    RuleKind = kind,
                    RuleId = id,
                    CreatedAt = now,
                };

                if (ApplySample(sample, body, now) is { } problem)
                    return Error(problem);

                db.ModerationTestSamples.Add(sample);
                await db.SaveChangesAsync(ct);

                return Results.Ok(SampleView(sample));
            })
            .WithName("AddTestSample")
            .WithSummary("Add a sample text to a rule's test set")
            .Produces<TestSampleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        rules.MapPut("/samples/{sampleId:guid}", async (
                [FromRoute] Guid id,
                [FromRoute] Guid sampleId,
                [FromBody] TestSampleInput body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var sample = await db.ModerationTestSamples.FirstOrDefaultAsync(s => s.Id == sampleId && s.RuleId == id, ct);
                if (sample is null)
                    return NotFound("No such sample.");

                if (ApplySample(sample, body, clock.UtcNow) is { } problem)
                    return Error(problem);

                await db.SaveChangesAsync(ct);

                return Results.Ok(SampleView(sample));
            })
            .WithName("UpdateTestSample")
            .WithSummary("Change a sample text")
            .Produces<TestSampleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        rules.MapDelete("/samples/{sampleId:guid}", async (
                [FromRoute] Guid id,
                [FromRoute] Guid sampleId,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var removed = await db.ModerationTestSamples
                    .Where(s => s.Id == sampleId && s.RuleId == id)
                    .ExecuteDeleteAsync(ct);

                return removed == 0 ? NotFound("No such sample.") : Results.NoContent();
            })
            .WithName("DeleteTestSample")
            .WithSummary("Remove a sample text")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        rules.MapPost("/tests/run", async (
                HttpContext http,
                [FromRoute] string kind,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] ModerationEngine engine,
                CancellationToken ct) =>
            {
                if (await FindRuleAsync(db, kind, id, ct) is null)
                    return NotFound("No such rule.");

                if (!await db.ModerationTestSamples.AnyAsync(s => s.RuleId == id, ct))
                    return Error("Add at least one sample first.");

                var run = await engine.RunTestSetAsync(kind, id, ModbotAuth.UserIdOf(http.User), http.User.Identity?.Name, ct);

                return run is null ? NotFound("No such rule.") : Results.Ok(RunView(run));
            })
            .WithName("RunRuleTestSet")
            .WithSummary("Check every sample against the rule as it stands now. AI topics cost a real request.")
            .Produces<TestRunView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        rules.MapGet("/versions", async (
                [FromRoute] string kind,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var versions = await db.ModerationRuleVersions.AsNoTracking()
                    .Where(v => v.RuleId == id)
                    .OrderByDescending(v => v.Version)
                    .ToListAsync(ct);

                return Results.Ok(new RuleVersionList(kind, id,
                    [.. versions.Select(v => new RuleVersionView(v.Version, v.ChangedAt, v.ChangedByUsername, v.Name, v.Text))]));
            })
            .WithName("ListRuleVersions")
            .WithSummary("Every version of a rule's text, newest first")
            .Produces<RuleVersionList>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        rules.MapPost("/end-trial", async (
                HttpContext http,
                [FromRoute] string kind,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                var list = kind == ModerationRuleKind.TermList
                    ? await db.ModerationTermLists.FirstOrDefaultAsync(l => l.Id == id, ct) : null;
                var topic = kind == ModerationRuleKind.Topic
                    ? await db.ModerationTopics.FirstOrDefaultAsync(t => t.Id == id, ct) : null;

                if (list is null && topic is null)
                    return NotFound("No such rule.");

                IModerationRule rule = (IModerationRule?)list ?? topic!;
                if (!RuleGuards.InTrial(rule))
                    return Error("This rule is not in a trial.");

                var now = clock.UtcNow;
                var userId = ModbotAuth.UserIdOf(http.User);
                var username = Clip(http.User.Identity?.Name ?? string.Empty, 64);

                if (list is not null)
                {
                    list.TrialEndedAt = now;
                    list.TrialEndedByUserId = userId;
                    list.TrialEndedByUsername = username;
                }
                else
                {
                    topic!.TrialEndedAt = now;
                    topic.TrialEndedByUserId = userId;
                    topic.TrialEndedByUsername = username;
                }

                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, kind, id, rule.Name, "trial-ended", new JsonObject
                {
                    ["trialStartedAt"] = rule.TrialStartedAt?.ToString("O"),
                    ["trialDays"] = rule.TrialDays,
                    ["deleteMessage"] = rule.DeleteMessage,
                    ["timeoutMinutes"] = rule.TimeoutMinutes,
                    ["groupBan"] = rule.GroupBan,
                    ["groupRemove"] = rule.GroupRemove,
                }, ct);

                return list is not null
                    ? Results.Ok(await DetailAsync(db, list, ct))
                    : Results.Ok(await TopicViewAsync(db, topic!, ct));
            })
            .WithName("EndRuleTrial")
            .WithSummary("End a rule's trial. From now on it really acts.")
            .Produces<TermListDetail>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        rules.MapPost("/resume", async (
                HttpContext http,
                [FromRoute] string kind,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                var list = kind == ModerationRuleKind.TermList
                    ? await db.ModerationTermLists.FirstOrDefaultAsync(l => l.Id == id, ct) : null;
                var topic = kind == ModerationRuleKind.Topic
                    ? await db.ModerationTopics.FirstOrDefaultAsync(t => t.Id == id, ct) : null;

                if (list is null && topic is null)
                    return NotFound("No such rule.");

                IModerationRule rule = (IModerationRule?)list ?? topic!;
                if (rule.PausedAt is null)
                    return Error("This rule is not paused.");

                var was = rule.PausedReason;

                if (list is not null)
                {
                    list.PausedAt = null;
                    list.PausedReason = null;
                }
                else
                {
                    topic!.PausedAt = null;
                    topic.PausedReason = null;
                }

                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, kind, id, rule.Name, "resumed",
                    new JsonObject { ["pausedBecause"] = was }, ct);

                return list is not null
                    ? Results.Ok(await DetailAsync(db, list, ct))
                    : Results.Ok(await TopicViewAsync(db, topic!, ct));
            })
            .WithName("ResumeRule")
            .WithSummary("Let a paused rule act again")
            .Produces<TermListDetail>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    // ── Applying input ──────────────────────────────────────────────────────────────────────

    /// <summary>Validates and applies a term list form. Null when it worked; otherwise what is wrong.</summary>
    private static string? ApplyList(ModerationTermList list, TermListInput body, ClaimsPrincipal user, DateTimeOffset now)
    {
        if (ModerationTargetNames.ParseAll(body.Targets, out var targetError) is not { } targets)
            return targetError;
        if (targets == ModerationTargets.None)
            return "Choose at least one target.";

        var wanted = new RuleActions(body.DeleteMessage, body.TimeoutMinutes, body.GroupBan, body.GroupRemove);

        if (ActionProblem(targets, wanted) is { } actionProblem)
            return actionProblem;

        if (ScopeProblem(body.Scope) is { } scopeProblem)
            return scopeProblem;

        if (body.Scope is { } scope)
        {
            list.ChannelMode = scope.ChannelMode;
            list.Channels = SerializeIds(scope.Channels);
            list.ExemptRoles = SerializeIds(scope.ExemptRoles);
            list.ExemptRolesSkipFlag = scope.ExemptRolesSkipFlag;
        }

        if (list.Source == TermListSource.Local)
        {
            var name = body.Name?.Trim() ?? string.Empty;
            if (name.Length == 0)
                return "Enter a name.";
            if (name.Length > MaxNameLength)
                return $"The name is too long (at most {MaxNameLength} characters).";

            list.Name = name;

            // No terms at all means "leave them": switching a list on or off sends only its switches.
            if (body.Terms is { } input)
            {
                if (LocalTerms(input, list.Terms, now, out var json) is { } termProblem)
                    return termProblem;

                list.Terms = json;
            }
        }
        else if (body.ExcludedTerms is not null)
        {
            var ids = StoredTerm.ParseList(list.Terms).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            list.ExcludedTerms = JsonSerializer.Serialize(
                body.ExcludedTerms.Where(ids.Contains).Distinct(StringComparer.Ordinal).ToList());
        }

        if (body.ContextMessages is { } listContext)
        {
            if (!ContextMessageCounts.IsCount(listContext))
                return "Choose how many earlier messages to send.";

            list.ContextMessages = listContext;
        }

        list.CheckPictures = body.CheckPictures;
        list.OpenReviewForEachFlag = body.OpenReviewForEachFlag;

        list.Enabled = body.Enabled;
        list.Targets = (int)targets;
        SetAction(RuleActions.Of(list), wanted, user, now,
            (actions, by, name, at) =>
            {
                list.DeleteMessage = actions.DeleteMessage;
                list.TimeoutMinutes = actions.TimeoutMinutes;
                list.GroupBan = actions.GroupBan;
                list.GroupRemove = actions.GroupRemove;
                list.ActSetByUserId = by;
                list.ActSetByUsername = name;
                list.ActSetAt = at;
            },
            list.ActSetByUserId, list.ActSetByUsername, list.ActSetAt);

        list.UpdatedAt = now;
        return null;
    }

    /// <summary>A local list's terms from the form, checked. Ids of terms already in the list are kept.</summary>
    private static string? LocalTerms(IReadOnlyList<TermInput> input, string current, DateTimeOffset now, out string json)
    {
        json = current;

        if (input.Count > MaxTerms)
            return $"A list can hold at most {MaxTerms} terms.";

        var existing = StoredTerm.ParseList(current).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var terms = new List<StoredTerm>(input.Count);

        foreach (var term in input)
        {
            var text = term.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
                continue;
            if (text.Length > MaxTermLength)
                return $"A term is too long (at most {MaxTermLength} characters): {Clip(text, 40)}…";
            if (!TermKind.IsLocal(term.Kind))
                return $"'{term.Kind}' is not a kind of term.";

            if (term.Kind == TermKind.Regex && TermMatcher.CompilePattern(text, out var regexError) is null)
                return $"The pattern {Clip(text, 40)} does not work: {regexError}";

            var id = term.Id is { Length: > 0 and <= 64 } kept && existing.Contains(kept) && terms.All(t => t.Id != kept)
                ? kept
                : Guid.CreateVersion7(now).ToString("N");

            terms.Add(term.Kind == TermKind.Regex
                ? new StoredTerm(id, TermKind.Regex, Pattern: text)
                : new StoredTerm(id, term.Kind, Text: text));
        }

        json = StoredTerm.Serialize(terms);
        return null;
    }

    private static string? ApplyTopic(ModerationTopic topic, TopicInput body, ClaimsPrincipal user, DateTimeOffset now)
    {
        var name = body.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return "Enter a name.";
        if (name.Length > MaxNameLength)
            return $"The name is too long (at most {MaxNameLength} characters).";

        var instructions = body.Instructions?.Trim() ?? string.Empty;
        if (instructions.Length == 0)
            return "Say what to catch.";
        if (instructions.Length > MaxInstructionsLength)
            return $"What to catch is too long (at most {MaxInstructionsLength} characters).";

        var sensitivity = body.Sensitivity ?? "medium";
        if (!Sensitivities.Contains(sensitivity, StringComparer.Ordinal))
            return "Choose low, medium or high sensitivity.";

        if (ModerationTargetNames.ParseAll(body.Targets, out var targetError) is not { } targets)
            return targetError;
        if (targets == ModerationTargets.None)
            return "Choose at least one target.";

        var wanted = new RuleActions(body.DeleteMessage, body.TimeoutMinutes, body.GroupBan, body.GroupRemove);

        if (ActionProblem(targets, wanted) is { } actionProblem)
            return actionProblem;

        if (ScopeProblem(body.Scope) is { } scopeProblem)
            return scopeProblem;

        if (body.Scope is { } scope)
        {
            topic.ChannelMode = scope.ChannelMode;
            topic.Channels = SerializeIds(scope.Channels);
            topic.ExemptRoles = SerializeIds(scope.ExemptRoles);
            topic.ExemptRolesSkipFlag = scope.ExemptRolesSkipFlag;
        }

        if (body.ContextMessages is { } topicContext)
        {
            if (!ContextMessageCounts.IsCount(topicContext))
                return "Choose how many earlier messages to send.";

            topic.ContextMessages = topicContext;
        }

        topic.CheckPictures = body.CheckPictures;
        topic.OpenReviewForEachFlag = body.OpenReviewForEachFlag;

        topic.Name = name;
        topic.Instructions = instructions;
        topic.Sensitivity = sensitivity;
        topic.Enabled = body.Enabled;
        topic.Targets = (int)targets;
        SetAction(RuleActions.Of(topic), wanted, user, now,
            (actions, by, n, at) =>
            {
                topic.DeleteMessage = actions.DeleteMessage;
                topic.TimeoutMinutes = actions.TimeoutMinutes;
                topic.GroupBan = actions.GroupBan;
                topic.GroupRemove = actions.GroupRemove;
                topic.ActSetByUserId = by;
                topic.ActSetByUsername = n;
                topic.ActSetAt = at;
            },
            topic.ActSetByUserId, topic.ActSetByUsername, topic.ActSetAt);

        topic.UpdatedAt = now;
        return null;
    }

    /// <summary>What a rule does beyond flagging, as one value (AutoMod design §5).</summary>
    private sealed record RuleActions(bool DeleteMessage, int? TimeoutMinutes, bool GroupBan, bool GroupRemove)
    {
        public static RuleActions None { get; } = new(false, null, false, false);

        public bool Any => DeleteMessage || TimeoutMinutes is not null || GroupBan || GroupRemove;

        public static RuleActions Of(IModerationRule rule) => new(rule.DeleteMessage, rule.TimeoutMinutes, rule.GroupBan, rule.GroupRemove);
    }

    /// <summary>
    /// Discord actions need a Discord message to act on and VRChat actions a VRChat profile
    /// (AutoMod design §5): a rule is refused an action it could never carry out.
    /// </summary>
    private static string? ActionProblem(ModerationTargets targets, RuleActions wanted)
    {
        if (wanted.TimeoutMinutes is not null && (wanted.TimeoutMinutes < 1 || wanted.TimeoutMinutes > MaxTimeoutMinutes))
            return $"A timeout must be between 1 and {MaxTimeoutMinutes} minutes.";

        if ((wanted.DeleteMessage || wanted.TimeoutMinutes is not null) && !targets.HasFlag(ModerationTargets.DiscordMessage))
            return "Deleting and timing out only work on Discord messages.";

        if ((wanted.GroupBan || wanted.GroupRemove) && (targets & ~ModerationTargets.DiscordMessage) == ModerationTargets.None)
            return "Banning and removing from the group only work on VRChat profile text.";

        return null;
    }

    /// <summary>
    /// Records who set a rule to act (M8 §2). A change to what it does names the person who made
    /// that change; going back to flag only clears the name; anything else leaves it alone.
    /// </summary>
    private static void SetAction(
        RuleActions was, RuleActions wanted, ClaimsPrincipal user, DateTimeOffset now,
        Action<RuleActions, Guid?, string?, DateTimeOffset?> apply,
        Guid? setBy, string? setByName, DateTimeOffset? setAt)
    {
        if (!wanted.Any)
        {
            apply(RuleActions.None, null, null, null);
            return;
        }

        if (was != wanted || setBy is null)
        {
            apply(wanted, ModbotAuth.UserIdOf(user), Clip(user.Identity?.Name ?? string.Empty, 64), now);
            return;
        }

        apply(wanted, setBy, setByName, setAt);
    }

    private static string? ScopeProblem(RuleScope? scope)
    {
        if (scope is null)
            return null;

        if (!ChannelScope.IsMode(scope.ChannelMode))
            return "Choose which channels this rule runs in.";

        var channels = scope.Channels ?? [];
        var roles = scope.ExemptRoles ?? [];

        if (scope.ChannelMode != ChannelScope.All && channels.Count == 0)
            return "Choose at least one channel.";
        if (channels.Count > MaxScopeIds)
            return $"A rule can name at most {MaxScopeIds} channels.";
        if (roles.Count > MaxScopeIds)
            return $"A rule can name at most {MaxScopeIds} roles.";
        if (channels.Concat(roles).Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 32))
            return "That is not a Discord id.";

        return null;
    }

    private static string SerializeIds(IReadOnlyList<string>? ids)
        => JsonSerializer.Serialize((ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToList());

    /// <summary>The acting gate and the trial, once the form has been applied (design §12.4, §13.1).</summary>
    /// <param name="Problem">Why the rule may not start acting, or null.</param>
    /// <param name="Overridden">The operator went ahead without a passing test run.</param>
    private sealed record ActingCheck(string? Problem, bool Overridden)
    {
        public static ActingCheck Fine { get; } = new(null, false);
    }

    /// <summary>
    /// Stops a rule going from flag only to acting until its test set says it is safe, and starts
    /// the trial when it does (design §12.4 and §13.1).
    /// </summary>
    /// <remarks>
    /// The gate is checked against the version the rule is on <em>after</em> the form was applied,
    /// so a save that changes the rule's text and sets it to act in one go is refused: the run that
    /// would have let it act was a run of a different rule.
    /// </remarks>
    private static async Task<ActingCheck> ActingAsync(
        ModbotContext db, IModerationRule rule, bool wasActing, int? trialDays, bool actWithoutTest,
        DateTimeOffset now, CancellationToken ct)
    {
        var willAct = RuleGuards.WantsAction(rule);

        if (!willAct)
        {
            // Back to flag only: nothing to trial, nothing to pause.
            SetTrial(rule, null, RuleGuards.DefaultTrialDays, clearPause: true);
            return ActingCheck.Fine;
        }

        if (wasActing)
            return ActingCheck.Fine;

        var overridden = false;

        if (actWithoutTest)
        {
            overridden = true;
        }
        else if (await AutoModRuleHistory.WhyCannotActAsync(db, rule.Id, rule.Version, ct) is { } why)
        {
            return new ActingCheck(why, false);
        }

        SetTrial(rule, now, Math.Clamp(trialDays ?? RuleGuards.DefaultTrialDays, RuleGuards.MinTrialDays, RuleGuards.MaxTrialDays), clearPause: true);
        return new ActingCheck(null, overridden);
    }

    private static void SetTrial(IModerationRule rule, DateTimeOffset? startedAt, int days, bool clearPause)
    {
        switch (rule)
        {
            case ModerationTermList list:
                list.TrialStartedAt = startedAt;
                list.TrialDays = days;
                list.TrialEndedAt = null;
                list.TrialEndedByUserId = null;
                list.TrialEndedByUsername = null;
                if (clearPause)
                {
                    list.PausedAt = null;
                    list.PausedReason = null;
                }

                break;

            case ModerationTopic topic:
                topic.TrialStartedAt = startedAt;
                topic.TrialDays = days;
                topic.TrialEndedAt = null;
                topic.TrialEndedByUserId = null;
                topic.TrialEndedByUsername = null;
                if (clearPause)
                {
                    topic.PausedAt = null;
                    topic.PausedReason = null;
                }

                break;
        }
    }

    private static string? ApplySample(ModerationTestSample sample, TestSampleInput body, DateTimeOffset now)
    {
        var text = body.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return "Enter some text.";
        if (text.Length > MaxSampleLength)
            return $"The text is too long (at most {MaxSampleLength} characters).";

        var note = body.Note?.Trim();
        if (note is { Length: > MaxSampleNoteLength })
            return $"The note is too long (at most {MaxSampleNoteLength} characters).";

        if (ModerationTargetNames.Parse(body.Target) is null)
            return "Choose what kind of text this is.";

        sample.Text = text;
        sample.ShouldFlag = body.ShouldFlag;
        sample.Note = string.IsNullOrEmpty(note) ? null : note;
        sample.Target = body.Target!;
        sample.UpdatedAt = now;

        if (sample.CreatedAt == default)
            sample.CreatedAt = now;

        return null;
    }

    // ── Test sets ───────────────────────────────────────────────────────────────────────────

    /// <summary>Either kind of rule, or null when there is no such rule of that kind.</summary>
    private static async Task<IModerationRule?> FindRuleAsync(ModbotContext db, string kind, Guid id, CancellationToken ct)
    {
        if (!ModerationRuleKind.IsKind(kind))
            return null;

        return kind == ModerationRuleKind.TermList
            ? await db.ModerationTermLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct)
            : await db.ModerationTopics.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    private static async Task<IReadOnlyList<TestSampleView>> SamplesAsync(ModbotContext db, Guid ruleId, CancellationToken ct)
        => [.. (await db.ModerationTestSamples.AsNoTracking()
                .Where(s => s.RuleId == ruleId)
                .OrderBy(s => s.CreatedAt)
                .ToListAsync(ct))
            .Select(SampleView)];

    private static async Task<IReadOnlyList<TestRunView>> RunsAsync(ModbotContext db, Guid ruleId, CancellationToken ct)
        => [.. (await db.ModerationTestRuns.AsNoTracking()
                .Where(r => r.RuleId == ruleId)
                .OrderByDescending(r => r.RanAt)
                .Take(RunsShown)
                .ToListAsync(ct))
            .Select(RunView)];

    private static TestSampleView SampleView(ModerationTestSample s)
        => new(s.Id, s.Text, s.ShouldFlag, s.Note, s.Target, s.Seeded);

    private static TestRunView RunView(ModerationTestRun r) => new(
        r.Id, r.RanAt, r.Model, r.RuleVersion, r.Samples, r.ShouldFlagCount, r.Caught, r.Missed,
        r.ShouldNotFlagCount, r.WronglyFlagged, r.AiSkipped, r.RanByUsername, RunResults(r.Results));

    private static IReadOnlyList<TestRunSampleView> RunResults(string? json)
    {
        if (JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) is not JsonArray rows)
            return [];

        var results = new List<TestRunSampleView>(rows.Count);

        foreach (var row in rows)
        {
            if (row is not JsonObject o)
                continue;

            results.Add(new TestRunSampleView(
                Guid.TryParse(o["sampleId"]?.GetValue<string>(), out var id) ? id : Guid.Empty,
                o["text"]?.GetValue<string>() ?? string.Empty,
                o["shouldFlag"]?.GetValue<bool>() ?? false,
                o["note"]?.GetValue<string>(),
                o["target"]?.GetValue<string>() ?? string.Empty,
                o["flagged"]?.GetValue<bool>() ?? false,
                o["term"]?.GetValue<string>(),
                o["matched"]?.GetValue<string>(),
                o["reason"]?.GetValue<string>()));
        }

        return results;
    }

    // ── Views ───────────────────────────────────────────────────────────────────────────────

    private static async Task<AutoModResponse> ViewAsync(ModbotContext db, IAiClients ai, IModbotClock clock, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);
        var lists = await db.ModerationTermLists.AsNoTracking().OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var topics = await db.ModerationTopics.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(ct);
        var extras = await ExtrasAsync(db, [.. lists.Cast<IModerationRule>(), .. topics], ct);
        var chat = await ai.GetChatAsync(ct);
        var tools = AutoModAiTools.Parse(settings.AutoModAiTools);

        return new AutoModResponse(
            settings.AutoModEnabled,
            settings.AiModerationDailyCallLimit,
            AiCallAllowance.UsedToday(settings, clock.UtcNow),
            chat is not null,
            [.. lists.Select(l => ListView(l, extras))],
            [.. topics.Select(t => TopicViewOf(t, extras))],
            chat is not null && await ReadsPicturesAsync(db, chat.Model, ct),
            ContextMessageCounts.All,
            // The same setting the AI tab's Base card reads: the AI section shows only while it is on.
            settings.AiEnabled,
            [.. AutoModAiTools.All.Select(t => new AiToolView(t.Name, t.Label, AutoModAiTools.IsOn(tools, t.Name)))]);
    }

    /// <summary>
    /// Everything a rule's card shows that is not on the rule row: its flags, its trial so far and
    /// its test set (design §12, §13).
    /// </summary>
    private sealed record RuleExtras(
        IReadOnlyDictionary<Guid, RuleStats> Stats,
        IReadOnlyDictionary<Guid, RuleTrial> Trials,
        IReadOnlyDictionary<Guid, int> Samples,
        IReadOnlyDictionary<Guid, ModerationTestRun> LastRuns)
    {
        public static RuleExtras None { get; } = new(
            new Dictionary<Guid, RuleStats>(),
            new Dictionary<Guid, RuleTrial>(),
            new Dictionary<Guid, int>(),
            new Dictionary<Guid, ModerationTestRun>());
    }

    private static async Task<RuleExtras> ExtrasAsync(ModbotContext db, IReadOnlyList<IModerationRule> rules, CancellationToken ct)
    {
        var ids = rules.Select(r => r.Id).ToList();
        if (ids.Count == 0)
            return RuleExtras.None;

        var stats = await StatsAsync(db, ids, ct);

        var samples = (await db.ModerationTestSamples.AsNoTracking()
                .Where(s => ids.Contains(s.RuleId))
                .GroupBy(s => s.RuleId)
                .Select(g => new { RuleId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(r => r.RuleId, r => r.Count);

        // The newest run of each rule, in two translatable queries rather than one per rule.
        var newest = await db.ModerationTestRuns.AsNoTracking()
            .Where(r => ids.Contains(r.RuleId))
            .GroupBy(r => r.RuleId)
            .Select(g => new { RuleId = g.Key, RanAt = g.Max(r => r.RanAt) })
            .ToListAsync(ct);

        var lastRuns = new Dictionary<Guid, ModerationTestRun>();
        if (newest.Count > 0)
        {
            var times = newest.Select(n => n.RanAt).Distinct().ToList();
            var rows = await db.ModerationTestRuns.AsNoTracking()
                .Where(r => ids.Contains(r.RuleId) && times.Contains(r.RanAt))
                .ToListAsync(ct);

            foreach (var n in newest)
            {
                if (rows.Find(r => r.RuleId == n.RuleId && r.RanAt == n.RanAt) is { } row)
                    lastRuns[n.RuleId] = row;
            }
        }

        // One small query for each rule that is actually in a trial, and none at all otherwise.
        var trials = new Dictionary<Guid, RuleTrial>();
        foreach (var rule in rules.Where(RuleGuards.InTrial))
        {
            var started = rule.TrialStartedAt!.Value;

            var counts = await db.ModerationFlags.AsNoTracking()
                .Where(f => f.RuleId == rule.Id && f.Trial && f.FlaggedAt >= started)
                .GroupBy(f => f.RuleId)
                .Select(g => new
                {
                    Flags = g.Count(),
                    Delete = g.Count(f => f.WouldDeleteMessage),
                    Timeout = g.Count(f => f.WouldTimeOutMinutes != null),
                    Ban = g.Count(f => f.WouldGroupBan),
                    Remove = g.Count(f => f.WouldGroupRemove),
                    Dismissed = g.Count(f => f.State == ModerationFlagState.Dismissed),
                })
                .FirstOrDefaultAsync(ct);

            trials[rule.Id] = new RuleTrial(
                started,
                rule.TrialDays,
                RuleGuards.TrialEndsAt(rule) ?? started,
                counts?.Flags ?? 0,
                counts?.Delete ?? 0,
                counts?.Timeout ?? 0,
                counts?.Dismissed ?? 0,
                counts?.Ban ?? 0,
                counts?.Remove ?? 0);
        }

        return new RuleExtras(stats, trials, samples, lastRuns);
    }

    private static async Task<TermListDetail> DetailAsync(ModbotContext db, ModerationTermList list, CancellationToken ct)
        => Detail(list, await ExtrasAsync(db, [list], ct));

    private static async Task<TopicView> TopicViewAsync(ModbotContext db, ModerationTopic topic, CancellationToken ct)
        => TopicViewOf(topic, await ExtrasAsync(db, [topic], ct));

    /// <summary>Whether the model in use reads pictures, as the model list last said (design §17).</summary>
    /// <remarks>
    /// A model the catalogue has never heard of reads none, as far as this page is concerned:
    /// offering a box that cannot work is worse than not offering it.
    /// </remarks>
    private static async Task<bool> ReadsPicturesAsync(ModbotContext db, string model, CancellationToken ct)
    {
        var modalities = await db.AiCatalogModels.AsNoTracking()
            .Where(m => m.Model == model)
            .Select(m => m.InputModalities)
            .FirstOrDefaultAsync(ct);

        return modalities is not null && modalities.Contains("image", StringComparer.OrdinalIgnoreCase);
    }

    private static RuleScope ScopeOf(IModerationRule rule) => new(
        rule.ChannelMode, RuleGuards.Ids(rule.Channels), RuleGuards.Ids(rule.ExemptRoles), rule.ExemptRolesSkipFlag);

    private static RulePause? PauseOf(IModerationRule rule)
        => rule.PausedAt is { } at ? new RulePause(at, rule.PausedReason) : null;

    private static RuleTestSummary TestsOf(IModerationRule rule, RuleExtras extras)
    {
        var run = extras.LastRuns.GetValueOrDefault(rule.Id);

        return new RuleTestSummary(
            extras.Samples.GetValueOrDefault(rule.Id),
            run?.RanAt,
            run?.Model,
            run?.Caught,
            run?.ShouldFlagCount,
            run?.WronglyFlagged,
            run is { Samples: > 0, WronglyFlagged: 0 } && run.RuleVersion == rule.Version);
    }

    /// <summary>
    /// Each rule's flags, dismissals and confirmations, whole and broken down by language
    /// (M8 §4.4, AI moderation design §18 and §19).
    /// </summary>
    /// <remarks>
    /// One query grouped by rule and language, summed here for the whole-rule figures: a rule whose
    /// dismissal rate is fine in English and terrible in Russian reads as fine until it is split.
    /// </remarks>
    private static async Task<Dictionary<Guid, RuleStats>> StatsAsync(ModbotContext db, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var query = db.ModerationFlags.AsNoTracking();
        if (ids is not null)
            query = query.Where(f => ids.Contains(f.RuleId));

        var rows = await query
            .GroupBy(f => new { f.RuleId, f.Language })
            .Select(g => new
            {
                g.Key.RuleId,
                g.Key.Language,
                Flags = g.Count(),
                Dismissed = g.Count(f => f.State == ModerationFlagState.Dismissed),
                Confirmed = g.Count(f => f.State == ModerationFlagState.Confirmed),
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.RuleId)
            .ToDictionary(g => g.Key, g => new RuleStats(
                g.Sum(r => r.Flags),
                g.Sum(r => r.Dismissed),
                g.Sum(r => r.Confirmed),
                [.. g.OrderByDescending(r => r.Flags)
                    .Select(r => new RuleLanguageStats(
                        r.Language, LanguageNames.Label(r.Language), r.Flags, r.Dismissed, r.Confirmed))]));
    }

    private static TermListView ListView(ModerationTermList l, RuleExtras extras)
    {
        var terms = StoredTerm.ParseList(l.Terms);
        var excluded = StoredTerm.ParseIds(l.ExcludedTerms);

        HubListChanges? changes = null;
        if (l.HubAvailableChanges is not null)
        {
            try
            {
                changes = JsonSerializer.Deserialize<HubListChanges>(l.HubAvailableChanges, StoredTerm.Json);
            }
            catch (JsonException)
            {
                changes = null;
            }
        }

        return new TermListView(
            l.Id, l.Name, l.Source, l.Enabled, ModerationTargetNames.NamesOf((ModerationTargets)l.Targets),
            l.DeleteMessage, l.TimeoutMinutes, l.ActSetByUsername, l.ActSetAt,
            terms.Count, excluded.Count(id => terms.Any(t => t.Id == id)),
            l.HubId, l.HubVersion, l.HubFetchedAt, l.HubAvailableVersion, changes, l.HubError,
            extras.Stats.GetValueOrDefault(l.Id) ?? new RuleStats(0, 0),
            l.Version,
            RuleGuards.ActsNow(l),
            ScopeOf(l),
            extras.Trials.GetValueOrDefault(l.Id),
            PauseOf(l),
            TestsOf(l, extras),
            l.ContextMessages,
            l.CheckPictures,
            l.OpenReviewForEachFlag,
            l.GroupBan,
            l.GroupRemove);
    }

    private static TermListDetail Detail(ModerationTermList l, RuleExtras extras)
    {
        var excluded = StoredTerm.ParseIds(l.ExcludedTerms).ToHashSet(StringComparer.Ordinal);

        return new TermListDetail(
            ListView(l, extras),
            [.. StoredTerm.ParseList(l.Terms).Select(t => new TermView(
                t.Id, t.Kind, t.Text, t.Pattern, t.Label, t.Category, t.Note, excluded.Contains(t.Id)))]);
    }

    private static TopicView TopicViewOf(ModerationTopic t, RuleExtras extras) => new(
        t.Id, t.Name, t.Instructions, t.Sensitivity, t.Enabled, ModerationTargetNames.NamesOf((ModerationTargets)t.Targets),
        t.DeleteMessage, t.TimeoutMinutes, t.ActSetByUsername, t.ActSetAt,
        extras.Stats.GetValueOrDefault(t.Id) ?? new RuleStats(0, 0),
        t.Version,
        RuleGuards.ActsNow(t),
        ScopeOf(t),
        extras.Trials.GetValueOrDefault(t.Id),
        PauseOf(t),
        TestsOf(t, extras),
        t.ContextMessages,
        t.CheckPictures,
        t.OpenReviewForEachFlag,
        t.GroupBan,
        t.GroupRemove);

    // ── Facts ───────────────────────────────────────────────────────────────────────────────

    private static JsonObject ListData(ModerationTermList l) => new()
    {
        ["source"] = l.Source,
        ["hubId"] = l.HubId,
        ["hubVersion"] = l.HubVersion,
        ["enabled"] = l.Enabled,
        ["targets"] = new JsonArray([.. ModerationTargetNames.NamesOf((ModerationTargets)l.Targets).Select(n => JsonValue.Create(n))]),
        ["termCount"] = StoredTerm.ParseList(l.Terms).Count,
        ["excludedTerms"] = JsonNode.Parse(l.ExcludedTerms),
        ["contextMessages"] = l.ContextMessages,
        ["checkPictures"] = l.CheckPictures,
        ["openReviewForEachFlag"] = l.OpenReviewForEachFlag,
        ["deleteMessage"] = l.DeleteMessage,
        ["timeoutMinutes"] = l.TimeoutMinutes,
        ["groupBan"] = l.GroupBan,
        ["groupRemove"] = l.GroupRemove,
    };

    private static JsonObject TopicData(ModerationTopic t) => new()
    {
        ["instructions"] = t.Instructions,
        ["sensitivity"] = t.Sensitivity,
        ["enabled"] = t.Enabled,
        ["targets"] = new JsonArray([.. ModerationTargetNames.NamesOf((ModerationTargets)t.Targets).Select(n => JsonValue.Create(n))]),
        ["contextMessages"] = t.ContextMessages,
        ["checkPictures"] = t.CheckPictures,
        ["openReviewForEachFlag"] = t.OpenReviewForEachFlag,
        ["deleteMessage"] = t.DeleteMessage,
        ["timeoutMinutes"] = t.TimeoutMinutes,
        ["groupBan"] = t.GroupBan,
        ["groupRemove"] = t.GroupRemove,
    };

    private static async Task RuleChangedAsync(
        HttpContext http,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        string ruleKind,
        Guid? ruleId,
        string ruleName,
        string change,
        JsonObject data,
        CancellationToken ct)
    {
        if (ModbotAuth.UserIdOf(http.User) is not { } userId)
            return;

        var now = clock.UtcNow;
        await partitions.EnsureForAsync(now, ct);

        data["ruleKind"] = ruleKind;
        data["ruleId"] = ruleId?.ToString();
        data["ruleName"] = ruleName;
        data["change"] = change;
        data["username"] = http.User.Identity?.Name;

        await facts.WriteAsync(new FactRecord
        {
            Type = FactType.AutoModRuleChanged,
            OccurredAt = now,
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = userId.ToString(),
            ActorPlatform = FactPlatform.Modbot,
            ActorId = userId.ToString(),
            Source = FactSource.Modbot,
            Data = data,
        }, ct);
    }

    /// <summary>
    /// Records that an operator let a rule act without a passing test run (design §12.4).
    /// </summary>
    /// <remarks>
    /// The override is allowed — the operator owns the group — but it is not quiet. This is the
    /// fact somebody reads afterwards when a rule that was never tried deleted the wrong thing.
    /// </remarks>
    private static Task OverrideFactAsync(
        HttpContext http,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        string ruleKind,
        IModerationRule rule,
        ActingCheck check,
        CancellationToken ct)
    {
        if (!check.Overridden)
            return Task.CompletedTask;

        return RuleChangedAsync(http, facts, partitions, clock, ruleKind, rule.Id, rule.Name, "act-without-test", new JsonObject
        {
            ["ruleVersion"] = rule.Version,
            ["deleteMessage"] = rule.DeleteMessage,
            ["timeoutMinutes"] = rule.TimeoutMinutes,
            ["groupBan"] = rule.GroupBan,
            ["groupRemove"] = rule.GroupRemove,
            ["trialStartedAt"] = rule.TrialStartedAt?.ToString("O"),
            ["trialDays"] = rule.TrialDays,
        }, ct);
    }

    private static IResult Error(string message) => Results.BadRequest(new { error = message });

    private static IResult NotFound(string message) => Results.NotFound(new { error = message });

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
