using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordRoleMenus;

/// <summary>One role on a role menu, as the page reads and writes it.</summary>
/// <param name="RoleId">The Discord role handed out.</param>
/// <param name="Label">What the button or choice says.</param>
/// <param name="Emoji">A Unicode emoji or a server emoji beside the label, or null.</param>
public sealed record DiscordRoleMenuRoleBody(string RoleId, string Label, string? Emoji);

/// <summary>The body of a create or a change.</summary>
/// <param name="Style"><c>buttons</c> or <c>select</c>.</param>
/// <param name="SingleChoice">Choosing a role takes away the others on the menu.</param>
public sealed record DiscordRoleMenuBody(
    string Name,
    string ChannelId,
    string Text,
    string Style,
    bool SingleChoice,
    IReadOnlyList<DiscordRoleMenuRoleBody>? Roles);

/// <summary>A role menu as stored.</summary>
/// <param name="MessageId">The posted message, or null while the menu has not been posted.</param>
/// <param name="PostedAt">When the message was last posted or rewritten.</param>
/// <param name="ChangedSincePosted">The menu was saved after its message was last written, so Discord shows the older one.</param>
public sealed record DiscordRoleMenuView(
    Guid Id,
    string Name,
    string ChannelId,
    string Text,
    string Style,
    bool SingleChoice,
    IReadOnlyList<DiscordRoleMenuRoleBody> Roles,
    string? MessageId,
    DateTimeOffset? PostedAt,
    bool ChangedSincePosted);

public sealed record DiscordRoleMenusResponse(
    IReadOnlyList<DiscordRoleMenuView> Menus,
    int MostMenus,
    int MostRoles,
    int MostNameLength,
    int MostTextLength,
    int MostLabelLength);

/// <summary>
/// Role menus: messages Modbot posts with buttons or a drop-down list, where a member picks a role
/// to get it or drop it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Saving and posting are two steps.</strong> A save only changes Modbot's copy, so a
/// half-built menu is never in front of members; Post writes the message, or rewrites the one
/// already there.
/// </para>
/// <para>
/// <strong>No dangerous roles.</strong> A role holding any permission the permission watch tells staff
/// about (<see cref="DiscordDangerousPermissions"/>) is refused at save and again at post, since a
/// role can gain one after the menu was made. A role the bot cannot hand out is not refused: the
/// page marks it, and a press on it says so.
/// </para>
/// </remarks>
public static class DiscordRoleMenuEndpoints
{
    public static IEndpointRouteBuilder MapDiscordRoleMenus(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-role-menus").WithTags("Settings");

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(await ListAsync(db, ct)))
            .WithName("ListDiscordRoleMenus")
            .WithSummary("List role menus")
            .WithDescription("The role menus Modbot posts in Discord, each with the roles it gives and whether its message is up.")
            .Produces<DiscordRoleMenusResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("", async (
                HttpContext http,
                [FromBody] DiscordRoleMenuBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (await db.DiscordRoleMenus.CountAsync(ct) >= DiscordRoleMenuLimits.MostMenus)
                    return Results.BadRequest(new { error = $"At most {DiscordRoleMenuLimits.MostMenus} role menus." });

                var clean = Clean(body);
                if (await ProblemAsync(db, clean, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var now = clock.UtcNow;
                var menu = new DiscordRoleMenu { CreatedAt = now, UpdatedAt = now };
                Apply(menu, clean);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                db.DiscordRoleMenus.Add(menu);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordRoleMenus")
                    .Items<string>("created", [], [Describe(menu)])
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);
                return Results.Ok(View(menu));
            })
            .WithName("CreateDiscordRoleMenu")
            .WithSummary("Add a role menu")
            .WithDescription(
                "Save a new role menu. It is not posted until Post is called. `style` is buttons or select. "
                + "Between 1 and 25 roles, each with a label of at most 80 characters on one line and an optional emoji; "
                + "a role with a dangerous permission is refused.")
            .Produces<DiscordRoleMenuView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromBody] DiscordRoleMenuBody body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var menu = await db.DiscordRoleMenus.Include(m => m.Roles).FirstOrDefaultAsync(m => m.Id == id, ct);
                if (menu is null)
                    return Results.NotFound(new { error = "No such role menu." });

                var clean = Clean(body);

                if (menu.MessageId is not null && clean.ChannelId != menu.ChannelId)
                    return Results.BadRequest(new { error = "A posted menu stays in its channel. Delete it and make a new one to move it." });

                if (await ProblemAsync(db, clean, ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var was = Describe(menu);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.DiscordRoleMenuRoles.RemoveRange(menu.Roles);
                menu.Roles = [];
                Apply(menu, clean);
                menu.UpdatedAt = clock.UtcNow;

                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordRoleMenus")
                    .Items<string>("changed", [was], [Describe(menu)])
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);
                return Results.Ok(View(menu));
            })
            .WithName("SetDiscordRoleMenu")
            .WithSummary("Change a role menu")
            .WithDescription(
                "Save changes to a role menu. The posted message is not rewritten until Post is called. "
                + "A posted menu cannot move to another channel.")
            .Produces<DiscordRoleMenuView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/{id:guid}/post", async (
                HttpContext http,
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordRoleMenuActions discord,
                CancellationToken ct) =>
            {
                var menu = await db.DiscordRoleMenus.Include(m => m.Roles).FirstOrDefaultAsync(m => m.Id == id, ct);
                if (menu is null)
                    return Results.NotFound(new { error = "No such role menu." });

                // A role can gain a dangerous permission after the menu was saved.
                if (await ProblemAsync(db, Of(menu), ct) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var outcome = await discord.PostAsync(menu, ct);

                // Somebody deleted the message: this is a new post, not an edit.
                if (outcome.Gone)
                {
                    menu.MessageId = null;
                    outcome = await discord.PostAsync(menu, ct);
                }

                if (!outcome.Done)
                {
                    return Results.Json(
                        new { error = outcome.Error ?? "Discord refused." },
                        statusCode: outcome.BotOffline ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status502BadGateway);
                }

                var wasPosted = menu.MessageId is not null;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                menu.MessageId = outcome.MessageId ?? menu.MessageId;
                menu.PostedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordRoleMenus")
                    .Items<string>(wasPosted ? "rewritten" : "posted", [], [Describe(menu)])
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);
                return Results.Ok(View(menu));
            })
            .WithName("PostDiscordRoleMenu")
            .WithSummary("Post a role menu")
            .WithDescription(
                "Post the role menu in its channel, or rewrite its message when it is already up. "
                + "Needs the Discord bot to be connected and to send messages in the channel.")
            .Produces<DiscordRoleMenuView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IDiscordRoleMenuActions discord,
                CancellationToken ct) =>
            {
                var menu = await db.DiscordRoleMenus.Include(m => m.Roles).FirstOrDefaultAsync(m => m.Id == id, ct);
                if (menu is null)
                    return Results.NotFound(new { error = "No such role menu." });

                // The message goes first, so a menu whose message is still up is never forgotten.
                if (menu.MessageId is { } messageId)
                {
                    var outcome = await discord.DeleteAsync(menu.ChannelId, messageId, ct);

                    if (!outcome.Done)
                    {
                        return Results.Json(
                            new { error = outcome.Error ?? "Discord refused." },
                            statusCode: outcome.BotOffline ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status502BadGateway);
                    }
                }

                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var described = Describe(menu);
                db.DiscordRoleMenus.Remove(menu);
                await db.SaveChangesAsync(ct);

                await new SettingsChange("discordRoleMenus")
                    .Items<string>("deleted", [described], [])
                    .RecordAsync(http, ct);

                await transaction.CommitAsync(ct);
                return Results.NoContent();
            })
            .WithName("DeleteDiscordRoleMenu")
            .WithSummary("Delete a role menu")
            .WithDescription("Delete the role menu and, when it is posted, its message in Discord. Roles members already picked stay with them.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static async Task<DiscordRoleMenusResponse> ListAsync(ModbotContext db, CancellationToken ct)
    {
        var menus = await db.DiscordRoleMenus.AsNoTracking()
            .Include(m => m.Roles)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        return new DiscordRoleMenusResponse(
            [.. menus.Select(View)],
            DiscordRoleMenuLimits.MostMenus,
            DiscordRoleMenuLimits.MostRoles,
            DiscordRoleMenuLimits.MostNameLength,
            DiscordRoleMenuLimits.MostTextLength,
            DiscordRoleMenuLimits.MostLabelLength);
    }

    private static DiscordRoleMenuView View(DiscordRoleMenu menu) => new(
        menu.Id,
        menu.Name,
        menu.ChannelId,
        menu.Text,
        menu.Style,
        menu.SingleChoice,
        [.. menu.Roles.OrderBy(r => r.Position).Select(r => new DiscordRoleMenuRoleBody(r.RoleId, r.Label, r.Emoji))],
        menu.MessageId,
        menu.PostedAt,
        menu.MessageId is not null && (menu.PostedAt is null || menu.UpdatedAt > menu.PostedAt));

    private static DiscordRoleMenuBody Of(DiscordRoleMenu menu) => new(
        menu.Name,
        menu.ChannelId,
        menu.Text,
        menu.Style,
        menu.SingleChoice,
        [.. menu.Roles.OrderBy(r => r.Position).Select(r => new DiscordRoleMenuRoleBody(r.RoleId, r.Label, r.Emoji))]);

    private static DiscordRoleMenuBody Clean(DiscordRoleMenuBody body) => new(
        (body.Name ?? string.Empty).Trim(),
        (body.ChannelId ?? string.Empty).Trim(),
        (body.Text ?? string.Empty).Trim(),
        (body.Style ?? string.Empty).Trim().ToLowerInvariant(),
        body.SingleChoice,
        [.. (body.Roles ?? []).Select(r => new DiscordRoleMenuRoleBody(
            (r.RoleId ?? string.Empty).Trim(),
            (r.Label ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(r.Emoji) ? null : r.Emoji.Trim()))]);

    private static void Apply(DiscordRoleMenu menu, DiscordRoleMenuBody body)
    {
        menu.Name = body.Name;
        menu.ChannelId = body.ChannelId;
        menu.Text = body.Text;
        menu.Style = body.Style;
        menu.SingleChoice = body.SingleChoice;
        menu.Roles = [.. (body.Roles ?? []).Select((r, i) => new DiscordRoleMenuRole
        {
            MenuId = menu.Id,
            RoleId = r.RoleId,
            Label = r.Label,
            Emoji = r.Emoji,
            Position = i,
        })];
    }

    /// <summary>What is wrong with a menu, in words for the page, or null.</summary>
    private static async Task<string?> ProblemAsync(ModbotContext db, DiscordRoleMenuBody menu, CancellationToken ct)
    {
        if (menu.Name.Length == 0)
            return "Give the menu a name.";

        if (menu.Name.Length > DiscordRoleMenuLimits.MostNameLength || !OneLine(menu.Name))
            return $"The name is one line of at most {DiscordRoleMenuLimits.MostNameLength} characters.";

        if (menu.ChannelId.Length == 0)
            return "Pick the channel.";

        if (menu.Text.Length == 0)
            return "Write the message.";

        if (menu.Text.Length > DiscordRoleMenuLimits.MostTextLength)
            return $"The message is at most {DiscordRoleMenuLimits.MostTextLength} characters.";

        if (!DiscordRoleMenuStyles.IsKnown(menu.Style))
            return "`style` is buttons or select.";

        var roles = menu.Roles ?? [];

        if (roles.Count == 0)
            return "Add at least one role.";

        if (roles.Count > DiscordRoleMenuLimits.MostRoles)
            return $"At most {DiscordRoleMenuLimits.MostRoles} roles.";

        if (roles.Any(r => r.RoleId.Length == 0))
            return "Pick a role for every line.";

        if (roles.Select(r => r.RoleId).Distinct(StringComparer.Ordinal).Count() != roles.Count)
            return "A role can be on the menu once.";

        foreach (var role in roles)
        {
            if (role.Label.Length == 0)
                return "Give every role a label.";

            if (role.Label.Length > DiscordRoleMenuLimits.MostLabelLength || !OneLine(role.Label))
                return $"A label is one line of at most {DiscordRoleMenuLimits.MostLabelLength} characters.";

            if (role.Emoji is { } emoji
                && (emoji.Length > DiscordRoleMenuLimits.MostEmojiLength || emoji.Any(char.IsWhiteSpace)))
            {
                return "An emoji is one emoji, with no spaces.";
            }
        }

        return await RoleProblemAsync(db, roles.Select(r => r.RoleId).ToList(), ct);
    }

    /// <summary>
    /// Whether every role is one a role menu may hand out: in the server, not removed, not
    /// @everyone, not owned by a bot or an integration, and holding no dangerous permission.
    /// </summary>
    private static async Task<string?> RoleProblemAsync(ModbotContext db, IReadOnlyList<string> roleIds, CancellationToken ct)
    {
        var guildId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct);

        var stored = guildId is null
            ? []
            : await db.DiscordRoles.AsNoTracking()
                .Where(r => r.GuildId == guildId && roleIds.Contains(r.RoleId))
                .ToListAsync(ct);

        foreach (var id in roleIds)
        {
            var role = stored.FirstOrDefault(r => r.RoleId == id);

            if (role is null || role.RemovedAt is not null)
                return "One of the roles is not in the Discord server.";

            if (role.Everyone || role.RoleId == role.GuildId)
                return "@everyone cannot be on a role menu.";

            if (role.Managed)
                return $"{role.Name} belongs to a bot or an integration, so nobody can be given it.";

            if (role.Permissions is not { } permissions)
                return $"Modbot has not read {role.Name}'s permissions yet. Try again in a minute.";

            if (DiscordDangerousPermissions.In(permissions) != 0)
            {
                var names = string.Join(", ", DiscordDangerousPermissions.Names(permissions));
                return $"{role.Name} holds {names}, so a role menu will not give it.";
            }
        }

        return null;
    }

    private static bool OneLine(string value) => !value.Contains('\n') && !value.Contains('\r');

    private static string Describe(DiscordRoleMenu menu)
        => $"{menu.Name} in {menu.ChannelId} ({menu.Style}{(menu.SingleChoice ? ", pick one" : string.Empty)}): "
            + string.Join(", ", menu.Roles.OrderBy(r => r.Position).Select(r => $"{r.Label} = {r.RoleId}"));
}
