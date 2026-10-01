using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Cases;

/// <summary>
/// Settings → Ban reasons: the list moderators pick from when writing up a ban (spec 5.8.2).
/// </summary>
/// <remarks>
/// <para>
/// Reading the list needs only a session, because everyone who writes a case file needs the
/// buttons and the labels are not sensitive. Changing it needs
/// <see cref="ModbotPermissions.EditClassifications"/>, and every change is a fact naming who made
/// it: the classification is the signal the accountability work reads, and a reason quietly
/// reworded from "Crasher" to "Other" would change what every old case file appears to say.
/// </para>
/// <para>
/// There is no delete. A reason is switched off and stays, because case files cite it by id.
/// </para>
/// <para>
/// Each reason says which actions offer it (<see cref="ReasonUse"/>). The list began as the ban
/// list and served kicks, unbans and join request rejections as well, so an unban could be
/// classified "Harassment" and nothing could say why a ban was lifted (M4 §9). The switch that
/// makes a reason required beyond bans lives here too: it is a rule about this list, and it had
/// no control anywhere before.
/// </para>
/// </remarks>
public static class BanReasonEndpoints
{
    public const int MaxLabelLength = 64;
    public const int MaxDescriptionLength = 256;

    public static IEndpointRouteBuilder MapBanReasons(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/ban-reasons").WithTags("Settings").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var reasons = await BanReasonList.AllAsync(db, clock, ct);
                var held = ModbotAuth.PermissionsOf(http.User);

                return Results.Ok(new BanReasonListResponse(
                    reasons.Select(View).ToList(),
                    held.HasFlag(ModbotPermissions.Administrator) || held.HasFlag(ModbotPermissions.EditClassifications),
                    await ReasonAlwaysRequiredAsync(db, ct)));
            })
            .WithName("GetBanReasons")
            .WithSummary("List ban reasons")
            .WithDescription(
                "The reasons a moderator picks from when banning, kicking, unbanning or turning a "
                + "join request down; usedFor says which actions offer each one. "
                + "In button order, switched-off ones included with isActive false. Seeded with a "
                + "plain default set the first time anybody asks, so the first case file can be "
                + "written without inventing a list first. reasonAlwaysRequired says whether the "
                + "actions other than a ban need a reason too.")
            .Produces<BanReasonListResponse>();

        group.MapPut("/required", async (
                HttpContext http,
                [FromBody] ReasonAlwaysRequiredRequest body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var before = settings.RequireModerationClassification;

                if (before == body.Required)
                    return Results.Ok(new ReasonAlwaysRequiredRequest(before));

                settings.RequireModerationClassification = body.Required;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("banReasons")
                    .Field("reasonAlwaysRequired", before, body.Required)
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(new ReasonAlwaysRequiredRequest(body.Required));
            })
            .RequiresFlag(ModbotPermissions.EditClassifications)
            .WithName("SetReasonAlwaysRequired")
            .WithSummary("Require a reason on every action")
            .WithDescription(
                "Whether a kick, an unban and turning a join request down each need a reason, as a "
                + "ban always does (foundation 5.8.1: optional unless the group asks for it). "
                + "Approving a join request never takes one.")
            .Produces<ReasonAlwaysRequiredRequest>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] BanReasonRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                if (Validate(body) is { } problem)
                    return Results.BadRequest(new { error = problem });

                if (!ReasonUses.TryRead(body.UsedFor, ReasonUses.WhenLeftOut, out var usedFor, out var unknown))
                    return Results.BadRequest(new { error = unknown });

                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                await BanReasonList.AllAsync(db, clock, ct);

                var now = clock.UtcNow;
                var last = await db.BanReasons.AsNoTracking().MaxAsync(r => (int?)r.SortOrder, ct) ?? -1;

                var reason = new BanReason
                {
                    Label = body.Label.Trim(),
                    Description = body.Description?.Trim() ?? string.Empty,
                    NeedsWrittenReason = body.NeedsWrittenReason,
                    UsedFor = usedFor,
                    SortOrder = last + 1,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                db.BanReasons.Add(reason);
                await db.SaveChangesAsync(ct);

                await RecordAsync(facts, partitions, clock, actor, http.User.Identity?.Name, reason.Id.ToString(),
                    new JsonObject
                    {
                        ["action"] = "create",
                        ["reasonId"] = reason.Id.ToString(),
                        ["label"] = reason.Label,
                        ["needsWrittenReason"] = reason.NeedsWrittenReason,
                        ["usedFor"] = ReasonUses.Json(reason.UsedFor),
                    },
                    $"Ban reason \"{reason.Label}\" added",
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(reason));
            })
            .RequiresFlag(ModbotPermissions.EditClassifications)
            .WithName("CreateBanReason")
            .WithSummary("Add ban reason")
            .WithDescription("Add a reason to the end of the list.")
            .Produces<BanReasonView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/order", async (
                HttpContext http,
                [FromBody] BanReasonOrderRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                if (body.Ids is null || body.Ids.Count == 0)
                    return Results.BadRequest(new { error = "ids is required." });

                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                var reasons = await db.BanReasons.ToListAsync(ct);
                var byId = reasons.ToDictionary(r => r.Id);

                if (body.Ids.Any(id => !byId.ContainsKey(id)))
                    return Results.BadRequest(new { error = "One of those ids is not a ban reason." });

                var now = clock.UtcNow;
                var position = 0;

                // Listed ones first, in the order given; anything left out keeps its relative
                // order after them, so a client that only knows the active reasons cannot
                // scramble the switched-off ones.
                foreach (var id in body.Ids.Distinct())
                    Place(byId[id], position++, now);

                foreach (var reason in reasons.Where(r => !body.Ids.Contains(r.Id)).OrderBy(r => r.SortOrder))
                    Place(reason, position++, now);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await RecordAsync(facts, partitions, clock, actor, http.User.Identity?.Name, "ban-reasons",
                    new JsonObject
                    {
                        ["action"] = "reorder",
                        ["ids"] = new JsonArray(body.Ids.Select(id => (JsonNode?)id.ToString()).ToArray()),
                    },
                    "Ban reasons reordered",
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(new BanReasonListResponse(
                    reasons.OrderBy(r => r.SortOrder).Select(View).ToList(),
                    true,
                    await ReasonAlwaysRequiredAsync(db, ct)));

                static void Place(BanReason reason, int order, DateTimeOffset now)
                {
                    if (reason.SortOrder == order) return;
                    reason.SortOrder = order;
                    reason.UpdatedAt = now;
                }
            })
            .RequiresFlag(ModbotPermissions.EditClassifications)
            .WithName("ReorderBanReasons")
            .WithSummary("Reorder ban reasons")
            .WithDescription("Put the reasons in this order.")
            .Produces<BanReasonListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] BanReasonRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                if (Validate(body) is { } problem)
                    return Results.BadRequest(new { error = problem });

                if (ModbotAuth.UserIdOf(http.User) is not { } actor)
                    return Results.Forbid();

                var reason = await db.BanReasons.FirstOrDefaultAsync(r => r.Id == id, ct);
                if (reason is null)
                    return Results.NotFound(new { error = "No such ban reason." });

                if (!ReasonUses.TryRead(body.UsedFor, reason.UsedFor, out var usedFor, out var unknown))
                    return Results.BadRequest(new { error = unknown });

                var before = new JsonObject
                {
                    ["label"] = reason.Label,
                    ["description"] = reason.Description,
                    ["needsWrittenReason"] = reason.NeedsWrittenReason,
                    ["isActive"] = reason.IsActive,
                    ["usedFor"] = ReasonUses.Json(reason.UsedFor),
                };

                reason.Label = body.Label.Trim();
                reason.Description = body.Description?.Trim() ?? string.Empty;
                reason.NeedsWrittenReason = body.NeedsWrittenReason;
                reason.IsActive = body.IsActive ?? reason.IsActive;
                reason.UsedFor = usedFor;
                reason.UpdatedAt = clock.UtcNow;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await RecordAsync(facts, partitions, clock, actor, http.User.Identity?.Name, reason.Id.ToString(),
                    new JsonObject
                    {
                        ["action"] = "change",
                        ["reasonId"] = reason.Id.ToString(),
                        ["before"] = before,
                        ["after"] = new JsonObject
                        {
                            ["label"] = reason.Label,
                            ["description"] = reason.Description,
                            ["needsWrittenReason"] = reason.NeedsWrittenReason,
                            ["isActive"] = reason.IsActive,
                            ["usedFor"] = ReasonUses.Json(reason.UsedFor),
                        },
                    },
                    $"Ban reason \"{reason.Label}\" changed",
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(reason));
            })
            .RequiresFlag(ModbotPermissions.EditClassifications)
            .WithName("UpdateBanReason")
            .WithSummary("Update ban reason")
            .WithDescription(
                "Reword a reason, change which actions offer it, or switch it on or off. "
                + "There is no delete: case files cite reasons by id, and one that pointed at a "
                + "reason nobody can name any more would have lost its classification. Switch it "
                + "off instead; it stays on the case files that picked it and leaves the buttons.")
            .Produces<BanReasonView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>The "require a reason" switch. Off when the settings row does not exist yet.</summary>
    private static Task<bool> ReasonAlwaysRequiredAsync(ModbotContext db, CancellationToken ct)
        => db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.RequireModerationClassification)
            .FirstOrDefaultAsync(ct);

    private static string? Validate(BanReasonRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Label))
            return "A label is required: the word on the button.";

        if (body.Label.Trim().Length > MaxLabelLength)
            return $"The label is too long (at most {MaxLabelLength} characters).";

        if (body.Description is { Length: > MaxDescriptionLength })
            return $"The description is too long (at most {MaxDescriptionLength} characters).";

        return null;
    }

    /// <summary>
    /// The change as a fact, when the writer is registered. A host that maps the API without the
    /// fact log still answers; it just cannot record.
    /// </summary>
    private static async Task RecordAsync(
        IFactWriter? facts,
        EventPartitionMaintainer? partitions,
        IModbotClock clock,
        Guid actor,
        string? actorName,
        string subjectId,
        JsonObject data,
        string description,
        CancellationToken ct)
    {
        if (facts is null || partitions is null)
            return;

        var now = clock.UtcNow;
        data["description"] = description;
        data["actorDisplayName"] = actorName;

        await partitions.EnsureForAsync(now, ct);
        await facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.BanReasonsChanged,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = subjectId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = actor.ToString(),
                Source = FactSource.Modbot,
                Data = data,
            },
            ct);
    }

    internal static BanReasonView View(BanReason r)
        => new(r.Id, r.Label, r.Description, r.SortOrder, r.IsActive, r.NeedsWrittenReason, ReasonUses.Names(r.UsedFor));
}

/// <summary>
/// <see cref="ReasonUse"/> as the API writes it: the actions by the names the moderation endpoints
/// use, so a client can match a reason to the action in front of it without a second vocabulary.
/// </summary>
public static class ReasonUses
{
    /// <summary>What a reason is used for when the request does not say: what a ban reason was used for before unbans had their own.</summary>
    public const ReasonUse WhenLeftOut = ReasonUse.Ban | ReasonUse.Kick | ReasonUse.Reject;

    private static readonly (ReasonUse Use, string Name)[] Known =
    [
        (ReasonUse.Ban, "ban"),
        (ReasonUse.Kick, "kick"),
        (ReasonUse.Unban, "unban"),
        (ReasonUse.Reject, "reject"),
    ];

    public static IReadOnlyList<string> Names(ReasonUse use)
        => Known.Where(k => use.HasFlag(k.Use)).Select(k => k.Name).ToList();

    public static JsonArray Json(ReasonUse use)
        => new(Names(use).Select(n => (JsonNode?)n).ToArray());

    /// <summary>
    /// The action names a moderation request carries, as the one use each stands for.
    /// <see cref="ReasonUse.None"/> for anything else, approving a join request included: approving
    /// somebody needs no reason.
    /// </summary>
    public static ReasonUse ForAction(string action)
        => Known.FirstOrDefault(k => string.Equals(k.Name, action, StringComparison.Ordinal)).Use;

    /// <summary>
    /// Reads the list a request sent. Null keeps <paramref name="fallback"/>; an empty list, or a
    /// name that is not one of the four, is refused with the sentence to answer with.
    /// </summary>
    public static bool TryRead(IReadOnlyList<string>? names, ReasonUse fallback, out ReasonUse use, out string? problem)
    {
        use = fallback;
        problem = null;

        if (names is null)
            return true;

        var read = ReasonUse.None;

        foreach (var name in names)
        {
            var known = Known.FirstOrDefault(k => string.Equals(k.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (known.Use == ReasonUse.None)
            {
                problem = $"'{name}' is not an action a reason can be used for. Use ban, kick, unban or reject.";
                return false;
            }

            read |= known.Use;
        }

        if (read == ReasonUse.None)
        {
            problem = "Pick at least one action this reason is used for.";
            return false;
        }

        use = read;
        return true;
    }
}

/// <summary>
/// Reads the reason list, seeding the defaults the first time anybody asks.
/// </summary>
/// <remarks>
/// Seeded on first read rather than by the migration so the timestamps come from
/// <c>IModbotClock</c> like every other time Modbot stamps, and so the default set can change
/// between releases without a data migration -- a deployment that already has a list keeps it.
/// The same shape as <c>GetSettingsAsync</c>: "the rows might not exist yet" is handled once.
/// </remarks>
public static class BanReasonList
{
    /// <summary>
    /// The starting list. Plain words; each "Other" is the one that needs a written reason. The
    /// reasons for lifting a ban are a list of their own (M4 §9): mistake, appeal upheld, time
    /// served. The same four are added to lists that existed before they did, by the migration that
    /// introduced them (<c>GiveUnbansTheirOwnReasons</c>).
    /// </summary>
    public static readonly IReadOnlyList<(string Label, string Description, bool NeedsWrittenReason, ReasonUse UsedFor)> Defaults =
    [
        ("Harassment", "Targeting, bullying or threatening people.", false, ReasonUses.WhenLeftOut),
        ("Hate speech", "Slurs, bigotry, or content attacking people for who they are.", false, ReasonUses.WhenLeftOut),
        ("Crashing or malicious avatars", "Avatars or tricks built to crash, lag or exploit other players.", false, ReasonUses.WhenLeftOut),
        ("Underage", "Under VRChat's minimum age, or under the group's own.", false, ReasonUses.WhenLeftOut),
        ("Ban evasion", "Back on another account after being banned.", false, ReasonUses.WhenLeftOut),
        ("Spam", "Flooding, advertising, or repeated unwanted messages.", false, ReasonUses.WhenLeftOut),
        ("Other", "None of the above. Say why in the written reason.", true, ReasonUses.WhenLeftOut),
        ("Mistake", "The ban should not have happened.", false, ReasonUse.Unban),
        ("Appeal upheld", "They asked to come back and the team agreed.", false, ReasonUse.Unban),
        ("Time served", "The ban was only ever meant to last this long.", false, ReasonUse.Unban),
        ("Other", "None of the above. Say why in the note.", true, ReasonUse.Unban),
    ];

    public static async Task<IReadOnlyList<BanReason>> AllAsync(
        ModbotContext db, IModbotClock clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        var rows = await db.BanReasons.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.CreatedAt).ToListAsync(ct);
        if (rows.Count > 0)
            return rows;

        var now = clock.UtcNow;
        var seeded = Defaults.Select((d, i) => new BanReason
        {
            Label = d.Label,
            Description = d.Description,
            NeedsWrittenReason = d.NeedsWrittenReason,
            UsedFor = d.UsedFor,
            SortOrder = i,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();

        db.BanReasons.AddRange(seeded);
        await db.SaveChangesAsync(ct);

        foreach (var reason in seeded)
            db.Entry(reason).State = EntityState.Detached;

        return seeded;
    }
}
