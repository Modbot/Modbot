using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Api.Features.Settings;

/// <summary>One of the bot's commands and whether it is on.</summary>
/// <param name="Name">The command's name: <c>lookup</c>, or a right-click menu's own words.</param>
/// <param name="Menu">A right-click menu rather than a slash command.</param>
/// <param name="On">Whether the bot registers it.</param>
/// <param name="OnByDefault">What it does until somebody chooses.</param>
public sealed record DiscordCommandSwitchView(string Name, bool Menu, bool On, bool OnByDefault);

/// <summary>Every command with a switch, in the order the Commands card lists them.</summary>
public sealed record DiscordCommandsSettingsResponse(IReadOnlyList<DiscordCommandSwitchView> Commands);

/// <param name="Commands">
/// Each command's name with whether it is on. A command that is left out keeps what it has. A name
/// that is not one of the bot's commands is refused.
/// </param>
public sealed record DiscordCommandsSettingsUpdate(IReadOnlyDictionary<string, bool> Commands);

/// <summary>
/// Settings → Discord → Commands: one switch per command (Discord commands design §3.8).
/// </summary>
/// <remarks>
/// <para>
/// A command that is off is not registered on the server, so members never see one that would only
/// say it is off. The bot reads the setting every few seconds and registers again when the set
/// changes.
/// </para>
/// <para>
/// Only what differs from a command's default is stored (<see cref="DiscordCommandSwitches.Write"/>),
/// so a command that has never been touched keeps following its default. A demo serves everyone as an
/// administrator and never runs the bot, so nothing can be saved there (Discord /me design §5): the
/// card shows what is stored, and a save is refused.
/// </para>
/// </remarks>
public static class DiscordCommandsSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordCommandsSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-commands").WithTags("Settings");

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) => Results.Ok(View((await db.GetSettingsAsync(ct)).DiscordCommands)))
            .WithName("GetDiscordCommandsSettings")
            .WithSummary("Get the command switches")
            .WithDescription("Every Discord command with whether it is on, and what it does until chosen.")
            .Produces<DiscordCommandsSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordCommandsSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] DemoMode? demo,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (DemoAuthentication.MayServeEveryoneAsAdministrator(demo))
                    return Results.BadRequest(new { error = "Commands cannot be changed in the demo." });

                var asked = body.Commands ?? new Dictionary<string, bool>();

                if (asked.Keys.FirstOrDefault(name => DiscordCommandSwitches.Find(name) is null) is { } unknown)
                    return Results.BadRequest(new { error = $"\"{unknown}\" is not one of the bot's commands." });

                var settings = await db.GetSettingsAsync(ct);
                var before = DiscordCommandSwitches.Current(settings.DiscordCommands);

                var after = new Dictionary<string, bool>(before, StringComparer.Ordinal);
                foreach (var (name, on) in asked)
                    after[name] = on;

                var change = new SettingsChange("discordCommands");
                foreach (var command in DiscordCommandSwitches.All)
                    change.Field(command.Name, before[command.Name], after[command.Name]);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.DiscordCommands = DiscordCommandSwitches.Write(after);

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings.DiscordCommands));
            })
            .WithName("SetDiscordCommandsSettings")
            .WithSummary("Update the command switches")
            .WithDescription("Switch Discord commands on or off. A command that is off is not registered on the server. Refused in the demo.")
            .Produces<DiscordCommandsSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static DiscordCommandsSettingsResponse View(string stored)
    {
        var effective = DiscordCommandSwitches.Current(stored);

        return new DiscordCommandsSettingsResponse([.. DiscordCommandSwitches.All.Select(c => new DiscordCommandSwitchView(
            c.Name, c.Menu, effective[c.Name], c.OnByDefault))]);
    }
}
