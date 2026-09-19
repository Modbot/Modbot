using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.AI.Moderation;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reviews;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Moderation;

namespace Modbot.Api.Features.Flags;

/// <param name="SubjectPlatform"><c>vrchat</c> or <c>discord</c>.</param>
/// <param name="RuleVersion">The rule version that flagged (AI moderation design §14).</param>
/// <param name="RuleText">
/// The rule as it read at that version — the terms, or what to catch. Null when the version is not
/// recorded, which is every flag from before rule versions existed.
/// </param>
/// <param name="Trial">The rule was in its trial, so nothing was done.</param>
/// <param name="WouldDeleteMessage">What the rule would have done, during a trial or while paused.</param>
/// <param name="Language">The checked text's language as an ISO 639-3 code, or null.</param>
/// <param name="LanguageLabel">That language in words, or "Unknown".</param>
/// <param name="Picture">Which picture matched, or null when the words did.</param>
/// <param name="Context">The messages the model was given to understand this one, oldest first.</param>
/// <param name="ReviewId">The review opened for this flag, or null.</param>
/// <param name="GroupBanned">The rule banned the person from the managed VRChat group (AutoMod design §5).</param>
/// <param name="GroupRemoved">The rule removed the person from the managed VRChat group.</param>
/// <param name="AiOpinion"><c>keep</c> or <c>dismiss</c>, when a moderator asked the AI (AutoMod design §6.3). Advice only.</param>
/// <param name="AiProposedAction">What the AI proposed a moderator might do, when that tool is on. Never carried out by Modbot.</param>
public sealed record FlagView(
    Guid Id,
    DateTimeOffset FlaggedAt,
    string RuleKind,
    Guid RuleId,
    string RuleName,
    string Term,
    string Target,
    string SubjectPlatform,
    string SubjectId,
    string? SubjectName,
    string? ChannelId,
    string? MessageId,
    string Matched,
    string? Reason,
    bool MessageDeleted,
    int? TimedOutMinutes,
    string State,
    DateTimeOffset? DismissedAt,
    string? DismissedBy,
    int RuleVersion = 0,
    string? RuleText = null,
    bool Trial = false,
    bool WouldDeleteMessage = false,
    int? WouldTimeOutMinutes = null,
    // The AI call that produced it, in the call log. Null for a term list, which makes no call.
    Guid? CallId = null,
    string? Language = null,
    string LanguageLabel = LanguageNames.UnknownLabel,
    string? Picture = null,
    string? PictureUrl = null,
    IReadOnlyList<FlagContextMessage>? Context = null,
    Guid? ReviewId = null,
    DateTimeOffset? ConfirmedAt = null,
    string? ConfirmedBy = null,
    bool GroupBanned = false,
    bool GroupRemoved = false,
    bool WouldGroupBan = false,
    bool WouldGroupRemove = false,
    string? AiOpinion = null,
    string? AiOpinionReason = null,
    string? AiProposedAction = null,
    DateTimeOffset? AiOpinionAt = null,
    Guid? AiOpinionCallId = null);

/// <summary>One message the model was shown alongside the flagged one (AI moderation design §16).</summary>
public sealed record FlagContextMessage(string MessageId, string Author, string Text);

/// <param name="Languages">Every language these flags are in, with how many of each.</param>
/// <param name="AiOpinionAvailable">
/// Whether the Ask AI button does anything: AI is on and the opinion tool is switched on under
/// Settings → AutoMod (AutoMod design §6).
/// </param>
public sealed record FlagList(
    IReadOnlyList<FlagView> Flags,
    int Open,
    IReadOnlyList<FlagLanguageCount> Languages,
    bool AiOpinionAvailable = false);

/// <param name="Language">The ISO 639-3 code, or null for flags whose language could not be told.</param>
public sealed record FlagLanguageCount(string? Language, string Label, int Flags);

/// <summary>
/// What AutoMod rules flagged, dismissing a flag, and asking the AI what it thinks (AI moderation
/// design §5; AutoMod design §6.3).
/// </summary>
/// <remarks>
/// Reading needs <c>ViewProfile</c>, because a flag is a note about a person (M8 §4.1). Dismissing
/// needs <c>ReviewTickets</c>, the permission for closing a question about somebody. A dismissal
/// suppresses that rule and term for that person from then on, so it is recorded as a fact naming
/// who dismissed it.
/// </remarks>
public static class FlagEndpoints
{
    public const int PageSize = 200;

    public static IEndpointRouteBuilder MapModerationFlags(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/moderation-flags").WithTags("Moderation");

        group.MapGet("", async (
                [FromQuery] string? state,
                [FromQuery] string? language,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var wanted = state switch
                {
                    "dismissed" => ModerationFlagState.Dismissed,
                    "confirmed" => ModerationFlagState.Confirmed,
                    _ => ModerationFlagState.Open,
                };

                var query = db.ModerationFlags.AsNoTracking().Where(f => f.State == wanted);

                // "unknown" is a language on this page: a flag whose language could not be told is
                // exactly the kind a moderator wants to pick out.
                var wantedLanguage = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
                if (wantedLanguage is UnknownLanguage)
                    query = query.Where(f => f.Language == null);
                else if (wantedLanguage is not null)
                    query = query.Where(f => f.Language == wantedLanguage);

                var flags = await query
                    .OrderByDescending(f => wanted == ModerationFlagState.Open ? f.FlaggedAt : (f.DismissedAt ?? f.ConfirmedAt))
                    .Take(PageSize)
                    .ToListAsync(ct);

                var open = await db.ModerationFlags.CountAsync(f => f.State == ModerationFlagState.Open, ct);
                var text = await RuleTextAsync(db, flags, ct);
                var context = await ContextAsync(db, flags, ct);

                // Counted over every flag in this state, not only the page, so the filter offers a
                // language the page in front of you happens not to show.
                var counts = await db.ModerationFlags.AsNoTracking()
                    .Where(f => f.State == wanted)
                    .GroupBy(f => f.Language)
                    .Select(g => new { Language = g.Key, Flags = g.Count() })
                    .ToListAsync(ct);

                var tools = await AiToolsAsync(db, ct);

                return Results.Ok(new FlagList(
                    [.. flags.Select(f => View(f, text, context))],
                    open,
                    [.. counts
                        .OrderByDescending(c => c.Flags)
                        .Select(c => new FlagLanguageCount(c.Language, LanguageNames.Label(c.Language), c.Flags))],
                    tools.Opinion));
            })
            .WithName("ListModerationFlags")
            .WithSummary("List flags")
            .WithDescription(
                "The newest flags: open, dismissed or confirmed, and filtered by language.")
            .Produces<FlagList>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ViewProfile);

        group.MapPost("/{id:guid}/dismiss", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter facts,
                [FromServices] EventPartitionMaintainer partitions,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Forbid();

                var flag = await db.ModerationFlags.FirstOrDefaultAsync(f => f.Id == id, ct);
                if (flag is null)
                    return Results.NotFound(new { error = "No such flag." });
                if (flag.State != ModerationFlagState.Open)
                    return Results.Conflict(new { error = "This flag is already closed." });

                var username = http.User.Identity?.Name ?? string.Empty;
                var now = clock.UtcNow;

                await partitions.EnsureForAsync(now, ct);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                await FlagDecisions.DismissAsync(facts, flag, userId, username, now, ct);
                await db.SaveChangesAsync(ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(flag, await RuleTextAsync(db, [flag], ct)));
            })
            .WithName("DismissModerationFlag")
            .WithSummary("Dismiss flag")
            .WithDescription("Dismiss a flag. The same rule and term never flags this person again.")
            .Produces<FlagView>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ReviewTickets);

        group.MapPost("/{id:guid}/review", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] ReviewFacts facts,
                CancellationToken ct) =>
            {
                var flag = await db.ModerationFlags.FirstOrDefaultAsync(f => f.Id == id, ct);
                if (flag is null)
                    return Results.NotFound(new { error = "No such flag." });
                if (flag.State != ModerationFlagState.Open)
                    return Results.Conflict(new { error = "This flag is already closed." });
                if (flag.ReviewId is not null)
                    return Results.Conflict(new { error = "This flag already has a review." });

                var now = clock.UtcNow;
                var review = FlagReviews.For(flag, now);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Reviews.Add(review);
                flag.ReviewId = review.Id;
                await db.SaveChangesAsync(ct);

                await facts.OpenedAsync(review, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(flag, await RuleTextAsync(db, [flag], ct)));
            })
            .WithName("OpenModerationFlagReview")
            .WithSummary("Open flag review")
            .WithDescription(
                "Open a review for a flag, so the team's review flow decides it. "
                + "Closing that review as wrong dismisses the flag; closing it as right confirms it. "
                + "Both feed the rule's counts on Settings → AutoMod.")
            .Produces<FlagView>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ReviewTickets);

        group.MapPost("/{id:guid}/ai-opinion", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] FlagReviewer? reviewer,
                CancellationToken ct) =>
            {
                var flag = await db.ModerationFlags.FirstOrDefaultAsync(f => f.Id == id, ct);
                if (flag is null)
                    return Results.NotFound(new { error = "No such flag." });

                // The tool switches are checked here, before anything is built to send: a
                // switched-off tool is never called (AutoMod design §6).
                var tools = await AiToolsAsync(db, ct);
                if (reviewer is null || !tools.AiOn)
                    return Results.Conflict(new { error = NoAiRuleChecker.Reason });
                if (!tools.Opinion)
                    return Results.Conflict(new { error = "The AI opinion tool is switched off in AutoMod." });

                var result = await reviewer.ReviewAsync(
                    flag, tools.Proposals, ModbotAuth.UserIdOf(http.User), ModbotAuth.UsernameOf(http.User), ct);

                if (result.Opinion is not { } opinion)
                    return Results.Conflict(new { error = result.Problem ?? "The model did not answer." });

                var now = clock.UtcNow;
                flag.AiOpinion = opinion.Verdict;
                flag.AiOpinionReason = opinion.Why;
                flag.AiProposedAction = opinion.ProposedAction;
                flag.AiOpinionAt = now;
                flag.AiOpinionCallId = opinion.CallId;

                // The review, when there is one, shows the opinion beside the flag's evidence so
                // the person closing it reads both in one place.
                if (flag.ReviewId is { } reviewId
                    && await db.Reviews.FirstOrDefaultAsync(r => r.Id == reviewId, ct) is { } review)
                {
                    review.Evidence = FlagReviews.WithOpinion(review.Evidence, opinion.Verdict, opinion.Why, opinion.ProposedAction, now);
                }

                await db.SaveChangesAsync(ct);

                return Results.Ok(View(flag, await RuleTextAsync(db, [flag], ct)));
            })
            .WithName("AskAiAboutModerationFlag")
            .WithSummary("Ask AI about flag")
            .WithDescription(
                "Advice only: the answer goes on the flag and on its review, and nothing is done with "
                + "it. Needs AI on and the opinion tool switched on under Settings → AutoMod; the "
                + "proposed action comes only when that tool is on too.")
            .Produces<FlagView>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ReviewTickets);

        return app;
    }

    /// <summary>Which of the AI's flag tools the group has on, and whether AI is on at all.</summary>
    private sealed record AiTools(bool AiOn, bool Opinion, bool Proposals);

    private static async Task<AiTools> AiToolsAsync(ModbotContext db, CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().Where(s => s.Id == 1)
            .Select(s => new { s.AiEnabled, s.AutoModAiTools })
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return new AiTools(false, false, false);

        var switches = AutoModAiTools.Parse(row.AutoModAiTools);

        return new AiTools(
            row.AiEnabled,
            row.AiEnabled && AutoModAiTools.IsOn(switches, AutoModAiTools.ReviewFlag),
            row.AiEnabled && AutoModAiTools.IsOn(switches, AutoModAiTools.ProposeAction));
    }

    /// <summary>What the Flags page filter calls a flag whose language could not be told.</summary>
    public const string UnknownLanguage = "unknown";

    /// <summary>
    /// The context messages each flag's check sent, keyed by message id (AI moderation design §16).
    /// </summary>
    /// <remarks>
    /// Read now rather than copied onto the flag, because the message rows are already kept and a
    /// second copy of somebody's words is a second thing to delete when they ask.
    /// </remarks>
    private static async Task<Dictionary<string, FlagContextMessage>> ContextAsync(
        ModbotContext db, IReadOnlyList<ModerationFlag> flags, CancellationToken ct)
    {
        var ids = flags.SelectMany(f => ContextIds(f.ContextMessageIds)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return [];

        var rows = await db.DiscordMessages.AsNoTracking()
            .Where(m => ids.Contains(m.MessageId))
            .Select(m => new { m.MessageId, m.AuthorName, m.Text })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.MessageId, r => new FlagContextMessage(r.MessageId, r.AuthorName, r.Text), StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ContextIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The rule text each flag's version read, keyed by rule and version (AI moderation design §14).
    /// </summary>
    /// <remarks>
    /// Read from the version rows rather than from the rule, because the rule may have been changed
    /// or deleted since. A flag that says "matched by this rule" and shows today's rule is a flag
    /// that misrepresents itself.
    /// </remarks>
    private static async Task<Dictionary<(Guid, int), string>> RuleTextAsync(
        ModbotContext db, IReadOnlyList<ModerationFlag> flags, CancellationToken ct)
    {
        var ids = flags.Where(f => f.RuleVersion > 0).Select(f => f.RuleId).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var versions = flags.Where(f => f.RuleVersion > 0).Select(f => f.RuleVersion).Distinct().ToList();

        var rows = await db.ModerationRuleVersions.AsNoTracking()
            .Where(v => ids.Contains(v.RuleId) && versions.Contains(v.Version))
            .Select(v => new { v.RuleId, v.Version, v.Text })
            .ToListAsync(ct);

        var text = new Dictionary<(Guid, int), string>();
        foreach (var row in rows)
            text[(row.RuleId, row.Version)] = row.Text;

        return text;
    }

    private static FlagView View(
        ModerationFlag f,
        IReadOnlyDictionary<(Guid, int), string>? ruleText = null,
        IReadOnlyDictionary<string, FlagContextMessage>? context = null) => new(
        f.Id,
        f.FlaggedAt,
        f.RuleKind,
        f.RuleId,
        f.RuleName,
        f.Term,
        f.Target,
        f.SubjectPlatform switch
        {
            FactPlatform.Discord => "discord",
            FactPlatform.VRChat => "vrchat",
            _ => "modbot",
        },
        f.SubjectId,
        f.SubjectName,
        f.ChannelId,
        f.MessageId,
        f.Matched,
        f.Reason,
        f.MessageDeleted,
        f.TimedOutMinutes,
        f.State switch
        {
            ModerationFlagState.Dismissed => "dismissed",
            ModerationFlagState.Confirmed => "confirmed",
            _ => "open",
        },
        f.DismissedAt,
        f.DismissedByUsername,
        f.RuleVersion,
        ruleText is not null && ruleText.TryGetValue((f.RuleId, f.RuleVersion), out var text) ? text : null,
        f.Trial,
        f.WouldDeleteMessage,
        f.WouldTimeOutMinutes,
        f.CallId,
        f.Language,
        LanguageNames.Label(f.Language),
        f.Picture,
        f.PictureUrl,
        [.. ContextIds(f.ContextMessageIds)
            .Select(id => context is not null && context.TryGetValue(id, out var m) ? m : null)
            .OfType<FlagContextMessage>()],
        f.ReviewId,
        f.ConfirmedAt,
        f.ConfirmedByUsername,
        f.GroupBanned,
        f.GroupRemoved,
        f.WouldGroupBan,
        f.WouldGroupRemove,
        f.AiOpinion,
        f.AiOpinionReason,
        f.AiProposedAction,
        f.AiOpinionAt,
        f.AiOpinionCallId);
}
