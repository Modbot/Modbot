using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Conventions;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Evidence.Options;

namespace Modbot.Api.Features.Settings;

/// <summary>
/// One automatic role rule, as saved. The same fields go in (<see cref="AutomaticRoleInput"/>) and
/// come out.
/// </summary>
public sealed record AutomaticRoleView(
    Guid Id,
    string? Name,
    string? VRChatRoleId,
    string? DiscordRoleId,
    bool WholeTeam,
    IReadOnlyList<string> WhoDiscordRoleIds,
    IReadOnlyList<string> WhoVRChatRoleIds,
    bool CompanionRunning,
    bool ClipsReady,
    bool InGroupInstance,
    bool MinimumLevel,
    bool MinimumLevelPassedTrial,
    string? MinimumLevelStaffRoleId,
    bool OverturnedBans,
    int? OverturnedBansMax,
    int? OverturnedBansDays,
    bool DailyBanCap,
    int? DailyBanCapMax,
    bool MissedClips,
    int? MissedClipsMax,
    int? MissedClipsDays,
    IReadOnlyList<Guid> TakeBackUserIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>What a person saves for one rule. Numbers for a condition that is switched off are dropped.</summary>
public sealed record AutomaticRoleInput(
    string? Name,
    string? VRChatRoleId,
    string? DiscordRoleId,
    bool WholeTeam,
    IReadOnlyList<string>? WhoDiscordRoleIds,
    IReadOnlyList<string>? WhoVRChatRoleIds,
    bool CompanionRunning,
    bool ClipsReady,
    bool InGroupInstance,
    bool MinimumLevel,
    bool MinimumLevelPassedTrial,
    string? MinimumLevelStaffRoleId,
    bool OverturnedBans,
    int? OverturnedBansMax,
    int? OverturnedBansDays,
    bool DailyBanCap,
    int? DailyBanCapMax,
    bool MissedClips,
    int? MissedClipsMax,
    int? MissedClipsDays,
    IReadOnlyList<Guid>? TakeBackUserIds);

public sealed record AutomaticRoleList(IReadOnlyList<AutomaticRoleView> Rules);

/// <summary>
/// What makes an automatic role rule acceptable. No database: whether the group has evidence
/// storage set up is passed in.
/// </summary>
public static class AutomaticRoleRules
{
    public const int MaxNameLength = 100;
    public const int MaxIdLength = 100;
    public const int MaxChosenRoles = 50;
    public const int MaxTakeBackUsers = 200;
    public const int MaxDays = 365;
    public const int MaxCount = 1000;

    /// <summary>The sentence to refuse the rule with, or null when it can be saved.</summary>
    public static string? Problem(AutomaticRoleInput rule, bool evidenceStorageSetUp)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (Clean(rule.Name) is { Length: > MaxNameLength })
            return $"The name can be at most {MaxNameLength} characters.";

        var vrchatRole = Clean(rule.VRChatRoleId);
        var discordRole = Clean(rule.DiscordRoleId);

        if (vrchatRole is null && discordRole is null)
            return "Pick a VRChat role, a Discord role, or one of each.";

        if (TooLong(vrchatRole) || TooLong(discordRole) || TooLong(Clean(rule.MinimumLevelStaffRoleId)))
            return "A role id is too long.";

        if (!rule.WholeTeam)
        {
            var discord = Ids(rule.WhoDiscordRoleIds);
            var vrchat = Ids(rule.WhoVRChatRoleIds);

            if (discord.Count == 0 && vrchat.Count == 0)
                return "Pick the Discord or VRChat roles the rule applies to, or choose the whole team.";

            if (discord.Count > MaxChosenRoles || vrchat.Count > MaxChosenRoles)
                return $"Pick at most {MaxChosenRoles} roles to apply the rule to.";

            if (discord.Any(TooLong) || vrchat.Any(TooLong))
                return "A role id is too long.";
        }

        if (!(rule.CompanionRunning || rule.ClipsReady || rule.InGroupInstance || rule.MinimumLevel
              || rule.OverturnedBans || rule.DailyBanCap || rule.MissedClips))
            return "Switch on at least one condition.";

        if ((rule.ClipsReady || rule.MissedClips) && !evidenceStorageSetUp)
            return "Clips ready and missed clips need evidence storage. Set it up first.";

        if (rule.MinimumLevel && !rule.MinimumLevelPassedTrial && Clean(rule.MinimumLevelStaffRoleId) is null)
            return "Minimum level needs the staff trial passed, a Discord staff role, or both.";

        if (rule.OverturnedBans
            && (Outside(rule.OverturnedBansMax, 0, MaxCount) || Outside(rule.OverturnedBansDays, 1, MaxDays)))
            return $"Overturned bans: pick 0 to {MaxCount} bans over 1 to {MaxDays} days.";

        if (rule.DailyBanCap && Outside(rule.DailyBanCapMax, 1, MaxCount))
            return $"Daily ban cap: pick 1 to {MaxCount} bans.";

        if (rule.MissedClips
            && (Outside(rule.MissedClipsMax, 0, MaxCount) || Outside(rule.MissedClipsDays, 1, MaxDays)))
            return $"Missed clips: pick 0 to {MaxCount} clips over 1 to {MaxDays} days.";

        if ((rule.TakeBackUserIds?.Distinct().Count() ?? 0) > MaxTakeBackUsers)
            return $"The take-back list can hold at most {MaxTakeBackUsers} people.";

        return null;
    }

    /// <summary>Copies the input onto a row, tidied: text trimmed, empty text gone, switched-off numbers dropped.</summary>
    public static void Apply(AutomaticRole row, AutomaticRoleInput rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);

        row.Name = Clean(rule.Name);
        row.VRChatRoleId = Clean(rule.VRChatRoleId);
        row.DiscordRoleId = Clean(rule.DiscordRoleId);

        row.WholeTeam = rule.WholeTeam;
        row.WhoDiscordRoleIds = rule.WholeTeam ? [] : Ids(rule.WhoDiscordRoleIds);
        row.WhoVRChatRoleIds = rule.WholeTeam ? [] : Ids(rule.WhoVRChatRoleIds);

        row.CompanionRunning = rule.CompanionRunning;
        row.ClipsReady = rule.ClipsReady;
        row.InGroupInstance = rule.InGroupInstance;

        row.MinimumLevel = rule.MinimumLevel;
        row.MinimumLevelPassedTrial = rule.MinimumLevel && rule.MinimumLevelPassedTrial;
        row.MinimumLevelStaffRoleId = rule.MinimumLevel ? Clean(rule.MinimumLevelStaffRoleId) : null;

        row.OverturnedBans = rule.OverturnedBans;
        row.OverturnedBansMax = rule.OverturnedBans ? rule.OverturnedBansMax : null;
        row.OverturnedBansDays = rule.OverturnedBans ? rule.OverturnedBansDays : null;

        row.DailyBanCap = rule.DailyBanCap;
        row.DailyBanCapMax = rule.DailyBanCap ? rule.DailyBanCapMax : null;

        row.MissedClips = rule.MissedClips;
        row.MissedClipsMax = rule.MissedClips ? rule.MissedClipsMax : null;
        row.MissedClipsDays = rule.MissedClips ? rule.MissedClipsDays : null;

        row.TakeBackUserIds = [.. (rule.TakeBackUserIds ?? []).Distinct()];
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<string> Ids(IReadOnlyList<string>? ids)
        => [.. (ids ?? []).Select(Clean).OfType<string>().Distinct(StringComparer.Ordinal)];

    private static bool TooLong(string? id) => id is { Length: > MaxIdLength };

    private static bool Outside(int? value, int low, int high) => value is null || value < low || value > high;
}

/// <summary>
/// Settings → Automatic roles: the rules, listed, added, changed and deleted. A rule gives a VRChat
/// role, a Discord role, or one of each, while every condition it switches on holds. This stores the
/// rules only; nothing here checks a condition or gives a role.
/// </summary>
public static class AutomaticRoleSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAutomaticRoleSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/automatic-roles")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var rows = await db.AutomaticRoles.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
                return Results.Ok(new AutomaticRoleList([.. rows.Select(View)]));
            })
            .WithName("ListAutomaticRoles")
            .WithSummary("List automatic roles")
            .WithDescription("Every automatic role rule, oldest first.")
            .Produces<AutomaticRoleList>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("", async (
                HttpContext http,
                [FromBody] AutomaticRoleInput body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (await RefusalAsync(db, body, ct) is { } refusal)
                    return refusal;

                var now = clock.UtcNow;
                var row = new AutomaticRole { Id = Guid.CreateVersion7(now), CreatedAt = now, UpdatedAt = now };
                AutomaticRoleRules.Apply(row, body);

                var change = new SettingsChange("automaticRoles").Document(null, Describe(row));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.AutomaticRoles.Add(row);
                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(row));
            })
            .WithName("CreateAutomaticRole")
            .WithSummary("Add automatic role")
            .WithDescription("Add a rule. It needs a VRChat role or a Discord role, and at least one condition switched on.")
            .Produces<AutomaticRoleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] AutomaticRoleInput body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var row = await db.AutomaticRoles.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (row is null)
                    return Problems.Of(StatusCodes.Status404NotFound, "No such automatic role.");

                if (await RefusalAsync(db, body, ct) is { } refusal)
                    return refusal;

                var before = Describe(row);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                AutomaticRoleRules.Apply(row, body);
                row.UpdatedAt = clock.UtcNow;

                var change = new SettingsChange("automaticRoles").Document(before, Describe(row));

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(row));
            })
            .WithName("UpdateAutomaticRole")
            .WithSummary("Update automatic role")
            .WithDescription("Replace a rule's settings.")
            .Produces<AutomaticRoleView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var row = await db.AutomaticRoles.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (row is null)
                    return Problems.Of(StatusCodes.Status404NotFound, "No such automatic role.");

                var change = new SettingsChange("automaticRoles").Document(Describe(row), null);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.AutomaticRoles.Remove(row);
                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .WithName("DeleteAutomaticRole")
            .WithSummary("Delete automatic role")
            .WithDescription("Delete a rule.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>The refusal for a rule that cannot be saved, or null.</summary>
    private static async Task<IResult?> RefusalAsync(ModbotContext db, AutomaticRoleInput body, CancellationToken ct)
    {
        // The same test the clip requests use: no evidence storage, nowhere to put a clip.
        var settings = await db.GetSettingsAsync(ct);
        var storageSetUp = EvidenceSettingsBinder.ToBackend(settings.EvidenceBackend) is not EvidenceBackend.None;

        if (AutomaticRoleRules.Problem(body, storageSetUp) is { } problem)
            return Problems.Of(StatusCodes.Status400BadRequest, problem);

        var people = (body.TakeBackUserIds ?? []).Distinct().ToList();
        if (people.Count > 0 && await db.Users.CountAsync(u => people.Contains(u.Id), ct) != people.Count)
            return Problems.Of(StatusCodes.Status400BadRequest, "Someone on the take-back list is not a Modbot user.");

        return null;
    }

    /// <summary>The rule as the audit log shows it: every setting that matters, nothing else.</summary>
    private static string Describe(AutomaticRole row)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(View(row), System.Text.Json.JsonSerializerOptions.Web)!.AsObject();
        json.Remove("id");
        json.Remove("createdAt");
        json.Remove("updatedAt");
        return new JsonObject { ["rule"] = json }.ToJsonString();
    }

    private static AutomaticRoleView View(AutomaticRole r) => new(
        r.Id, r.Name, r.VRChatRoleId, r.DiscordRoleId, r.WholeTeam, r.WhoDiscordRoleIds, r.WhoVRChatRoleIds,
        r.CompanionRunning, r.ClipsReady, r.InGroupInstance,
        r.MinimumLevel, r.MinimumLevelPassedTrial, r.MinimumLevelStaffRoleId,
        r.OverturnedBans, r.OverturnedBansMax, r.OverturnedBansDays,
        r.DailyBanCap, r.DailyBanCapMax,
        r.MissedClips, r.MissedClipsMax, r.MissedClipsDays,
        r.TakeBackUserIds, r.CreatedAt, r.UpdatedAt);
}
