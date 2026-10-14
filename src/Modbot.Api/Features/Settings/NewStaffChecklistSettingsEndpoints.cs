using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>The lines of the new staff checklist, as stored.</summary>
/// <param name="Tasks">What a new staff member does and fills in themselves, one written answer each.</param>
/// <param name="SignOffs">What their buddy signs off.</param>
/// <param name="ReminderChannelId">The Discord channel the day-30 reminders and the 30-day check-ins are posted in. Null means off.</param>
/// <param name="MostItemsPerKind">The most lines of each kind.</param>
/// <param name="MostTextLength">The most characters in one line.</param>
public sealed record NewStaffChecklistSettingsResponse(
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string> SignOffs,
    string? ReminderChannelId,
    int MostItemsPerKind,
    int MostTextLength);

/// <summary>
/// A change to the new staff checklist. Both lists are sent whole, in the order they are shown; so is the
/// reminder channel, where empty means off.
/// </summary>
public sealed record NewStaffChecklistSettingsUpdate(List<string>? Tasks, List<string>? SignOffs, string? ReminderChannelId);

/// <summary>
/// Settings → People and roles → New staff: the two lists of lines a new staff member's checklist is
/// made from, and the Discord channel the day-30 reminder and the 30-day check-in are posted in.
/// Needs Manage users, as starting a trial does. A change to the lines reaches trials started after it;
/// a trial keeps the lines it was given. Every change is recorded as a settings change. The channel is
/// an opaque Discord id and is never checked for its shape.
/// </summary>
public static class NewStaffChecklistSettingsEndpoints
{
    public static IEndpointRouteBuilder MapNewStaffChecklistSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/new-staff-checklist")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageUsers);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetNewStaffChecklistSettings")
            .WithSummary("Get the new staff checklist")
            .WithDescription("The tasks a new staff member fills in themselves, the lines their buddy signs off, and the channel the reminders are posted in.")
            .Produces<NewStaffChecklistSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] NewStaffChecklistSettingsUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var tasks = Clean(body.Tasks);
                var signOffs = Clean(body.SignOffs);

                if (tasks.Count > StaffTrialLimits.MostItemsPerKind || signOffs.Count > StaffTrialLimits.MostItemsPerKind)
                    return Results.BadRequest(new { error = $"At most {StaffTrialLimits.MostItemsPerKind} lines of each kind." });

                if (tasks.Concat(signOffs).Any(line => line.Length > StaffTrialLimits.ItemTextLength))
                    return Results.BadRequest(new { error = $"A line is at most {StaffTrialLimits.ItemTextLength} characters." });

                var channelId = string.IsNullOrWhiteSpace(body.ReminderChannelId) ? null : body.ReminderChannelId.Trim();

                var settings = await db.GetSettingsAsync(ct);

                var change = new SettingsChange("newStaffChecklist")
                    .Items("tasks", settings.NewStaffTasks ?? [], tasks)
                    .Items("signOffs", settings.NewStaffSignOffs ?? [], signOffs)
                    .Field("reminderChannelId", settings.StaffRemindersChannelId, channelId);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.NewStaffTasks = tasks;
                settings.NewStaffSignOffs = signOffs;
                settings.StaffRemindersChannelId = channelId;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetNewStaffChecklistSettings")
            .WithSummary("Update the new staff checklist")
            .WithDescription(
                "Replace both lists and the reminder channel. Blank lines are dropped, and an empty channel turns the reminders off. "
                + "A trial already started keeps the lines it was given; the change reaches trials started after it.")
            .Accepts<NewStaffChecklistSettingsUpdate>("application/json")
            .Produces<NewStaffChecklistSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static List<string> Clean(List<string>? lines)
        => (lines ?? []).Select(line => line?.Trim() ?? string.Empty).Where(line => line.Length > 0).ToList();

    private static NewStaffChecklistSettingsResponse View(Core.Data.Entities.Settings settings)
        => new(
            settings.NewStaffTasks ?? [],
            settings.NewStaffSignOffs ?? [],
            settings.StaffRemindersChannelId,
            StaffTrialLimits.MostItemsPerKind,
            StaffTrialLimits.ItemTextLength);
}
