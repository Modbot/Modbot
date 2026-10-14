using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Settings;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordSnapshots;

/// <summary>One snapshot in the list.</summary>
/// <param name="TakenBy">Who pressed the button; null for one the schedule took.</param>
public sealed record DiscordSnapshotRow(
    Guid Id, DateTimeOffset TakenAt, bool Scheduled, string? TakenBy, int RoleCount, int ChannelCount);

/// <param name="Schedule">One of <c>off</c>, <c>weekly</c>, <c>every-two-weeks</c> or <c>monthly</c>.</param>
/// <param name="NextDue">When the schedule takes the next one; null while it is off or when the first is due now.</param>
/// <param name="RestoreOn">Putting permissions back is allowed (Settings → Discord → Server snapshots).</param>
public sealed record DiscordSnapshotsResponse(
    IReadOnlyList<DiscordSnapshotRow> Snapshots, string Schedule, DateTimeOffset? NextDue, bool RestoreOn, int MostKept);

/// <summary>A role in a snapshot. <see cref="Permissions"/> are the bits; <see cref="PermissionNames"/> say them in words.</summary>
public sealed record DiscordSnapshotRoleView(
    string Id,
    string Name,
    int Color,
    int Position,
    long Permissions,
    IReadOnlyList<string> PermissionNames,
    bool Hoisted,
    bool Mentionable,
    bool Managed,
    bool Everyone);

/// <summary>What a channel's own settings say about one role.</summary>
public sealed record DiscordSnapshotOverwriteView(
    string RoleId,
    string RoleName,
    long Allow,
    IReadOnlyList<string> AllowNames,
    long Deny,
    IReadOnlyList<string> DenyNames);

public sealed record DiscordSnapshotChannelView(
    string Id,
    string Name,
    string Type,
    string? CategoryId,
    int Position,
    bool Nsfw,
    IReadOnlyList<DiscordSnapshotOverwriteView> Overwrites);

/// <summary>One snapshot in full. This is also what the download file holds.</summary>
public sealed record DiscordSnapshotDetail(
    Guid Id,
    DateTimeOffset TakenAt,
    bool Scheduled,
    string? TakenBy,
    string GuildName,
    IReadOnlyList<DiscordSnapshotRoleView> Roles,
    IReadOnlyList<DiscordSnapshotChannelView> Channels);

/// <param name="ToNow">The second side is the server as it is now, not a snapshot.</param>
/// <param name="Error">Why the server could not be read, when <paramref name="ToNow"/> and it could not.</param>
public sealed record DiscordSnapshotCompareResponse(
    Guid FromId,
    DateTimeOffset FromTakenAt,
    Guid? ToId,
    DateTimeOffset? ToTakenAt,
    bool ToNow,
    IReadOnlyList<SnapshotChange> Changes,
    string? Error);

/// <param name="Kind"><c>role</c> or <c>channel</c>.</param>
/// <param name="TargetId">The Discord id of the role or channel.</param>
public sealed record DiscordSnapshotRestoreBody(string Kind, string TargetId, string? Fingerprint);

/// <summary>What a restore would change, from the server as it is now.</summary>
/// <param name="Lines">The exact change, one sentence each. Empty when nothing would change.</param>
/// <param name="Problem">Why it cannot run, or null.</param>
/// <param name="MissingPermission">The problem is a permission the bot lacks.</param>
/// <param name="Fingerprint">Sent back with the restore, which is refused if the server no longer gives the same change.</param>
public sealed record DiscordSnapshotRestorePreview(
    string Kind,
    string TargetId,
    string Name,
    IReadOnlyList<string> Lines,
    string? Problem,
    bool MissingPermission,
    bool NothingToChange,
    string Fingerprint);

public sealed record DiscordSnapshotRestoreResponse(
    bool Done, string? Error, bool MissingPermission, int Applied, IReadOnlyList<string> Lines);

/// <summary>
/// Server snapshots: the Discord server's roles, channels and permissions saved on a schedule or by
/// hand, what changed between two of them, a download, and putting one role's or one channel's
/// permissions back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Seeing is analytics, changing is settings.</strong> The list, a snapshot, a comparison and the
/// download are under <see cref="ModbotPermissions.ViewAnalytics"/>, like the rest of the Discord page;
/// taking one, deleting one and restoring need <see cref="ModbotPermissions.ManageSettings"/>.
/// </para>
/// <para>
/// <strong>Restoring is the one step that changes the live server.</strong> It is off until Settings →
/// Discord → Server snapshots turns it on (the bot's link then asks for Manage Roles). A restore
/// shows the exact change first; the press that runs it carries that change's fingerprint, and the
/// server works the change out again and refuses if it differs. Each is recorded in the audit log.
/// </para>
/// </remarks>
public static class DiscordSnapshotEndpoints
{
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IEndpointRouteBuilder MapDiscordSnapshots(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord/snapshots").WithTags("Discord");

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                var rows = await db.DiscordSnapshots.AsNoTracking()
                    .OrderByDescending(s => s.TakenAt)
                    .Select(s => new DiscordSnapshotRow(s.Id, s.TakenAt, s.Scheduled, s.TakenBy, s.RoleCount, s.ChannelCount))
                    .ToListAsync(ct);

                DateTimeOffset? next = rows.Count > 0
                    ? DiscordSnapshotSchedules.NextAfter(settings.DiscordSnapshotSchedule, rows[0].TakenAt)
                    : null;

                return Results.Ok(new DiscordSnapshotsResponse(
                    rows, settings.DiscordSnapshotSchedule, next, settings.DiscordSnapshotRestoreOn, DiscordSnapshotLimits.MostKept));
            })
            .WithName("ListDiscordSnapshots")
            .WithSummary("List server snapshots")
            .WithDescription(
                "The saved snapshots of the Discord server's roles and channels, newest first, with the schedule "
                + "and when the next one is due.")
            .Produces<DiscordSnapshotsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ViewAnalytics);

        group.MapPost("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordSnapshotActions discord,
                CancellationToken ct) =>
            {
                if (await db.DiscordSnapshots.CountAsync(ct) >= DiscordSnapshotLimits.MostKept)
                    return Results.BadRequest(new { error = $"There are already {DiscordSnapshotLimits.MostKept} snapshots. Delete one first." });

                var read = await discord.ReadAsync(ct);
                if (read.Structure is not { } structure)
                    return Results.Json(new { error = read.Error }, statusCode: StatusCodes.Status503ServiceUnavailable);

                var saved = await DiscordSnapshotStore.SaveAsync(
                    db, clock, structure.Content, scheduled: false, Actor.Of(http)?.Username, ct);

                return Results.Ok(new DiscordSnapshotRow(
                    saved.Id, saved.TakenAt, saved.Scheduled, saved.TakenBy, saved.RoleCount, saved.ChannelCount));
            })
            .WithName("TakeDiscordSnapshot")
            .WithSummary("Take a snapshot now")
            .WithDescription(
                "Saves the Discord server's roles, channels and permissions as they are now. Needs the bot connected. "
                + "At most 100 are kept; past that, delete one first.")
            .Produces<DiscordSnapshotRow>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/{id:guid}", async (Guid id, [FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var detail = await DetailAsync(db, id, ct);
                return detail is null ? Results.NotFound(new { error = "No such snapshot." }) : Results.Ok(detail);
            })
            .WithName("GetDiscordSnapshot")
            .WithSummary("Get a snapshot")
            .WithDescription("Every role and channel in the snapshot, with its permissions in words.")
            .Produces<DiscordSnapshotDetail>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ViewAnalytics);

        group.MapGet("/{id:guid}/download", async (Guid id, [FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var detail = await DetailAsync(db, id, ct);
                if (detail is null)
                    return Results.NotFound(new { error = "No such snapshot." });

                var bytes = JsonSerializer.SerializeToUtf8Bytes(detail, FileJson);
                return Results.File(bytes, "application/json", $"modbot-snapshot-{detail.TakenAt.UtcDateTime:yyyy-MM-dd-HHmm}.json");
            })
            .WithName("DownloadDiscordSnapshot")
            .WithSummary("Download a snapshot as a file")
            .WithDescription("The snapshot as a JSON file: roles and channels, each permission set as its number and in words.")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ViewAnalytics);

        group.MapDelete("/{id:guid}", async (
                HttpContext http, Guid id, [FromServices] ModbotContext db, CancellationToken ct) =>
            {
                var snapshot = await db.DiscordSnapshots.FirstOrDefaultAsync(s => s.Id == id, ct);
                if (snapshot is null)
                    return Results.NotFound(new { error = "No such snapshot." });

                var was = $"{snapshot.TakenAt:yyyy-MM-dd HH:mm} UTC";

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                db.DiscordSnapshots.Remove(snapshot);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordSnapshots")
                    .Items<string>("deleted", [was], [])
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);
                return Results.NoContent();
            })
            .WithName("DeleteDiscordSnapshot")
            .WithSummary("Delete a snapshot")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/compare", async (
                [FromQuery] Guid from,
                [FromQuery] string? to,
                [FromServices] ModbotContext db,
                [FromServices] IDiscordSnapshotActions discord,
                CancellationToken ct) =>
            {
                var before = await db.DiscordSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == from, ct);
                if (before is null || ServerSnapshotContent.FromJson(before.Content) is not { } beforeContent)
                    return Results.NotFound(new { error = "No such snapshot." });

                if (string.Equals(to, "now", StringComparison.OrdinalIgnoreCase))
                {
                    var read = await discord.ReadAsync(ct);
                    if (read.Structure is not { } live)
                    {
                        return Results.Ok(new DiscordSnapshotCompareResponse(
                            before.Id, before.TakenAt, null, null, true, [], read.Error));
                    }

                    return Results.Ok(new DiscordSnapshotCompareResponse(
                        before.Id, before.TakenAt, null, null, true, ServerSnapshotCompare.Compare(beforeContent, live.Content), null));
                }

                if (!Guid.TryParse(to, out var toId))
                    return Results.BadRequest(new { error = "Pick the snapshot to compare with, or \"now\"." });

                var after = await db.DiscordSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == toId, ct);
                if (after is null || ServerSnapshotContent.FromJson(after.Content) is not { } afterContent)
                    return Results.NotFound(new { error = "No such snapshot." });

                return Results.Ok(new DiscordSnapshotCompareResponse(
                    before.Id, before.TakenAt, after.Id, after.TakenAt, false, ServerSnapshotCompare.Compare(beforeContent, afterContent), null));
            })
            .WithName("CompareDiscordSnapshots")
            .WithSummary("Compare two snapshots")
            .WithDescription(
                "What changed in roles and channels from the snapshot `from` to the snapshot `to`, or to the server as it is "
                + "now when `to` is `now`. Roles and channels are told apart by their Discord id, so a rename shows as a change.")
            .Produces<DiscordSnapshotCompareResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ViewAnalytics);

        group.MapPost("/{id:guid}/restore-preview", async (
                Guid id,
                [FromBody] DiscordSnapshotRestoreBody body,
                [FromServices] ModbotContext db,
                [FromServices] IDiscordSnapshotActions discord,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var result = await PlanAsync(db, discord, id, body, ct);
                if (result.Failure is not null)
                    return result.Failure;

                var plan = result.Plan!;
                return Results.Ok(new DiscordSnapshotRestorePreview(
                    plan.Kind,
                    plan.Id,
                    plan.Name,
                    plan.Lines,
                    plan.Problem,
                    plan.MissingPermission,
                    plan.NothingToChange,
                    plan.Fingerprint));
            })
            .WithName("PreviewDiscordSnapshotRestore")
            .WithSummary("Show what a restore would change")
            .WithDescription(
                "Works out, from the server as it is now, exactly what putting one role's or one channel's permissions "
                + "back from the snapshot would change. Changes nothing.")
            .Accepts<DiscordSnapshotRestoreBody>("application/json")
            .Produces<DiscordSnapshotRestorePreview>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/{id:guid}/restore", async (
                HttpContext http,
                Guid id,
                [FromBody] DiscordSnapshotRestoreBody body,
                [FromServices] ModbotContext db,
                [FromServices] IDiscordSnapshotActions discord,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var result = await PlanAsync(db, discord, id, body, ct);
                if (result.Failure is not null)
                    return result.Failure;

                var plan = result.Plan!;

                if (plan.Problem is not null)
                    return Results.BadRequest(new { error = plan.Problem });

                if (plan.NothingToChange)
                    return Results.BadRequest(new { error = "Nothing to change: the server already has these permissions." });

                if (!string.Equals(body.Fingerprint, plan.Fingerprint, StringComparison.Ordinal))
                    return Results.Conflict(new { error = "The server changed since the preview. Look at the change again." });

                var reason = $"Modbot: permissions put back from the snapshot of {result.TakenAt:yyyy-MM-dd HH:mm} UTC";
                var edit = plan.Kind == SnapshotChangeKinds.Role
                    ? await discord.SetRolePermissionsAsync(plan.Id, plan.RolePermissions!.Value, reason, ct)
                    : await discord.SetChannelOverwritesAsync(plan.Id, plan.Set, plan.Remove, reason, ct);

                if (edit.BotOffline)
                    return Results.Json(new { error = edit.Error }, statusCode: StatusCodes.Status503ServiceUnavailable);

                if (edit.Applied > 0)
                {
                    await facts.RecordAsync(
                        FactType.DiscordSnapshotRestored,
                        id.ToString(),
                        Actor.Of(http),
                        new JsonObject
                        {
                            ["kind"] = plan.Kind,
                            ["targetId"] = plan.Id,
                            ["name"] = plan.Name,
                            ["changes"] = new JsonArray(plan.Lines.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
                            ["applied"] = edit.Applied,
                            ["error"] = edit.Done ? null : edit.Error,
                        },
                        ct);
                }

                return Results.Ok(new DiscordSnapshotRestoreResponse(
                    edit.Done, edit.Done ? null : edit.Error, edit.MissingPermission, edit.Applied, plan.Lines));
            })
            .WithName("RestoreDiscordSnapshot")
            .WithSummary("Put a role's or a channel's permissions back")
            .WithDescription(
                "Puts one role's server-wide permissions, or one channel's permissions for roles, back to what the "
                + "snapshot has. Needs Restore turned on in Settings, the bot connected and Manage Roles. The body carries "
                + "the fingerprint from the preview; if the server no longer gives the same change the answer is 409. "
                + "Settings for single members in a channel are left alone. Recorded in the audit log.")
            .Accepts<DiscordSnapshotRestoreBody>("application/json")
            .Produces<DiscordSnapshotRestoreResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private sealed record PlanResult(IResult? Failure, SnapshotRestorePlan? Plan, DateTimeOffset TakenAt);

    /// <summary>The restore for a request, worked out against the server as it is now.</summary>
    private static async Task<PlanResult> PlanAsync(
        ModbotContext db, IDiscordSnapshotActions discord, Guid id, DiscordSnapshotRestoreBody body, CancellationToken ct)
    {
        if (!(await db.GetSettingsAsync(ct)).DiscordSnapshotRestoreOn)
        {
            return new PlanResult(
                Results.BadRequest(new { error = "Restoring is off. Turn on Allow restore in Settings → Discord → Server snapshots." }),
                null,
                default);
        }

        if (body.Kind is not (SnapshotChangeKinds.Role or SnapshotChangeKinds.Channel) || string.IsNullOrWhiteSpace(body.TargetId))
            return new PlanResult(Results.BadRequest(new { error = "Pick a role or a channel to restore." }), null, default);

        var snapshot = await db.DiscordSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (snapshot is null || ServerSnapshotContent.FromJson(snapshot.Content) is not { } content)
            return new PlanResult(Results.NotFound(new { error = "No such snapshot." }), null, default);

        var read = await discord.ReadAsync(ct);
        if (read.Structure is not { } live)
            return new PlanResult(Results.Json(new { error = read.Error }, statusCode: StatusCodes.Status503ServiceUnavailable), null, default);

        var target = body.TargetId.Trim();
        var plan = body.Kind == SnapshotChangeKinds.Role
            ? ServerSnapshotCompare.PlanRole(content, live, target)
            : ServerSnapshotCompare.PlanChannel(content, live, target);

        return new PlanResult(null, plan, snapshot.TakenAt);
    }

    private static async Task<DiscordSnapshotDetail?> DetailAsync(ModbotContext db, Guid id, CancellationToken ct)
    {
        var snapshot = await db.DiscordSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (snapshot is null || ServerSnapshotContent.FromJson(snapshot.Content) is not { } content)
            return null;

        var roleNames = content.Roles.ToDictionary(r => r.Id, r => r.Name);

        return new DiscordSnapshotDetail(
            snapshot.Id,
            snapshot.TakenAt,
            snapshot.Scheduled,
            snapshot.TakenBy,
            content.GuildName,
            [.. content.Roles.Select(r => new DiscordSnapshotRoleView(
                r.Id, r.Name, r.Color, r.Position, r.Permissions, DiscordPermissionNames.Of(r.Permissions),
                r.Hoisted, r.Mentionable, r.Managed, r.Everyone))],
            [.. content.Channels.Select(c => new DiscordSnapshotChannelView(
                c.Id, c.Name, c.Type, c.CategoryId, c.Position, c.Nsfw,
                [.. c.Overwrites.Select(o => new DiscordSnapshotOverwriteView(
                    o.RoleId,
                    roleNames.TryGetValue(o.RoleId, out var name) ? name : o.RoleId,
                    o.Allow, DiscordPermissionNames.Of(o.Allow),
                    o.Deny, DiscordPermissionNames.Of(o.Deny)))]))]);
    }
}
