using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.AI.Chat;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Settings;

/// <summary>One tool on the settings page.</summary>
/// <param name="Needs">The permissions a person must hold to be offered it, in the roles page's words.</param>
/// <param name="OnlyReads">False for a tool that changes something; such a tool is off until switched on.</param>
public sealed record AiChatToolView(string Name, string Label, IReadOnlyList<string> Needs, bool OnlyReads, bool Enabled);

/// <param name="Model">Null means the Base model, <paramref name="BaseModel"/>.</param>
/// <param name="AiEnabled">Whether AI as a whole is on, on Base.</param>
public sealed record AiChatSettingsResponse(
    bool Enabled,
    string? Model,
    string? BaseModel,
    string? Instructions,
    int MaxToolCalls,
    int MaxReplyTokens,
    int TimeLimitSeconds,
    bool AiEnabled,
    IReadOnlyList<AiChatToolView> Tools);

/// <param name="Tools">Tool name to on or off. Tools left out keep their switch.</param>
public sealed record AiChatSettingsUpdate(
    bool Enabled,
    string? Model,
    string? Instructions,
    int MaxToolCalls,
    int MaxReplyTokens,
    int TimeLimitSeconds,
    IReadOnlyDictionary<string, bool>? Tools);

/// <summary>Settings → AI → Chat (AI chat design §5).</summary>
public static class AiChatSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAiChatSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/ai/chat").WithTags("AI settings");

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] ChatToolRegistry registry,
                CancellationToken ct) =>
                Results.Ok(View(await db.GetSettingsAsync(ct), registry)))
            .WithName("GetAiChatSettings")
            .WithSummary("Chat's on/off switch, model, extra instructions, limits and tools")
            .Produces<AiChatSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                [FromBody] AiChatSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] ChatToolRegistry registry,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var model = string.IsNullOrWhiteSpace(body.Model) ? null : body.Model.Trim();
                var instructions = string.IsNullOrWhiteSpace(body.Instructions) ? null : body.Instructions.Trim();

                if (ChatSettingsRules.Problem(body.MaxToolCalls, body.MaxReplyTokens, body.TimeLimitSeconds, model, instructions) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var settings = await db.GetSettingsAsync(ct);
                var switches = new Dictionary<string, bool>(ChatToolRegistry.ParseSwitches(settings.AiChatToolSwitches), StringComparer.Ordinal);

                foreach (var (name, on) in body.Tools ?? new Dictionary<string, bool>())
                {
                    var tool = registry.All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
                    if (tool is null)
                        return Results.BadRequest(new { error = $"'{name}' is not a tool." });

                    // Stored only where it differs from the tool's starting state, so a later
                    // version can change a tool's kind without every deployment's old switch
                    // pinning it.
                    if (on == tool.OnlyReads)
                        switches.Remove(name);
                    else
                        switches[name] = on;
                }

                settings.AiChatEnabled = body.Enabled;
                settings.AiChatModel = model;
                settings.AiChatInstructions = instructions;
                settings.AiChatMaxToolCalls = body.MaxToolCalls;
                settings.AiChatMaxReplyTokens = body.MaxReplyTokens;
                settings.AiChatTimeLimitSeconds = body.TimeLimitSeconds;
                settings.AiChatToolSwitches = JsonSerializer.Serialize(switches);

                await db.SaveChangesAsync(ct);

                return Results.Ok(View(settings, registry));
            })
            .WithName("SetAiChatSettings")
            .WithSummary("Save Chat's settings")
            .Produces<AiChatSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static IReadOnlyList<string> NeedsOf(ModbotPermissions needs) =>
        [.. PermissionCatalog.All.Where(p => p.Value != 0 && needs.HasFlag((ModbotPermissions)p.Value)).Select(p => p.Label)];

    private static AiChatSettingsResponse View(Core.Data.Entities.Settings settings, ChatToolRegistry registry)
    {
        var switches = ChatToolRegistry.ParseSwitches(settings.AiChatToolSwitches);

        return new AiChatSettingsResponse(
            settings.AiChatEnabled,
            settings.AiChatModel,
            settings.AiModel,
            settings.AiChatInstructions,
            settings.AiChatMaxToolCalls,
            settings.AiChatMaxReplyTokens,
            settings.AiChatTimeLimitSeconds,
            settings.AiEnabled,
            [.. registry.All.Select(t => new AiChatToolView(
                t.Name, t.Label, NeedsOf(t.Needs), t.OnlyReads, ChatToolRegistry.IsOn(t, switches)))]);
    }
}
