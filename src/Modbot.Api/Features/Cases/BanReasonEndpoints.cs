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
                    held.HasFlag(ModbotPermissions.Administrator) || held.HasFlag(ModbotPermissions.EditClassifications)));
            })
            .WithName("GetBanReasons")
            .WithSummary("The reasons a moderator picks from when writing up a ban")
            .WithDescription(
                "In button order, switched-off ones included with isActive false. Seeded with a "
                + "plain default set the first time anybody asks, so the first case file can be "
                + "written without inventing a list first.")
            .Produces<BanReasonListResponse>();

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
                    },
                    $"Ban reason \"{reason.Label}\" added",
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(reason));
            })
            .RequiresFlag(ModbotPermissions.EditClassifications)
            .WithName("CreateBanReason")
            .WithSummary("Add a reason to the end of the list")
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
                    true));

                static void Place(BanReason reason, int order, DateTimeOffset now)
                {
                    if (reason.SortOrder == order) return;
                    reason.SortOrder = order;
                    reason.UpdatedAt = now;
                }
            })
            .RequiresFlag(ModbotPermissions.EditClassifications)
            .WithName("ReorderBanReasons")
            .WithSummary("Put the reasons in this order")
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

                var before = new JsonObject
                {
                    ["label"] = reason.Label,
                    ["description"] = reason.Description,
                    ["needsWrittenReason"] = reason.NeedsWrittenReason,
                    ["isActive"] = reason.IsActive,
                };

                reason.Label = body.Label.Trim();
                reason.Description = body.Description?.Trim() ?? string.Empty;
                reason.NeedsWrittenReason = body.NeedsWrittenReason;
                reason.IsActive = body.IsActive ?? reason.IsActive;
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
                        },
                    },
                    $"Ban reason \"{reason.Label}\" changed",
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(reason));
            })
            .RequiresFlag(ModbotPermissions.EditClassifications)
            .WithName("UpdateBanReason")
            .WithSummary("Reword a reason, or switch it on or off")
            .WithDescription(
                "There is no delete: case files cite reasons by id, and one that pointed at a "
                + "reason nobody can name any more would have lost its classification. Switch it "
                + "off instead; it stays on the case files that picked it and leaves the buttons.")
            .Produces<BanReasonView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

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
        => new(r.Id, r.Label, r.Description, r.SortOrder, r.IsActive, r.NeedsWrittenReason);
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
    /// <summary>The starting list. Plain words; "Other" is the one that needs a written reason.</summary>
    public static readonly IReadOnlyList<(string Label, string Description, bool NeedsWrittenReason)> Defaults =
    [
        ("Harassment", "Targeting, bullying or threatening people.", false),
        ("Hate speech", "Slurs, bigotry, or content attacking people for who they are.", false),
        ("Crashing or malicious avatars", "Avatars or tricks built to crash, lag or exploit other players.", false),
        ("Underage", "Under VRChat's minimum age, or under the group's own.", false),
        ("Ban evasion", "Back on another account after being banned.", false),
        ("Spam", "Flooding, advertising, or repeated unwanted messages.", false),
        ("Other", "None of the above. Say why in the written reason.", true),
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
