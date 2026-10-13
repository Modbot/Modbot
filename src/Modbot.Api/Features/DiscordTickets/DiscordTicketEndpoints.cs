using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Conventions;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordTickets;

/// <summary>One kind of ticket, as the page shows it.</summary>
/// <param name="StaffRoleName">The role's name, or null when no role is set or Modbot no longer knows it.</param>
/// <param name="OpenTickets">How many tickets of this type are open now.</param>
public sealed record DiscordTicketTypeView(
    Guid Id,
    string Name,
    string? StaffRoleId,
    string? StaffRoleName,
    string OpeningMessage,
    bool HoldsPrivateImages,
    int Position,
    int OpenTickets);

/// <param name="TranscriptChannelId">Where a closed ticket's transcript goes. Null means none is kept.</param>
public sealed record DiscordTicketsView(string? TranscriptChannelId, IReadOnlyList<DiscordTicketTypeView> Types);

/// <param name="StaffRoleId">Null or empty for none: only people who may manage threads then see the ticket.</param>
public sealed record DiscordTicketTypeRequest(string? Name, string? StaffRoleId, string? OpeningMessage, bool HoldsPrivateImages);

/// <param name="ChannelId">Null or empty for none.</param>
public sealed record DiscordTicketTranscriptChannelUpdate(string? ChannelId);

/// <param name="Ids">Every type's id, in the order the buttons should appear.</param>
public sealed record DiscordTicketOrderRequest(IReadOnlyList<Guid>? Ids);

/// <param name="ChannelId">The channel the panel goes in.</param>
/// <param name="Text">The words above the buttons.</param>
public sealed record DiscordTicketPanelRequest(string? ChannelId, string? Text);

public sealed record DiscordTicketPanelDone(bool Done);

/// <summary>
/// Settings → Discord → Tickets: the kinds of ticket members can open, where a closed ticket's
/// transcript goes, and posting the panel members press.
/// </summary>
/// <remarks>
/// Every change is a settings change in the audit log. Removing a type keeps the tickets already
/// opened under it; the page and the bot read each ticket's own copy of the type's name.
/// </remarks>
public static class DiscordTicketEndpoints
{
    public static IEndpointRouteBuilder MapDiscordTickets(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-tickets").WithTags("Settings");

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(await ViewAsync(db, ct)))
            .WithName("GetDiscordTickets")
            .WithSummary("Get ticket settings")
            .WithDescription("The kinds of ticket members can open, how many of each are open, and the channel transcripts go to.")
            .Produces<DiscordTicketsView>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("/transcript-channel", async (
                HttpContext http,
                [FromBody] DiscordTicketTranscriptChannelUpdate body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var channel = string.IsNullOrWhiteSpace(body.ChannelId) ? null : body.ChannelId.Trim();

                var change = new SettingsChange("discordTickets")
                    .Field("transcriptChannelId", settings.DiscordTicketTranscriptChannelId, channel);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.DiscordTicketTranscriptChannelId = channel;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("SetDiscordTicketTranscriptChannel")
            .WithSummary("Set the transcript channel")
            .WithDescription("Save the channel a closed ticket's transcript is posted in. Empty for none.")
            .Produces<DiscordTicketsView>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/types", async (
                HttpContext http,
                [FromBody] DiscordTicketTypeRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (Check(body) is { } invalid)
                    return Results.BadRequest(new { error = invalid });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var types = await db.DiscordTicketTypes.OrderBy(t => t.Position).ToListAsync(ct);

                if (types.Count >= DiscordTicketLimits.MostTypes)
                    return Results.BadRequest(new { error = $"At most {DiscordTicketLimits.MostTypes} ticket types." });

                var staffRole = StaffRoleOf(body);

                if (staffRole is not null && await RoleProblemAsync(db, staffRole, ct) is { } roleProblem)
                    return Results.BadRequest(new { error = roleProblem });

                var now = clock.UtcNow;
                var type = new DiscordTicketType
                {
                    Name = body.Name!.Trim(),
                    StaffRoleId = staffRole,
                    OpeningMessage = body.OpeningMessage!.Trim(),
                    HoldsPrivateImages = body.HoldsPrivateImages,
                    Position = types.Count == 0 ? 0 : types[^1].Position + 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                db.DiscordTicketTypes.Add(type);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordTickets")
                    .Field("typeAdded", null, Describe(type))
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("AddDiscordTicketType")
            .WithSummary("Add a ticket type")
            .WithDescription("Add a kind of ticket: its button label, the staff role, the opening message and whether members may post private images. At most five.")
            .Produces<DiscordTicketsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("/types/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromBody] DiscordTicketTypeRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (Check(body) is { } invalid)
                    return Results.BadRequest(new { error = invalid });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var type = await db.DiscordTicketTypes.FirstOrDefaultAsync(t => t.Id == id, ct);

                if (type is null)
                    return Results.NotFound();

                var staffRole = StaffRoleOf(body);

                // A role saved earlier that has since gone does not stop other edits; picking it anew does.
                if (staffRole is not null && staffRole != type.StaffRoleId
                    && await RoleProblemAsync(db, staffRole, ct) is { } roleProblem)
                    return Results.BadRequest(new { error = roleProblem });

                var change = new SettingsChange("discordTickets")
                    .Field($"type {type.Name} name", type.Name, body.Name!.Trim())
                    .Field($"type {type.Name} staffRoleId", type.StaffRoleId, staffRole)
                    .Field($"type {type.Name} openingMessage", type.OpeningMessage, body.OpeningMessage!.Trim())
                    .Field($"type {type.Name} holdsPrivateImages", type.HoldsPrivateImages, body.HoldsPrivateImages);

                type.Name = body.Name!.Trim();
                type.StaffRoleId = staffRole;
                type.OpeningMessage = body.OpeningMessage!.Trim();
                type.HoldsPrivateImages = body.HoldsPrivateImages;
                type.UpdatedAt = clock.UtcNow;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("UpdateDiscordTicketType")
            .WithSummary("Update a ticket type")
            .WithDescription("Change a ticket type's button label, staff role, opening message or private images switch. Tickets already open keep the private images rule they were opened with.")
            .Produces<DiscordTicketsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("/types/order", async (
                HttpContext http,
                [FromBody] DiscordTicketOrderRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var types = await db.DiscordTicketTypes.OrderBy(t => t.Position).ToListAsync(ct);
                var ids = body.Ids ?? [];

                if (ids.Count != types.Count || ids.Distinct().Count() != ids.Count || types.Any(t => !ids.Contains(t.Id)))
                    return Results.BadRequest(new { error = "Send every ticket type once." });

                var before = types.Select(t => t.Name).ToList();
                var now = clock.UtcNow;

                for (var i = 0; i < ids.Count; i++)
                {
                    var type = types.First(t => t.Id == ids[i]);

                    if (type.Position == i)
                        continue;

                    type.Position = i;
                    type.UpdatedAt = now;
                }

                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordTickets")
                    .Items("typeOrder", before, ids.Select(id => types.First(t => t.Id == id).Name))
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("ReorderDiscordTicketTypes")
            .WithSummary("Reorder the ticket types")
            .WithDescription("Set the order of the panel's buttons. Send every type's id once.")
            .Produces<DiscordTicketsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapDelete("/types/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var type = await db.DiscordTicketTypes.FirstOrDefaultAsync(t => t.Id == id, ct);

                if (type is null)
                    return Results.NotFound();

                db.DiscordTicketTypes.Remove(type);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordTickets")
                    .Field("typeRemoved", Describe(type), null)
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, ct));
            })
            .WithName("DeleteDiscordTicketType")
            .WithSummary("Delete a ticket type")
            .WithDescription("Remove a ticket type. Tickets already open stay open; only people who may manage threads can close them afterwards, and a panel already posted keeps its button, which then says the ticket type is gone.")
            .Produces<DiscordTicketsView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/panel", async (
                HttpContext http,
                [FromBody] DiscordTicketPanelRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IDiscordTicketPanel panel,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var channel = body.ChannelId?.Trim();
                var text = body.Text?.Trim();

                if (string.IsNullOrEmpty(channel))
                    return Results.BadRequest(new { error = "Pick a channel." });

                if (string.IsNullOrEmpty(text))
                    return Results.BadRequest(new { error = "Write the panel text." });

                if (text.Length > DiscordTicketLimits.MostPanelTextLength)
                    return Results.BadRequest(new { error = $"The panel text is at most {DiscordTicketLimits.MostPanelTextLength} characters." });

                var buttons = await db.DiscordTicketTypes.AsNoTracking()
                    .OrderBy(t => t.Position)
                    .Select(t => new DiscordTicketPanelButton(t.Id, t.Name))
                    .ToListAsync(ct);

                if (buttons.Count == 0)
                    return Results.BadRequest(new { error = "Add a ticket type first." });

                var outcome = await panel.PostAsync(channel, text, buttons, ct);

                if (outcome.BotOffline)
                    return Problems.Of(StatusCodes.Status503ServiceUnavailable, outcome.Error, Problems.Unavailable);

                if (!outcome.Done)
                    return Problems.Of(StatusCodes.Status502BadGateway, outcome.Error);

                await new SettingsChange("discordTickets")
                    .Field("panelPostedIn", null, channel)
                    .RecordAsync(http, ct);

                return Results.Ok(new DiscordTicketPanelDone(true));
            })
            .WithName("PostDiscordTicketPanel")
            .WithSummary("Post the ticket panel")
            .WithDescription("Post a message in a channel with one button for each ticket type, in order. Members press a button to open a private thread with staff. Needs the bot to be connected and to write in that channel.")
            .Produces<DiscordTicketPanelDone>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static string? StaffRoleOf(DiscordTicketTypeRequest body)
        => string.IsNullOrWhiteSpace(body.StaffRoleId) ? null : body.StaffRoleId.Trim();

    private static string? Check(DiscordTicketTypeRequest body)
    {
        var name = body.Name?.Trim();
        var opening = body.OpeningMessage?.Trim();

        if (string.IsNullOrEmpty(name))
            return "Give the ticket type a name.";

        if (name.Length > DiscordTicketLimits.MostNameLength)
            return $"The name is at most {DiscordTicketLimits.MostNameLength} characters.";

        if (string.IsNullOrEmpty(opening))
            return "Write the opening message.";

        if (opening.Length > DiscordTicketLimits.MostOpeningLength)
            return $"The opening message is at most {DiscordTicketLimits.MostOpeningLength} characters.";

        return null;
    }

    /// <summary>Why the staff role cannot be used, or null when Modbot knows it and Discord still has it.</summary>
    private static async Task<string?> RoleProblemAsync(ModbotContext db, string roleId, CancellationToken ct)
    {
        var guildId = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.DiscordGuildId).FirstOrDefaultAsync(ct);

        var role = await db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId && r.RoleId == roleId)
            .Select(r => new { r.RemovedAt, r.Everyone })
            .FirstOrDefaultAsync(ct);

        if (role is null || role.RemovedAt is not null)
            return "Pick a role that is in the server.";

        if (role.Everyone)
            return "Pick a role other than @everyone.";

        return null;
    }

    private static string Describe(DiscordTicketType type)
        => $"{type.Name}{(type.HoldsPrivateImages ? ", private images" : string.Empty)}";

    private static async Task<DiscordTicketsView> ViewAsync(ModbotContext db, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);

        var types = await db.DiscordTicketTypes.AsNoTracking().OrderBy(t => t.Position).ToListAsync(ct);

        var open = await db.DiscordTickets.AsNoTracking()
            .Where(t => t.State == DiscordTicketStates.Open)
            .GroupBy(t => t.TypeId)
            .Select(g => new { TypeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TypeId, g => g.Count, ct);

        var roleIds = types.Where(t => t.StaffRoleId is not null).Select(t => t.StaffRoleId!).Distinct().ToList();

        var roleNames = roleIds.Count == 0
            ? []
            : await db.DiscordRoles.AsNoTracking()
                .Where(r => r.GuildId == settings.DiscordGuildId && r.RemovedAt == null && roleIds.Contains(r.RoleId))
                .ToDictionaryAsync(r => r.RoleId, r => r.Name, ct);

        return new DiscordTicketsView(
            settings.DiscordTicketTranscriptChannelId,
            [.. types.Select((t, i) => new DiscordTicketTypeView(
                t.Id,
                t.Name,
                t.StaffRoleId,
                t.StaffRoleId is not null && roleNames.TryGetValue(t.StaffRoleId, out var name) ? name : null,
                t.OpeningMessage,
                t.HoldsPrivateImages,
                i,
                open.GetValueOrDefault(t.Id)))]);
    }
}
