using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <param name="Id">The rule's id, as sent back in <c>autoModRules</c>.</param>
/// <param name="Kind"><c>termList</c> or <c>topic</c>.</param>
/// <param name="Counts">Whether its flags make somebody Flagged now.</param>
public sealed record FlagRuleAutoModOption(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("counts")] bool Counts);

/// <param name="LiftedBansForDays">How many days a lifted ban still counts. Null means always.</param>
/// <param name="DiscordPost">Whether a flagged join is also posted in the Discord channel.</param>
/// <param name="DiscordChannelId">The channel it is posted in. Null when none is picked.</param>
/// <param name="CrashAlert">Whether many people leaving one instance at once raises a possible crash alert.</param>
/// <param name="CrashLeaves">How many people must leave one instance within <paramref name="CrashSeconds"/>.</param>
/// <param name="CrashSeconds">The window, in seconds, those people must leave within.</param>
/// <param name="WarnsAtLeast">How many instance warns make somebody Flagged.</param>
/// <param name="EveryAutoModRule">Every AutoMod rule counts, including rules added later.</param>
/// <param name="AutoModRules">Every AutoMod rule there is, and whether it counts.</param>
public sealed record FlagRulesView(
    [property: JsonPropertyName("kicksAndBans")] bool KicksAndBans,
    [property: JsonPropertyName("liftedBansForDays")] int? LiftedBansForDays,
    [property: JsonPropertyName("warns")] bool Warns,
    [property: JsonPropertyName("warnsAtLeast")] int WarnsAtLeast,
    [property: JsonPropertyName("nuisance")] bool Nuisance,
    [property: JsonPropertyName("autoMod")] bool AutoMod,
    [property: JsonPropertyName("everyAutoModRule")] bool EveryAutoModRule,
    [property: JsonPropertyName("autoModRules")] IReadOnlyList<FlagRuleAutoModOption> AutoModRules,
    [property: JsonPropertyName("newAccounts")] bool NewAccounts,
    [property: JsonPropertyName("discordPost")] bool DiscordPost,
    [property: JsonPropertyName("discordChannelId")] string? DiscordChannelId,
    [property: JsonPropertyName("crashAlert")] bool CrashAlert,
    [property: JsonPropertyName("crashLeaves")] int CrashLeaves,
    [property: JsonPropertyName("crashSeconds")] int CrashSeconds);

/// <param name="AutoModRules">The rules that count when <c>everyAutoModRule</c> is false.</param>
/// <param name="LiftedBansForDays">
/// How many days a lifted ban still counts, 1 to 3650. Null or left out means always, so a caller
/// written before this existed keeps today's rule.
/// </param>
/// <param name="NewAccounts">
/// Whether a Visitor or New User account makes somebody Flagged. Null or left out keeps what is
/// set now, so a caller written before this existed does not switch it.
/// </param>
/// <param name="DiscordPost">Whether a flagged join is also posted in Discord. Null or left out keeps what is set now.</param>
/// <param name="DiscordChannelId">
/// The channel to post in. Null or left out keeps what is set now; an empty string picks none.
/// </param>
/// <param name="CrashAlert">Whether a possible crash alert is raised. Null or left out keeps what is set now.</param>
/// <param name="CrashLeaves">How many people must leave, 2 to 99. Null or left out keeps what is set now.</param>
/// <param name="CrashSeconds">Within how many seconds, 1 to 60. Null or left out keeps what is set now.</param>
public sealed record SetFlagRulesRequest(
    [property: JsonPropertyName("kicksAndBans")] bool KicksAndBans,
    [property: JsonPropertyName("warns")] bool Warns,
    [property: JsonPropertyName("warnsAtLeast")] int WarnsAtLeast,
    [property: JsonPropertyName("nuisance")] bool Nuisance,
    [property: JsonPropertyName("autoMod")] bool AutoMod,
    [property: JsonPropertyName("everyAutoModRule")] bool EveryAutoModRule,
    [property: JsonPropertyName("autoModRules")] IReadOnlyList<Guid>? AutoModRules,
    [property: JsonPropertyName("liftedBansForDays")] int? LiftedBansForDays = null,
    [property: JsonPropertyName("newAccounts")] bool? NewAccounts = null,
    [property: JsonPropertyName("discordPost")] bool? DiscordPost = null,
    [property: JsonPropertyName("discordChannelId")] string? DiscordChannelId = null,
    [property: JsonPropertyName("crashAlert")] bool? CrashAlert = null,
    [property: JsonPropertyName("crashLeaves")] int? CrashLeaves = null,
    [property: JsonPropertyName("crashSeconds")] int? CrashSeconds = null);

/// <summary>
/// Settings → Moderation → Flagged: which rules make a person Flagged on the companion and the
/// Live page (flagged rules design §3).
/// </summary>
/// <remarks>
/// Nothing is rebuilt on a save. Flagged is decided on every read, so the next roster read and the
/// next join already follow the new rules; a join card that already went out stays as it was.
/// </remarks>
public static class FlagRuleSettingsEndpoints
{
    public static IEndpointRouteBuilder MapFlagRuleSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/flag-rules")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async ([FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                return Results.Ok(await ViewAsync(db, FlagRuleSettings.Read(settings.FlagRules), ct));
            })
            .WithName("GetFlagRules")
            .WithSummary("Get flag rules")
            .WithDescription("The rules that make a person Flagged on the companion and the Live page.")
            .Produces<FlagRulesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                [FromBody] SetFlagRulesRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.WarnsAtLeast is < FlagRuleSettings.MinWarns or > FlagRuleSettings.MaxWarns)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Warns must be between {FlagRuleSettings.MinWarns} and {FlagRuleSettings.MaxWarns}.",
                    });
                }

                if (body.LiftedBansForDays is < FlagRuleSettings.MinLiftedBanDays or > FlagRuleSettings.MaxLiftedBanDays)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Lifted bans must count for {FlagRuleSettings.MinLiftedBanDays} to "
                            + $"{FlagRuleSettings.MaxLiftedBanDays} days.",
                    });
                }

                if (body.CrashLeaves is < FlagRuleSettings.MinCrashLeaves or > FlagRuleSettings.MaxCrashLeaves)
                {
                    return Results.BadRequest(new
                    {
                        error = $"People leaving must be between {FlagRuleSettings.MinCrashLeaves} and {FlagRuleSettings.MaxCrashLeaves}.",
                    });
                }

                if (body.CrashSeconds is < FlagRuleSettings.MinCrashSeconds or > FlagRuleSettings.MaxCrashSeconds)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Seconds must be between {FlagRuleSettings.MinCrashSeconds} and {FlagRuleSettings.MaxCrashSeconds}.",
                    });
                }

                if (body.DiscordChannelId?.Trim() is { Length: > FlagRuleSettings.ChannelIdLength })
                {
                    return Results.BadRequest(new
                    {
                        error = $"A channel id is at most {FlagRuleSettings.ChannelIdLength} characters.",
                    });
                }

                IReadOnlyList<Guid>? chosen = null;

                if (!body.EveryAutoModRule)
                {
                    var wanted = (body.AutoModRules ?? []).Distinct().ToList();
                    var known = await RuleIdsAsync(db, ct);

                    if (wanted.Where(id => !known.Contains(id)).ToList() is [var unknown, ..])
                        return Results.BadRequest(new { error = $"'{unknown}' is not an AutoMod rule." });

                    chosen = wanted;
                }

                var settings = await db.GetSettingsAsync(ct);
                var before = FlagRuleSettings.Read(settings.FlagRules);

                var channelId = body.DiscordChannelId is null
                    ? before.DiscordChannelId
                    : string.IsNullOrWhiteSpace(body.DiscordChannelId) ? null : body.DiscordChannelId.Trim();
                var post = body.DiscordPost ?? before.DiscordPost;

                if (post && channelId is null)
                    return Results.BadRequest(new { error = "Pick a channel to post in." });

                var after = new FlagRuleSettings
                {
                    KicksAndBans = body.KicksAndBans,
                    LiftedBansForDays = body.LiftedBansForDays,
                    Warns = body.Warns,
                    WarnsAtLeast = body.WarnsAtLeast,
                    Nuisance = body.Nuisance,
                    NewAccounts = body.NewAccounts ?? before.NewAccounts,
                    DiscordPost = post,
                    DiscordChannelId = channelId,
                    CrashAlert = body.CrashAlert ?? before.CrashAlert,
                    CrashLeaves = body.CrashLeaves ?? before.CrashLeaves,
                    CrashSeconds = body.CrashSeconds ?? before.CrashSeconds,
                    AutoMod = body.AutoMod,
                    AutoModRules = chosen,
                }.Clamped();

                if (Json(after).ToJsonString() == Json(before).ToJsonString())
                    return Results.Ok(await ViewAsync(db, before, ct));

                settings.FlagRules = after.ToJson();
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "flagRules",
                        ["before"] = Json(before),
                        ["after"] = Json(after),
                    },
                    ct);

                return Results.Ok(await ViewAsync(db, after, ct));
            })
            .WithName("SetFlagRules")
            .WithSummary("Update flag rules")
            .WithDescription(
                "Change which rules make a person Flagged. `warnsAtLeast` is 1 to 99. "
                + "`liftedBansForDays` is 1 to 3650, or null for a lifted ban to count always. With "
                + "`everyAutoModRule` false, only the flags of the rules in `autoModRules` count. "
                + "`newAccounts` makes a Visitor or New User account Flagged; left out, it keeps its setting. "
                + "`discordPost` also posts every flagged join in the channel `discordChannelId` (an empty string "
                + "picks none); left out, each keeps its setting. "
                + "`crashAlert` raises a possible crash alert when `crashLeaves` people (2 to 99) leave one instance "
                + "within `crashSeconds` seconds (1 to 60); left out, each keeps its setting. "
                + "A watch on a person always counts and has no setting here.")
            .Produces<FlagRulesView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static JsonObject Json(FlagRuleSettings rules)
    {
        var chosen = new JsonArray();
        foreach (var id in (rules.AutoModRules ?? []).Order())
            chosen.Add(JsonValue.Create(id));

        return new JsonObject
        {
            ["kicksAndBans"] = rules.KicksAndBans,
            ["liftedBansForDays"] = rules.LiftedBansForDays,
            ["warns"] = rules.Warns,
            ["warnsAtLeast"] = rules.WarnsAtLeast,
            ["nuisance"] = rules.Nuisance,
            ["newAccounts"] = rules.NewAccounts,
            ["discordPost"] = rules.DiscordPost,
            ["discordChannelId"] = rules.DiscordChannelId,
            ["crashAlert"] = rules.CrashAlert,
            ["crashLeaves"] = rules.CrashLeaves,
            ["crashSeconds"] = rules.CrashSeconds,
            ["autoMod"] = rules.AutoMod,
            ["autoModRules"] = rules.AutoModRules is null ? null : chosen,
        };
    }

    private static async Task<HashSet<Guid>> RuleIdsAsync(ModbotContext db, CancellationToken ct)
    {
        var lists = await db.ModerationTermLists.AsNoTracking().Select(l => l.Id).ToListAsync(ct);
        var topics = await db.ModerationTopics.AsNoTracking().Select(t => t.Id).ToListAsync(ct);

        return [.. lists, .. topics];
    }

    private static async Task<FlagRulesView> ViewAsync(ModbotContext db, FlagRuleSettings rules, CancellationToken ct)
    {
        var lists = await db.ModerationTermLists.AsNoTracking()
            .OrderBy(l => l.Name)
            .Select(l => new { l.Id, l.Name })
            .ToListAsync(ct);

        var topics = await db.ModerationTopics.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        bool Counts(Guid id) => rules.AutoModRules is null || rules.AutoModRules.Contains(id);

        var options = lists
            .Select(l => new FlagRuleAutoModOption(l.Id, ModerationRuleKind.TermList, l.Name, Counts(l.Id)))
            .Concat(topics.Select(t => new FlagRuleAutoModOption(t.Id, ModerationRuleKind.Topic, t.Name, Counts(t.Id))))
            .ToList();

        return new FlagRulesView(
            rules.KicksAndBans,
            rules.LiftedBansForDays,
            rules.Warns,
            rules.WarnsAtLeast,
            rules.Nuisance,
            rules.AutoMod,
            rules.AutoModRules is null,
            options,
            rules.NewAccounts,
            rules.DiscordPost,
            rules.DiscordChannelId,
            rules.CrashAlert,
            rules.CrashLeaves,
            rules.CrashSeconds);
    }
}
