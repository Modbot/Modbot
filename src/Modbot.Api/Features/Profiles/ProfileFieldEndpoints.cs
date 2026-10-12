using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Profiles;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Profiles;

/// <summary>One answer a pick-list field offers.</summary>
public sealed record ProfileOptionView(Guid Id, string Label);

/// <summary>One profile field.</summary>
/// <param name="Kind">pick-list or text.</param>
/// <param name="DefaultVisibility">only-me, staff or members: who sees the answer of a member who has not chosen.</param>
/// <param name="Options">Empty for a text field.</param>
public sealed record ProfileFieldView(
    Guid Id,
    string Name,
    string Kind,
    string DefaultVisibility,
    IReadOnlyList<ProfileOptionView> Options);

public sealed record ProfileFieldListResponse(IReadOnlyList<ProfileFieldView> Fields);

/// <param name="Id">The option this is, or null for a new one.</param>
public sealed record ProfileOptionRequest(Guid? Id, string Label);

/// <param name="Kind">pick-list or text. Only read when adding: a field's kind does not change.</param>
/// <param name="DefaultVisibility">
/// only-me, staff or members. Left out, a new field is staff. Only read when adding: a field's default
/// is fixed afterwards, and an update that sends a different one is refused.
/// </param>
/// <param name="Options">
/// A pick-list's whole list in order. An option with an id keeps its id and its members' answers;
/// one left out is removed.
/// </param>
public sealed record ProfileFieldRequest(
    string Name,
    string? Kind,
    string? DefaultVisibility,
    IReadOnlyList<ProfileOptionRequest>? Options);

public sealed record ProfileFieldOrderRequest(IReadOnlyList<Guid> Ids);

/// <summary>
/// Settings → Member profiles: the fields every member can fill in (member profiles design §2).
/// </summary>
/// <remarks>
/// Every change is a settings change in the audit log. Removing a field deletes the answers to it;
/// the screen asks first.
/// </remarks>
public static class ProfileFieldEndpoints
{
    public const string PickListName = "pick-list";
    public const string TextName = "text";

    public static IEndpointRouteBuilder MapProfileFields(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/profile-fields")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
                Results.Ok(new ProfileFieldListResponse([.. (await MemberProfiles.FieldsAsync(db, ct)).Select(View)])))
            .WithName("GetProfileFields")
            .WithSummary("List profile fields")
            .WithDescription("The questions on every member's profile, in the order members see them.")
            .Produces<ProfileFieldListResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] ProfileFieldRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var all = await db.ProfileFields.AsNoTracking().ToListAsync(ct);

                if (all.Count >= ProfileField.MaxFields)
                    return Results.BadRequest(new { error = $"A profile can have at most {ProfileField.MaxFields} fields." });

                if (!TryKind(body.Kind, out var kind))
                    return Results.BadRequest(new { error = "kind must be pick-list or text." });

                var visibility = ProfileVisibility.Staff;
                if (body.DefaultVisibility is not null && !TryVisibility(body.DefaultVisibility, out visibility))
                    return Results.BadRequest(new { error = "defaultVisibility must be only-me, staff or members." });

                var name = (body.Name ?? string.Empty).Trim();
                if (NameProblem(name, all, null) is { } nameProblem)
                    return Results.BadRequest(new { error = nameProblem });

                var now = clock.UtcNow;
                var field = new ProfileField
                {
                    Name = name,
                    Kind = kind,
                    DefaultVisibility = visibility,
                    SortOrder = all.Count == 0 ? 0 : all.Max(f => f.SortOrder) + 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                if (kind == ProfileFieldKind.PickList)
                {
                    var options = OptionsFrom(body.Options, existing: [], out var optionProblem);
                    if (optionProblem is not null)
                        return Results.BadRequest(new { error = optionProblem });

                    field.Options = options;
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                db.ProfileFields.Add(field);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("memberProfiles")
                    .Field<string?>("fieldAdded", null, field.Name)
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(View(field));
            })
            .WithName("CreateProfileField")
            .WithSummary("Add profile field")
            .WithDescription("Add a field to the end of the profile. A pick-list brings its options; a text field has none.")
            .Produces<ProfileFieldView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/order", async (
                HttpContext http,
                [FromBody] ProfileFieldOrderRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (body.Ids is null || body.Ids.Count == 0)
                    return Results.BadRequest(new { error = "ids is required." });

                var fields = await db.ProfileFields.Include(f => f.Options).ToListAsync(ct);
                var byId = fields.ToDictionary(f => f.Id);

                if (body.Ids.Any(id => !byId.ContainsKey(id)))
                    return Results.BadRequest(new { error = "One of those ids is not a profile field." });

                var before = fields.OrderBy(f => f.SortOrder).Select(f => f.Name).ToList();
                var now = clock.UtcNow;
                var position = 0;

                // Listed ones first, in the order given; anything left out keeps its relative order after them.
                foreach (var id in body.Ids.Distinct())
                    Place(byId[id], position++, now);

                foreach (var field in fields.Where(f => !body.Ids.Contains(f.Id)).OrderBy(f => f.SortOrder))
                    Place(field, position++, now);

                var after = fields.OrderBy(f => f.SortOrder).Select(f => f.Name).ToList();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("memberProfiles")
                    .Items("fieldOrder", before, after)
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(new ProfileFieldListResponse([.. fields.OrderBy(f => f.SortOrder).Select(View)]));

                static void Place(ProfileField field, int order, DateTimeOffset now)
                {
                    if (field.SortOrder == order) return;
                    field.SortOrder = order;
                    field.UpdatedAt = now;
                }
            })
            .WithName("ReorderProfileFields")
            .WithSummary("Reorder profile fields")
            .WithDescription("Put the fields in this order.")
            .Produces<ProfileFieldListResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] ProfileFieldRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var all = await db.ProfileFields.Include(f => f.Options).ToListAsync(ct);
                var field = all.FirstOrDefault(f => f.Id == id);
                if (field is null)
                    return Results.NotFound(new { error = "No such profile field." });

                if (body.DefaultVisibility is not null)
                {
                    if (!TryVisibility(body.DefaultVisibility, out var sent))
                        return Results.BadRequest(new { error = "defaultVisibility must be only-me, staff or members." });
                    if (sent != field.DefaultVisibility)
                        return Results.BadRequest(new { error = "Who sees a field is set when it is added and cannot be changed." });
                }

                var name = (body.Name ?? string.Empty).Trim();
                if (NameProblem(name, all, id) is { } nameProblem)
                    return Results.BadRequest(new { error = nameProblem });

                var change = new SettingsChange("memberProfiles");
                var now = clock.UtcNow;

                change.Field("fieldName", field.Name, name);

                field.Name = name;
                field.UpdatedAt = now;

                if (field.Kind == ProfileFieldKind.PickList && body.Options is not null)
                {
                    var wasLabels = field.Options.OrderBy(o => o.SortOrder).Select(o => o.Label).ToList();
                    var options = OptionsFrom(body.Options, field.Options, out var optionProblem);
                    if (optionProblem is not null)
                        return Results.BadRequest(new { error = optionProblem });

                    var keptIds = options.Select(o => o.Id).ToHashSet();
                    foreach (var gone in field.Options.Where(o => !keptIds.Contains(o.Id)).ToList())
                        db.ProfileOptions.Remove(gone);

                    foreach (var option in options)
                    {
                        if (field.Options.FirstOrDefault(o => o.Id == option.Id) is { } same)
                        {
                            same.Label = option.Label;
                            same.SortOrder = option.SortOrder;
                        }
                        else
                        {
                            option.FieldId = field.Id;
                            db.ProfileOptions.Add(option);
                        }
                    }

                    change.Items($"{name}: options", wasLabels, options.Select(o => o.Label));
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                var saved = (await MemberProfiles.FieldsAsync(db, ct)).First(f => f.Id == id);
                return Results.Ok(View(saved));
            })
            .WithName("UpdateProfileField")
            .WithSummary("Update profile field")
            .WithDescription(
                "Rename a field and, for a pick-list, replace its options. "
                + "An option sent with its id keeps the members who picked it; one left out is removed and "
                + "clears their answer. The kind and who sees the field by default do not change; sending a different default is refused.")
            .Produces<ProfileFieldView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var field = await db.ProfileFields.FirstOrDefaultAsync(f => f.Id == id, ct);
                if (field is null)
                    return Results.NotFound(new { error = "No such profile field." });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                db.ProfileFields.Remove(field);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("memberProfiles")
                    .Field<string?>("fieldRemoved", field.Name, null)
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .WithName("DeleteProfileField")
            .WithSummary("Remove profile field")
            .WithDescription("Remove a field and every member's answer to it.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    public static ProfileFieldView View(ProfileField field)
        => new(
            field.Id,
            field.Name,
            field.Kind == ProfileFieldKind.Text ? TextName : PickListName,
            Visibility(field.DefaultVisibility),
            [.. field.Options.OrderBy(o => o.SortOrder).Select(o => new ProfileOptionView(o.Id, o.Label))]);

    public static string Visibility(ProfileVisibility level) => level switch
    {
        ProfileVisibility.OnlyMe => "only-me",
        ProfileVisibility.Members => "members",
        _ => "staff",
    };

    private static bool TryKind(string? text, out ProfileFieldKind kind)
    {
        kind = ProfileFieldKind.PickList;

        switch (text)
        {
            case PickListName:
                return true;
            case TextName:
                kind = ProfileFieldKind.Text;
                return true;
            default:
                return false;
        }
    }

    private static bool TryVisibility(string? text, out ProfileVisibility level)
    {
        level = ProfileVisibility.Staff;

        switch (text)
        {
            case "only-me":
                level = ProfileVisibility.OnlyMe;
                return true;
            case "staff":
                return true;
            case "members":
                level = ProfileVisibility.Members;
                return true;
            default:
                return false;
        }
    }

    private static string? NameProblem(string name, IEnumerable<ProfileField> fields, Guid? except)
    {
        if (name.Length == 0)
            return "name is required.";

        if (name.Length > ProfileField.MaxNameLength)
            return $"name can be at most {ProfileField.MaxNameLength} characters.";

        return fields.Any(f => f.Id != except && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            ? "Another field has that name."
            : null;
    }

    /// <summary>
    /// A pick-list's options as saved: ids kept for the ones the field already has, new ids for the
    /// rest, in the order sent. An id the field does not have is a new option, never somebody
    /// else's.
    /// </summary>
    private static List<ProfileOption> OptionsFrom(
        IReadOnlyList<ProfileOptionRequest>? requested, IEnumerable<ProfileOption> existing, out string? problem)
    {
        problem = null;
        var known = existing.ToDictionary(o => o.Id);
        var result = new List<ProfileOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (item, index) in (requested ?? []).Select((o, i) => (o, i)))
        {
            var label = (item.Label ?? string.Empty).Trim();

            if (label.Length == 0)
                continue;

            if (label.Length > ProfileField.MaxOptionLength)
            {
                problem = $"An option can be at most {ProfileField.MaxOptionLength} characters.";
                return result;
            }

            if (!seen.Add(label))
            {
                problem = "Two options have the same name.";
                return result;
            }

            var id = item.Id is { } given && known.ContainsKey(given) ? given : Guid.CreateVersion7();
            result.Add(new ProfileOption { Id = id, Label = label, SortOrder = index });
        }

        if (result.Count == 0)
            problem = "A pick-list needs at least one option.";
        else if (result.Count > ProfileField.MaxOptions)
            problem = $"A pick-list can have at most {ProfileField.MaxOptions} options.";

        return result;
    }
}
