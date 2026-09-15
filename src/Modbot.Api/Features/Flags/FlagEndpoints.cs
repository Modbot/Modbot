using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Flags;

/// <param name="SubjectPlatform"><c>vrchat</c> or <c>discord</c>.</param>
/// <param name="RuleVersion">The rule version that flagged (AI moderation design §14).</param>
/// <param name="RuleText">
/// The rule as it read at that version — the terms, or what to catch. Null when the version is not
/// recorded, which is every flag from before rule versions existed.
/// </param>
/// <param name="Trial">The rule was in its trial, so nothing was done.</param>
/// <param name="WouldDeleteMessage">What the rule would have done, during a trial or while paused.</param>
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
    int? WouldTimeOutMinutes = null);

public sealed record FlagList(IReadOnlyList<FlagView> Flags, int Open);

/// <summary>
/// What AI moderation rules flagged, and dismissing a flag (AI moderation design §5).
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
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var wanted = state == "dismissed" ? ModerationFlagState.Dismissed : ModerationFlagState.Open;

                var flags = await db.ModerationFlags.AsNoTracking()
                    .Where(f => f.State == wanted)
                    .OrderByDescending(f => wanted == ModerationFlagState.Dismissed ? f.DismissedAt : f.FlaggedAt)
                    .Take(PageSize)
                    .ToListAsync(ct);

                var open = await db.ModerationFlags.CountAsync(f => f.State == ModerationFlagState.Open, ct);
                var text = await RuleTextAsync(db, flags, ct);

                return Results.Ok(new FlagList([.. flags.Select(f => View(f, text))], open));
            })
            .WithName("ListModerationFlags")
            .WithSummary("The newest flags, open or dismissed")
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
                if (flag.State == ModerationFlagState.Dismissed)
                    return Results.Conflict(new { error = "This flag is already dismissed." });

                var username = http.User.Identity?.Name ?? string.Empty;
                var now = clock.UtcNow;

                await partitions.EnsureForAsync(now, ct);
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                flag.State = ModerationFlagState.Dismissed;
                flag.DismissedAt = now;
                flag.DismissedByUserId = userId;
                flag.DismissedByUsername = username.Length <= 64 ? username : username[..64];
                await db.SaveChangesAsync(ct);

                await facts.WriteAsync(new FactRecord
                {
                    Type = FactType.AiModerationFlagDismissed,
                    OccurredAt = now,
                    SubjectPlatform = flag.SubjectPlatform,
                    SubjectId = flag.SubjectId,
                    ActorPlatform = FactPlatform.Modbot,
                    ActorId = userId.ToString(),
                    Source = FactSource.Modbot,
                    Data = new JsonObject
                    {
                        ["flagId"] = flag.Id.ToString(),
                        ["ruleKind"] = flag.RuleKind,
                        ["ruleId"] = flag.RuleId.ToString(),
                        ["ruleName"] = flag.RuleName,
                        ["termKey"] = flag.TermKey,
                        ["term"] = flag.Term,
                        ["matched"] = flag.Matched,
                        ["username"] = username,
                    },
                }, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(flag, await RuleTextAsync(db, [flag], ct)));
            })
            .WithName("DismissModerationFlag")
            .WithSummary("Dismiss a flag. The same rule and term never flags this person again.")
            .Produces<FlagView>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ReviewTickets);

        return app;
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

    private static FlagView View(ModerationFlag f, IReadOnlyDictionary<(Guid, int), string>? ruleText = null) => new(
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
        f.State == ModerationFlagState.Dismissed ? "dismissed" : "open",
        f.DismissedAt,
        f.DismissedByUsername,
        f.RuleVersion,
        ruleText is not null && ruleText.TryGetValue((f.RuleId, f.RuleVersion), out var text) ? text : null,
        f.Trial,
        f.WouldDeleteMessage,
        f.WouldTimeOutMinutes);
}
