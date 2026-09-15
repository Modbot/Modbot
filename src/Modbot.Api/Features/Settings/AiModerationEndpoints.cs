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
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Moderation;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>
/// Settings → AI → Moderation: term lists, Hub subscriptions, AI topics, the switch, the daily AI
/// call limit and "Try it" (AI moderation design).
/// </summary>
/// <remarks>
/// Every change to a rule writes a <c>modbot.ai-moderation.rule.change</c> fact naming the account,
/// and a rule set to act remembers who set it, because M8 §2 requires every action to name the
/// operator who allowed it.
/// </remarks>
public static class AiModerationEndpoints
{
    public const int MaxNameLength = 100;
    public const int MaxTermLength = 200;
    public const int MaxTerms = 5000;
    public const int MaxInstructionsLength = 2000;
    public const int MaxTryLength = 4000;
    public const int MaxDailyAiCalls = 100_000;

    /// <summary>Discord's own longest timeout: 28 days.</summary>
    public const int MaxTimeoutMinutes = 28 * 24 * 60;

    private static readonly string[] Sensitivities = ["low", "medium", "high"];

    public static IEndpointRouteBuilder MapAiModerationSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/ai/moderation").WithTags("AI settings");

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                [FromServices] IModbotClock clock,
                CancellationToken ct) => Results.Ok(await ViewAsync(db, ai, clock, ct)))
            .WithName("GetAiModeration")
            .WithSummary("Term lists, AI topics, the switch and the daily AI call limit")
            .Produces<AiModerationResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] AiModerationUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.DailyAiCallLimit is < 0 or > MaxDailyAiCalls)
                    return Error($"The daily AI call limit must be between 0 and {MaxDailyAiCalls}.");

                var settings = await db.GetSettingsAsync(ct);
                var switched = settings.AiModerationEnabled != body.Enabled;

                settings.AiModerationEnabled = body.Enabled;
                settings.AiModerationDailyCallLimit = body.DailyAiCallLimit;
                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, "settings", null, "AI moderation",
                    switched ? (body.Enabled ? "switched-on" : "switched-off") : "changed",
                    new JsonObject { ["enabled"] = body.Enabled, ["dailyAiCallLimit"] = body.DailyAiCallLimit }, ct);

                return Results.Ok(await ViewAsync(db, ai, clock, ct));
            })
            .WithName("SetAiModeration")
            .WithSummary("Switch AI moderation on or off and set the daily AI call limit")
            .Produces<AiModerationResponse>()
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

                var stats = await StatsAsync(db, [id], ct);
                return Results.Ok(Detail(list, stats));
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

                db.ModerationTermLists.Add(list);
                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list.Id, list.Name, "created", ListData(list), ct);

                return Results.Ok(Detail(list, new Dictionary<Guid, RuleStats>()));
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
                if (ApplyList(list, body, http.User, now) is { } problem)
                    return Error(problem);

                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list.Id, list.Name, "changed", ListData(list), ct);

                return Results.Ok(Detail(list, await StatsAsync(db, [id], ct)));
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

                db.ModerationTermLists.Add(list);
                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list.Id, list.Name, "added-from-hub", ListData(list), ct);

                return Results.Ok(Detail(list, new Dictionary<Guid, RuleStats>()));
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

                return Results.Ok(Detail(list, await StatsAsync(db, [id], ct)));
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
                if (!updates.Apply(list))
                    return Error("There is no newer version to apply.");

                await db.SaveChangesAsync(ct);

                var data = ListData(list);
                data["changes"] = changes is null ? null : JsonNode.Parse(changes);
                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.TermList, list.Id, list.Name, "updated-from-hub", data, ct);

                return Results.Ok(Detail(list, await StatsAsync(db, [id], ct)));
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

                db.ModerationTopics.Add(topic);
                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.Topic, topic.Id, topic.Name, "created", TopicData(topic), ct);

                return Results.Ok(TopicViewOf(topic, new Dictionary<Guid, RuleStats>()));
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

                if (ApplyTopic(topic, body, http.User, clock.UtcNow) is { } problem)
                    return Error(problem);

                await db.SaveChangesAsync(ct);

                await RuleChangedAsync(http, facts, partitions, clock, ModerationRuleKind.Topic, topic.Id, topic.Name, "changed", TopicData(topic), ct);

                return Results.Ok(TopicViewOf(topic, await StatsAsync(db, [id], ct)));
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

                var result = await engine.TryAsync(body.Text, target, body.IncludeAi, ct);

                return Results.Ok(new TryResponse(
                    [.. result.Matches.Select(m => new TryMatchView(
                        m.Match.RuleKind, m.Match.RuleId, m.Match.RuleName, m.RuleEnabled, m.Match.Term, m.Match.Matched,
                        m.Match.Reason, m.Match.DeleteMessage, m.Match.TimeoutMinutes))],
                    result.WouldDeleteMessage,
                    result.WouldTimeOutMinutes,
                    result.AiSkipped));
            })
            .WithName("TryAiModeration")
            .WithSummary("Check some text against every rule. Nothing is recorded and nothing is done.")
            .Produces<TryResponse>()
            .Produces(StatusCodes.Status400BadRequest)
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

        if (ActionProblem(targets, body.DeleteMessage, body.TimeoutMinutes) is { } actionProblem)
            return actionProblem;

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

        list.Enabled = body.Enabled;
        list.Targets = (int)targets;
        SetAction(list.DeleteMessage, list.TimeoutMinutes, body.DeleteMessage, body.TimeoutMinutes, user, now,
            (d, m, by, name, at) =>
            {
                list.DeleteMessage = d;
                list.TimeoutMinutes = m;
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

        if (ActionProblem(targets, body.DeleteMessage, body.TimeoutMinutes) is { } actionProblem)
            return actionProblem;

        topic.Name = name;
        topic.Instructions = instructions;
        topic.Sensitivity = sensitivity;
        topic.Enabled = body.Enabled;
        topic.Targets = (int)targets;
        SetAction(topic.DeleteMessage, topic.TimeoutMinutes, body.DeleteMessage, body.TimeoutMinutes, user, now,
            (d, m, by, n, at) =>
            {
                topic.DeleteMessage = d;
                topic.TimeoutMinutes = m;
                topic.ActSetByUserId = by;
                topic.ActSetByUsername = n;
                topic.ActSetAt = at;
            },
            topic.ActSetByUserId, topic.ActSetByUsername, topic.ActSetAt);

        topic.UpdatedAt = now;
        return null;
    }

    private static string? ActionProblem(ModerationTargets targets, bool delete, int? minutes)
    {
        if (minutes is not null && (minutes < 1 || minutes > MaxTimeoutMinutes))
            return $"A timeout must be between 1 and {MaxTimeoutMinutes} minutes.";

        if ((delete || minutes is not null) && !targets.HasFlag(ModerationTargets.DiscordMessage))
            return "Deleting and timing out only work on Discord messages.";

        return null;
    }

    /// <summary>
    /// Records who set a rule to act (M8 §2). A change to what it does names the person who made
    /// that change; going back to flag only clears the name; anything else leaves it alone.
    /// </summary>
    private static void SetAction(
        bool wasDelete, int? wasMinutes, bool delete, int? minutes, ClaimsPrincipal user, DateTimeOffset now,
        Action<bool, int?, Guid?, string?, DateTimeOffset?> apply,
        Guid? setBy, string? setByName, DateTimeOffset? setAt)
    {
        var acts = delete || minutes is not null;

        if (!acts)
        {
            apply(false, null, null, null, null);
            return;
        }

        if (wasDelete != delete || wasMinutes != minutes || setBy is null)
        {
            apply(delete, minutes, ModbotAuth.UserIdOf(user), Clip(user.Identity?.Name ?? string.Empty, 64), now);
            return;
        }

        apply(delete, minutes, setBy, setByName, setAt);
    }

    // ── Views ───────────────────────────────────────────────────────────────────────────────

    private static async Task<AiModerationResponse> ViewAsync(ModbotContext db, IAiClients ai, IModbotClock clock, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);
        var lists = await db.ModerationTermLists.AsNoTracking().OrderBy(l => l.CreatedAt).ToListAsync(ct);
        var topics = await db.ModerationTopics.AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync(ct);
        var stats = await StatsAsync(db, null, ct);
        var aiReady = await ai.GetChatAsync(ct) is not null;

        return new AiModerationResponse(
            settings.AiModerationEnabled,
            settings.AiModerationDailyCallLimit,
            AiCallAllowance.UsedToday(settings, clock.UtcNow),
            aiReady,
            [.. lists.Select(l => ListView(l, stats))],
            [.. topics.Select(t => TopicViewOf(t, stats))]);
    }

    private static async Task<Dictionary<Guid, RuleStats>> StatsAsync(ModbotContext db, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var query = db.ModerationFlags.AsNoTracking();
        if (ids is not null)
            query = query.Where(f => ids.Contains(f.RuleId));

        var rows = await query
            .GroupBy(f => f.RuleId)
            .Select(g => new { RuleId = g.Key, Flags = g.Count(), Dismissed = g.Count(f => f.State == ModerationFlagState.Dismissed) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.RuleId, r => new RuleStats(r.Flags, r.Dismissed));
    }

    private static TermListView ListView(ModerationTermList l, IReadOnlyDictionary<Guid, RuleStats> stats)
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
            stats.GetValueOrDefault(l.Id) ?? new RuleStats(0, 0));
    }

    private static TermListDetail Detail(ModerationTermList l, IReadOnlyDictionary<Guid, RuleStats> stats)
    {
        var excluded = StoredTerm.ParseIds(l.ExcludedTerms).ToHashSet(StringComparer.Ordinal);

        return new TermListDetail(
            ListView(l, stats),
            [.. StoredTerm.ParseList(l.Terms).Select(t => new TermView(
                t.Id, t.Kind, t.Text, t.Pattern, t.Label, t.Category, t.Note, excluded.Contains(t.Id)))]);
    }

    private static TopicView TopicViewOf(ModerationTopic t, IReadOnlyDictionary<Guid, RuleStats> stats) => new(
        t.Id, t.Name, t.Instructions, t.Sensitivity, t.Enabled, ModerationTargetNames.NamesOf((ModerationTargets)t.Targets),
        t.DeleteMessage, t.TimeoutMinutes, t.ActSetByUsername, t.ActSetAt,
        stats.GetValueOrDefault(t.Id) ?? new RuleStats(0, 0));

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
        ["deleteMessage"] = l.DeleteMessage,
        ["timeoutMinutes"] = l.TimeoutMinutes,
    };

    private static JsonObject TopicData(ModerationTopic t) => new()
    {
        ["instructions"] = t.Instructions,
        ["sensitivity"] = t.Sensitivity,
        ["enabled"] = t.Enabled,
        ["targets"] = new JsonArray([.. ModerationTargetNames.NamesOf((ModerationTargets)t.Targets).Select(n => JsonValue.Create(n))]),
        ["deleteMessage"] = t.DeleteMessage,
        ["timeoutMinutes"] = t.TimeoutMinutes,
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
            Type = FactType.AiModerationRuleChanged,
            OccurredAt = now,
            SubjectPlatform = FactPlatform.Modbot,
            SubjectId = userId.ToString(),
            ActorPlatform = FactPlatform.Modbot,
            ActorId = userId.ToString(),
            Source = FactSource.Modbot,
            Data = data,
        }, ct);
    }

    private static IResult Error(string message) => Results.BadRequest(new { error = message });

    private static IResult NotFound(string message) => Results.NotFound(new { error = message });

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
