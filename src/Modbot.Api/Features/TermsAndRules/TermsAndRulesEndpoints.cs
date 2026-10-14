using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.TermsAndRules;

/// <summary>
/// The Terms and rules page: the words the community uses with what they mean, and the rules for
/// members, typed by staff and answered by <c>/term</c> and <c>/rule</c> in Discord.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Staff only, and nothing leaves Modbot but through the commands.</strong> Seeing needs a
/// signed-in account, since the page is a reference for the whole team. Changing needs
/// <see cref="ModbotPermissions.ManageSettings"/> (Change settings): the commands repeat these words
/// to every member, so who may edit them is a setting of the server. Nothing here calls VRChat or
/// Discord.
/// </para>
/// <para>
/// Each change is recorded as a fact naming the term or the rule and what was done, never the
/// meaning or the text, so the log holds no copy of them once one is deleted.
/// </para>
/// <para>
/// The limits are the ones in <see cref="GlossaryTerm"/> and <see cref="ServerRule"/>, which keep a
/// <c>/term</c> or <c>/rule</c> answer inside one Discord message; they are enforced here, at save.
/// </para>
/// </remarks>
public static class TermsAndRulesEndpoints
{
    public static IEndpointRouteBuilder MapTermsAndRules(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapTerms(app.MapGroup("/api/terms").WithTags("Terms and rules").RequireAuthorization());
        MapRules(app.MapGroup("/api/rules").WithTags("Terms and rules").RequireAuthorization());

        return app;
    }

    private static bool CanEdit(HttpContext http)
    {
        var held = ModbotAuth.PermissionsOf(http.User);
        return held.HasFlag(ModbotPermissions.Administrator) || held.HasFlag(ModbotPermissions.ManageSettings);
    }

    // ── Terms ────────────────────────────────────────────────────────────────────────────────

    private static void MapTerms(RouteGroupBuilder group)
    {
        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var terms = await db.GlossaryTerms.AsNoTracking().ToListAsync(ct);

                return Results.Ok(new TermsView(
                    [.. terms.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(View)],
                    CanEdit(http)));
            })
            .WithName("ListTerms")
            .WithSummary("List terms")
            .WithDescription("Every term with its other names and meaning, by name. canEdit says whether this account may change them.")
            .Produces<TermsView>();

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] TermRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // The count and the names are read after the lock, so two adds at once take turns.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await LockAsync(db, TermsLock, ct);

                if (await db.GlossaryTerms.CountAsync(ct) >= GlossaryTerm.MaxTerms)
                    return Results.BadRequest(new { error = $"There can be at most {GlossaryTerm.MaxTerms} terms." });

                var term = new GlossaryTerm { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };

                if (await ApplyAsync(db, term, body, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                term.UpdatedAt = term.CreatedAt;

                db.GlossaryTerms.Add(term);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.TermChanged, term.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = term.Name, ["action"] = "added" }, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(term));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("CreateTerm")
            .WithSummary("Add term")
            .WithDescription("Add a term. A refusal (400) says what is wrong in error.")
            .Produces<TermView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] TermRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await LockAsync(db, TermsLock, ct);

                var term = await db.GlossaryTerms.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (term is null)
                    return Results.NotFound();

                var before = (term.Name, string.Join('\n', term.OtherNames), term.Meaning);

                if (await ApplyAsync(db, term, body, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                if (before == (term.Name, string.Join('\n', term.OtherNames), term.Meaning))
                    return Results.Ok(View(term));

                term.UpdatedAt = clock.UtcNow;

                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.TermChanged, term.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = term.Name, ["action"] = "changed" }, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(term));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("UpdateTerm")
            .WithSummary("Update term")
            .WithDescription("Replace a term's name, other names and meaning. A refusal (400) says what is wrong in error.")
            .Produces<TermView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                var term = await db.GlossaryTerms.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (term is null)
                    return Results.NotFound();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.GlossaryTerms.Remove(term);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.TermChanged, term.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["name"] = term.Name, ["action"] = "deleted" }, ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("DeleteTerm")
            .WithSummary("Delete term")
            .WithDescription("Delete a term. /term stops answering to its names.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Copies the request onto <paramref name="term"/> after trimming it; returns what is wrong, or
    /// null. Nothing is copied when something is wrong.
    /// </summary>
    private static async Task<string?> ApplyAsync(ModbotContext db, GlossaryTerm term, TermRequest body, CancellationToken ct)
    {
        var name = Clean(body.Name);
        if (name is null)
            return "Give the term a name.";
        if (name.Length > GlossaryTerm.MaxNameLength)
            return $"The name can be at most {GlossaryTerm.MaxNameLength} characters.";
        if (OnManyLines(name))
            return "The name must be on one line.";

        var others = (body.OtherNames ?? []).Select(Clean).OfType<string>().ToList();
        if (others.Count > GlossaryTerm.MaxOtherNames)
            return $"There can be at most {GlossaryTerm.MaxOtherNames} other names.";
        if (others.Any(o => o.Length > GlossaryTerm.MaxOtherNameLength))
            return $"An other name can be at most {GlossaryTerm.MaxOtherNameLength} characters.";
        if (others.Any(OnManyLines))
            return "An other name must be on one line.";

        // Said twice, or the same as the name, means once.
        var names = new List<string> { name };
        foreach (var other in others)
        {
            if (!names.Contains(other, StringComparer.OrdinalIgnoreCase))
                names.Add(other);
        }

        var meaning = Clean(body.Meaning);
        if (meaning is null)
            return "Say what the term means.";
        if (meaning.Length > GlossaryTerm.MaxMeaningLength)
            return $"The meaning can be at most {GlossaryTerm.MaxMeaningLength} characters.";

        // /term takes a name and finds one term, so no two terms may answer to the same name.
        var rest = await db.GlossaryTerms.AsNoTracking()
            .Where(t => t.Id != term.Id)
            .Select(t => new { t.Name, t.OtherNames })
            .ToListAsync(ct);

        foreach (var used in names)
        {
            var owner = rest.FirstOrDefault(t =>
                string.Equals(t.Name, used, StringComparison.OrdinalIgnoreCase)
                || t.OtherNames.Contains(used, StringComparer.OrdinalIgnoreCase));

            if (owner is not null)
                return $"“{used}” is already a name of the term “{owner.Name}”.";
        }

        term.Name = name;
        term.OtherNames = [.. names.Skip(1)];
        term.Meaning = meaning;

        return null;
    }

    private static TermView View(GlossaryTerm t) => new(t.Id, t.Name, t.OtherNames, t.Meaning, t.UpdatedAt);

    // ── Rules ────────────────────────────────────────────────────────────────────────────────

    private static void MapRules(RouteGroupBuilder group)
    {
        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var rules = await InOrderAsync(db.ServerRules.AsNoTracking(), ct);

                return Results.Ok(new RulesView(Numbered(rules), CanEdit(http)));
            })
            .WithName("ListRules")
            .WithSummary("List rules")
            .WithDescription("Every rule in order, each with its number. canEdit says whether this account may change them.")
            .Produces<RulesView>();

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] RuleRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // The count and the last number are read after the lock, so two adds at once take turns.
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await LockAsync(db, RulesLock, ct);

                var rules = await InOrderAsync(db.ServerRules, ct);
                if (rules.Count >= ServerRule.MaxRules)
                    return Results.BadRequest(new { error = $"There can be at most {ServerRule.MaxRules} rules." });

                var rule = new ServerRule
                {
                    Id = Guid.CreateVersion7(),
                    Position = rules.Count == 0 ? 0 : rules.Max(r => r.Position) + 1,
                    CreatedAt = clock.UtcNow,
                };

                if (Apply(rule, body) is { } problem)
                    return Results.BadRequest(new { error = problem });

                rule.UpdatedAt = rule.CreatedAt;

                db.ServerRules.Add(rule);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.RuleChanged, rule.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["title"] = rule.Title, ["action"] = "added" }, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(new RuleView(rule.Id, rules.Count + 1, rule.Title, rule.Text, rule.UpdatedAt));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("CreateRule")
            .WithSummary("Add rule")
            .WithDescription("Add a rule at the end of the list. A refusal (400) says what is wrong in error.")
            .Produces<RuleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] RuleRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var rules = await InOrderAsync(db.ServerRules, ct);
                var rule = rules.FirstOrDefault(r => r.Id == id);
                if (rule is null)
                    return Results.NotFound();

                var before = (rule.Title, rule.Text);

                if (Apply(rule, body) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var number = rules.IndexOf(rule) + 1;

                if (before == (rule.Title, rule.Text))
                    return Results.Ok(new RuleView(rule.Id, number, rule.Title, rule.Text, rule.UpdatedAt));

                rule.UpdatedAt = clock.UtcNow;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.RuleChanged, rule.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["title"] = rule.Title, ["action"] = "changed" }, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(new RuleView(rule.Id, number, rule.Title, rule.Text, rule.UpdatedAt));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("UpdateRule")
            .WithSummary("Update rule")
            .WithDescription("Replace a rule's title and text. A refusal (400) says what is wrong in error.")
            .Produces<RuleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/move", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] RuleMoveRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var step = body.Direction switch
                {
                    "up" => -1,
                    "down" => 1,
                    _ => 0,
                };

                if (step == 0)
                    return Results.BadRequest(new { error = "The direction must be up or down." });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await LockAsync(db, RulesLock, ct);

                var rules = await InOrderAsync(db.ServerRules, ct);
                var rule = rules.FirstOrDefault(r => r.Id == id);
                if (rule is null)
                    return Results.NotFound();

                var from = rules.IndexOf(rule);
                var to = from + step;

                // Already first or last: nothing to move, and nothing to record.
                if (to >= 0 && to < rules.Count)
                {
                    rules.RemoveAt(from);
                    rules.Insert(to, rule);

                    var now = clock.UtcNow;

                    for (var at = 0; at < rules.Count; at++)
                        rules[at].Position = at;

                    rule.UpdatedAt = now;

                    await db.SaveChangesAsync(ct);

                    await facts.RecordAsync(
                        FactType.RuleChanged, rule.Id.ToString(), Actor.Of(http),
                        new JsonObject { ["title"] = rule.Title, ["action"] = "moved", ["number"] = to + 1 }, ct);

                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(new RulesView(Numbered(rules), true));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("MoveRule")
            .WithSummary("Move rule")
            .WithDescription("Move a rule one place up or down; the rule it passes takes its number. Returns the list in its new order.")
            .Produces<RulesView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                var rule = await db.ServerRules.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (rule is null)
                    return Results.NotFound();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.ServerRules.Remove(rule);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.RuleChanged, rule.Id.ToString(), Actor.Of(http),
                    new JsonObject { ["title"] = rule.Title, ["action"] = "deleted" }, ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("DeleteRule")
            .WithSummary("Delete rule")
            .WithDescription("Delete a rule. The rules after it move up one number.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Copies the request onto <paramref name="rule"/> after trimming it; returns what is wrong, or
    /// null. Nothing is copied when something is wrong.
    /// </summary>
    private static string? Apply(ServerRule rule, RuleRequest body)
    {
        var title = Clean(body.Title);
        if (title is null)
            return "Give the rule a title.";
        if (title.Length > ServerRule.MaxTitleLength)
            return $"The title can be at most {ServerRule.MaxTitleLength} characters.";
        if (OnManyLines(title))
            return "The title must be on one line.";

        var text = Clean(body.Text);
        if (text is null)
            return "Write the rule.";
        if (text.Length > ServerRule.MaxTextLength)
            return $"The rule can be at most {ServerRule.MaxTextLength} characters.";

        rule.Title = title;
        rule.Text = text;

        return null;
    }

    private static Task<List<ServerRule>> InOrderAsync(IQueryable<ServerRule> rules, CancellationToken ct)
        => rules.OrderBy(r => r.Position).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);

    private static List<RuleView> Numbered(IReadOnlyList<ServerRule> inOrder)
        => [.. inOrder.Select((r, at) => new RuleView(r.Id, at + 1, r.Title, r.Text, r.UpdatedAt))];

    // ── Both ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Names the locks so two people changing the terms, or the rules, at the same moment take
    /// turns: without it both could pass the same name check or limit check, or share a place in
    /// the rules. Any stable string works; the prefix matches the other locks Modbot takes.
    /// </summary>
    private const string TermsLock = "modbot:terms:write";

    private const string RulesLock = "modbot:rules:write";

    private static Task LockAsync(ModbotContext db, string name, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({name}))", ct);

    private static string? Clean(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool OnManyLines(string text) => text.Contains('\n') || text.Contains('\r');
}
