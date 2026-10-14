using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// Event templates: a name and everything the event form holds except its dates (calendar templates
/// plan, step 2).
/// </summary>
/// <remarks>
/// <para>
/// Manage calendar or Manage templates may read them; only Manage templates may make, change or
/// delete one. Nothing here makes an event or publishes anything: a template is stored words.
/// </para>
/// <para>
/// A save is checked by the event's own checks, with a made-up first date (<see cref="FirstStart"/>)
/// standing in for the dates a template does not have, and with the checks for what an event is only
/// refused for later -- no title, no channel, no world -- left out, as the form's preview leaves
/// them out. So what a template holds is what an event would hold, spelled and trimmed the same way,
/// and the form can load it as it is.
/// </para>
/// </remarks>
public static class CalendarTemplateEndpoints
{
    /// <summary>The date the event's checks are run against. Never stored; it only has to be a real Monday.</summary>
    private const string FirstStart = "2000-01-03T00:00";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapCalendarTemplates(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/calendar/templates").WithTags("Calendar templates").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var held = ModbotAuth.PermissionsOf(http.User);
                var manage = ModbotAuth.Allows(held, ModbotPermissions.ManageTemplates);

                if (!manage && !ModbotAuth.Allows(held, ModbotPermissions.ManageCalendar))
                {
                    return Results.Json(
                        new { error = "Seeing templates needs Manage calendar or Manage templates." },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                var templates = await db.CalendarTemplates.AsNoTracking().OrderBy(t => t.Name).ThenBy(t => t.CreatedAt).ToListAsync(ct);

                return Results.Ok(new CalendarTemplatesView(await ViewsAsync(db, templates, ct), manage));
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar)
            .WithName("ListCalendarTemplates")
            .WithSummary("List event templates")
            .WithDescription(
                "Every template by name, with the event fields it fills in. Needs Manage calendar or "
                + "Manage templates; canManage says whether the caller may also change them.")
            .Produces<CalendarTemplatesView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] CalendarTemplateRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var (refused, name, fields) = await CheckAsync(http, db, body, kept: null, ct);
                if (refused is not null)
                    return refused;

                var now = clock.UtcNow;
                var template = new CalendarTemplate
                {
                    Id = Guid.CreateVersion7(),
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                Fill(template, name!, fields!);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.CalendarTemplates.Add(template);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.CalendarTemplateCreated, template.Id.ToString(), Actor.Of(http), new JsonObject { ["name"] = template.Name }, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok((await ViewsAsync(db, [template], ct))[0]);
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageTemplates)
            .WithName("CreateCalendarTemplate")
            .WithSummary("Add event template")
            .WithDescription(
                "Save a template: a name and the event fields, without dates. A refusal (400) lists "
                + "everything wrong at once, in problems, and joins it in error. A title, a channel or "
                + "a world may be left empty; the event made from it is refused for them instead.")
            .Produces<CalendarTemplateView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] CalendarTemplateRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var template = await db.CalendarTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (template is null)
                    return Results.NotFound();

                var (refused, name, fields) = await CheckAsync(http, db, body, template, ct);
                if (refused is not null)
                    return refused;

                // Compared as the fields an event form would load, written out the same way. Postgres hands
                // jsonb back in its own key order and spacing, so the stored text itself is no basis.
                var before = (template.Name, Fields: JsonSerializer.Serialize(FieldsOf(template), Json));
                var previousName = template.Name;

                Fill(template, name!, fields!);

                if (before == (template.Name, JsonSerializer.Serialize(FieldsOf(template), Json)))
                    return Results.Ok((await ViewsAsync(db, [template], ct))[0]);

                template.UpdatedAt = clock.UtcNow;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.CalendarTemplateChanged,
                    template.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["name"] = template.Name, ["before"] = previousName },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok((await ViewsAsync(db, [template], ct))[0]);
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageTemplates)
            .WithName("UpdateCalendarTemplate")
            .WithSummary("Update event template")
            .WithDescription("Replace a template's name and fields. Events already made from it do not change.")
            .Produces<CalendarTemplateView>()
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
                var template = await db.CalendarTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (template is null)
                    return Results.NotFound();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.CalendarTemplates.Remove(template);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.CalendarTemplateDeleted, template.Id.ToString(), Actor.Of(http), new JsonObject { ["name"] = template.Name }, ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageTemplates)
            .WithName("DeleteCalendarTemplate")
            .WithSummary("Delete event template")
            .WithDescription("Delete a template. Events already made from it stay as they are.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>The fields a template holds, with the picture and world list put back from their columns.</summary>
    internal static CalendarTemplateFields FieldsOf(CalendarTemplate template)
    {
        var fields = JsonSerializer.Deserialize<CalendarTemplateFields>(template.Fields, Json)
            ?? throw new InvalidOperationException($"Template {template.Id} has no fields.");

        // A template saved before categories were stored has none: read as an empty list, so an
        // unchanged save compares equal.
        return fields with
        {
            CoverPictureId = template.CoverPictureId,
            WorldListId = template.WorldListId,
            CategoryIds = fields.CategoryIds ?? [],
        };
    }

    private static void Fill(CalendarTemplate template, string name, CalendarTemplateFields fields)
    {
        template.Name = name;
        template.CoverPictureId = fields.CoverPictureId;
        template.WorldListId = fields.WorldListId;
        template.Fields = JsonSerializer.Serialize(fields with { CoverPictureId = null, WorldListId = null }, Json);
    }

    private static async Task<List<CalendarTemplateView>> ViewsAsync(
        ModbotContext db, IReadOnlyList<CalendarTemplate> templates, CancellationToken ct)
    {
        var makers = templates.Select(t => t.CreatedByUserId).OfType<Guid>().Distinct().ToList();

        var names = await db.Users.AsNoTracking()
            .Where(u => makers.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        // A category deleted since the template was saved is left out, so the form never offers it.
        var parsed = templates.ToDictionary(t => t.Id, FieldsOf);
        var named = parsed.Values.SelectMany(f => f.CategoryIds ?? []).Distinct().ToList();

        var existing = named.Count == 0
            ? []
            : (await db.CalendarCategories.AsNoTracking().Where(c => named.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)).ToHashSet();

        return
        [
            .. templates.Select(t => new CalendarTemplateView(
                t.Id,
                t.Name,
                parsed[t.Id] with { CategoryIds = [.. (parsed[t.Id].CategoryIds ?? []).Where(existing.Contains)] },
                t.CreatedByUserId is { } maker && names.TryGetValue(maker, out var username) ? username : null,
                t.CreatedAt,
                t.UpdatedAt)),
        ];
    }

    /// <summary>
    /// Checks a save. Answers the refusal, or null with the trimmed name and the fields as an event
    /// would keep them.
    /// </summary>
    /// <param name="kept">The template being changed: what it already holds is not checked again.</param>
    private static async Task<(IResult? Refused, string? Name, CalendarTemplateFields? Fields)> CheckAsync(
        HttpContext http, ModbotContext db, CalendarTemplateRequest body, CalendarTemplate? kept, CancellationToken ct)
    {
        if (body.Fields is null)
            return (Results.BadRequest(new { error = "Send the template's fields." }), null, null);

        var problems = new List<string>();

        var name = body.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            problems.Add("Give the template a name.");
        else if (name.Length > CalendarTemplate.MaxNameLength)
            problems.Add($"The name is longer than {CalendarTemplate.MaxNameLength} characters.");

        var longest = (int)CalendarEndpoints.MaxLength.TotalMinutes;
        var minutes = body.Fields.LengthMinutes;

        if (minutes < 1 || minutes > longest)
        {
            problems.Add("An event can last from 1 minute to 7 days.");
            minutes = 60;
        }

        var keptFields = kept is null ? null : FieldsOf(kept);
        var asEvent = AsEvent(body.Fields, minutes);
        var target = new CalendarEvent();

        problems.AddRange(CalendarEndpoints.ApplyAll(asEvent, target, preview: true));

        if (await CalendarEndpoints.ListProblemAsync(db, asEvent, ct) is { } listProblem)
            problems.Add(listProblem);

        if (await CalendarEndpoints.MentionProblemAsync(db, asEvent, keptFields?.MentionRoleId, ct) is { } mentionProblem)
            problems.Add(mentionProblem);

        if (await CalendarEndpoints.CoverProblemAsync(db, asEvent, keptFields?.CoverPictureId, ct) is { } coverProblem)
            problems.Add(coverProblem);

        var categories = await CalendarEndpoints.CategoriesAsync(db, asEvent, problems, ct);

        if (problems.Count > 0)
            return (Results.BadRequest(new { error = string.Join(" ", problems), problems }), null, null);

        var keptStaff = keptFields is null
            ? []
            : (keptFields.InviteStaffUserIds ?? []).Concat(keptFields.InviteHostUserId is { } host ? [host] : []).ToList();

        if (await CalendarEndpoints.CheckInvitesAsync(http, db, asEvent, keptFields?.InviteListId, keptStaff, ct, onTemplate: true) is { } refused)
            return (refused, null, null);

        var fields = body.Fields;
        var repeatDays = (fields.RepeatDays ?? []).Select(d => d.Trim().ToUpperInvariant()).ToHashSet();

        return (null, name, new CalendarTemplateFields(
            Title: target.Title,
            Description: target.Description,
            LengthMinutes: minutes,
            Repeat: target.Repeat,
            RepeatDays: target.Repeat == CalendarRepeats.Weekly ? [.. CalendarRepeats.Days.Where(repeatDays.Contains)] : [],
            RepeatEvery: target.RepeatEvery,
            RepeatTimes: target.RepeatTimes,
            WorldId: target.WorldId,
            WorldListId: target.WorldListId,
            AccessType: target.AccessType,
            Region: target.Region,
            ImageUrl: target.ImageUrl,
            VRChatImageId: target.VRChatImageId,
            CoverPictureId: target.CoverPictureId,
            Category: target.Category,
            Languages: target.Languages,
            Platforms: target.Platforms,
            Tags: target.Tags,
            Visibility: target.Visibility,
            NotifyMembers: target.NotifyMembers,
            Featured: fields.Featured,
            PublishToVRChat: target.PublishToVRChat,
            PublishToDiscord: target.PublishToDiscord,
            PublishToGoogle: fields.PublishToGoogle,
            PostToChannel: target.PostToChannel,
            ChannelId: target.ChannelId,
            MentionRoleId: target.MentionRoleId,
            AutoOpen: target.AutoOpen,
            OpenMinutesBefore: target.OpenMinutesBefore,
            InviteHostUserId: target.InviteHostUserId,
            InviteStaffUserIds: target.InviteStaffUserIds,
            InviteListId: target.InviteListId,
            AnnounceFirstJoinInDiscord: target.AnnounceFirstJoinInDiscord,
            AnnounceFirstJoinInVRChat: target.AnnounceFirstJoinInVRChat,
            VRChatTitle: target.VRChatTitle,
            VRChatDescription: target.VRChatDescription,
            DiscordTitle: target.DiscordTitle,
            DiscordDescription: target.DiscordDescription,
            GoogleTitle: target.GoogleTitle,
            GoogleDescription: target.GoogleDescription,
            CategoryIds: [.. (categories ?? []).Select(c => c.Id).Order()],
            PublishToBluesky: fields.PublishToBluesky,
            BlueskyPostBeforeMinutes: fields.BlueskyPostBeforeMinutes,
            BlueskyTitle: target.BlueskyTitle,
            BlueskyDescription: target.BlueskyDescription));
    }

    /// <summary>
    /// The fields as an event request, on a made-up first date and in UTC, a draft: what the event's
    /// own checks are given to check.
    /// </summary>
    private static CalendarEventRequest AsEvent(CalendarTemplateFields f, int minutes)
    {
        var ends = new DateTime(2000, 1, 3).AddMinutes(minutes);

        return new CalendarEventRequest(
            Title: f.Title,
            Description: f.Description,
            StartsAt: FirstStart,
            EndsAt: ends.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            TimeZone: "UTC",
            Repeat: f.Repeat,
            RepeatDays: f.RepeatDays,
            RepeatUntil: null,
            WorldId: f.WorldId,
            AccessType: f.AccessType,
            Region: f.Region,
            ImageUrl: f.ImageUrl,
            VRChatImageId: f.VRChatImageId,
            Category: f.Category,
            Languages: f.Languages,
            Platforms: f.Platforms,
            Tags: f.Tags,
            Visibility: f.Visibility,
            NotifyMembers: f.NotifyMembers,
            PublishToVRChat: f.PublishToVRChat,
            PublishToDiscord: f.PublishToDiscord,
            PostToChannel: f.PostToChannel,
            ChannelId: f.ChannelId,
            AutoOpen: f.AutoOpen,
            OpenMinutesBefore: f.OpenMinutesBefore,
            Draft: true,
            WorldListId: f.WorldListId,
            InviteHostUserId: f.InviteHostUserId,
            InviteStaffUserIds: f.InviteStaffUserIds,
            InviteListId: f.InviteListId,
            AnnounceFirstJoinInDiscord: f.AnnounceFirstJoinInDiscord,
            AnnounceFirstJoinInVRChat: f.AnnounceFirstJoinInVRChat,
            MentionRoleId: f.MentionRoleId,
            RepeatEvery: f.RepeatEvery,
            RepeatTimes: f.RepeatTimes,
            Featured: f.Featured,
            CoverPictureId: f.CoverPictureId,
            PublishToGoogle: f.PublishToGoogle,
            VRChatTitle: f.VRChatTitle,
            VRChatDescription: f.VRChatDescription,
            DiscordTitle: f.DiscordTitle,
            DiscordDescription: f.DiscordDescription,
            GoogleTitle: f.GoogleTitle,
            GoogleDescription: f.GoogleDescription,
            CategoryIds: f.CategoryIds,
            PublishToBluesky: f.PublishToBluesky,
            BlueskyTitle: f.BlueskyTitle,
            BlueskyDescription: f.BlueskyDescription,
            BlueskyPostBeforeMinutes: f.BlueskyPostBeforeMinutes);
    }
}
