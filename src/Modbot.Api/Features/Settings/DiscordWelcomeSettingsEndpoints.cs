using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>One rule: a member who holds <paramref name="RoleId"/> is sent this step.</summary>
/// <param name="RoleId">The Discord role.</param>
/// <param name="Text">What the step says.</param>
/// <param name="Link">An https address the step opens, or null.</param>
public sealed record DiscordWelcomeRule(string RoleId, string Text, string? Link);

/// <summary>One default step, sent when fewer than three rules matched.</summary>
/// <param name="Text">What the step says.</param>
/// <param name="Link">An https address the step opens, or null.</param>
public sealed record DiscordWelcomeDefault(string Text, string? Link);

/// <summary>The smart welcome's settings, as stored.</summary>
/// <param name="Via"><c>dm</c>, <c>channel</c> or <c>both</c>.</param>
/// <param name="LinkButton">Add a button to the account link page for a member who has not linked.</param>
/// <param name="Rules">In the order they are tried.</param>
/// <param name="Defaults">In the order they fill.</param>
public sealed record DiscordWelcomeSettingsResponse(
    bool Enabled,
    string Via,
    string? ChannelId,
    bool LinkButton,
    IReadOnlyList<DiscordWelcomeRule> Rules,
    IReadOnlyList<DiscordWelcomeDefault> Defaults,
    int MostRules,
    int MostDefaults,
    int MostTextLength);

/// <param name="Via"><c>dm</c> (a direct message), <c>channel</c> or <c>both</c>.</param>
/// <param name="ChannelId">Needed when <c>via</c> is <c>channel</c> or <c>both</c>.</param>
/// <param name="LinkButton">Add a button to the account link page for a member who has not linked.</param>
/// <param name="Rules">At most 25, in the order they are tried.</param>
/// <param name="Defaults">At most 3, in the order they fill.</param>
public sealed record DiscordWelcomeSettingsUpdate(
    bool Enabled,
    string Via,
    string? ChannelId,
    bool LinkButton,
    IReadOnlyList<DiscordWelcomeRule>? Rules,
    IReadOnlyList<DiscordWelcomeDefault>? Defaults);

/// <summary>
/// Settings → Discord → Welcome steps (smart welcome design §7).
/// </summary>
/// <remarks>
/// A save replaces every step in one transaction. Switching the welcome on starts the clock that says
/// which members count: only those who join after it (design §3.1).
/// </remarks>
public static class DiscordWelcomeSettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordWelcomeSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-welcome").WithTags("Settings");

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(await ViewAsync(db, await db.GetSettingsAsync(ct), ct)))
            .WithName("GetDiscordWelcomeSettings")
            .WithSummary("Get welcome steps")
            .WithDescription("The steps a new Discord member is sent: whether it is on, where it goes, the rules that pick a step from a role, and the default steps.")
            .Produces<DiscordWelcomeSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordWelcomeSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var via = body.Via?.Trim().ToLowerInvariant();
                if (!DiscordWelcomeWhere.IsKnown(via))
                    return Results.BadRequest(new { error = "`via` is dm, channel or both." });

                var channel = Blank(body.ChannelId);
                if (DiscordWelcomeWhere.UsesChannel(via) && channel is null)
                    return Results.BadRequest(new { error = "Pick the welcome channel." });

                var rules = (body.Rules ?? []).Select(r => r with { Text = (r.Text ?? string.Empty).Trim(), Link = Blank(r.Link), RoleId = (r.RoleId ?? string.Empty).Trim() }).ToList();
                var defaults = (body.Defaults ?? []).Select(d => d with { Text = (d.Text ?? string.Empty).Trim(), Link = Blank(d.Link) }).ToList();

                if (rules.Count > DiscordWelcomeLimits.MostRules)
                    return Results.BadRequest(new { error = $"At most {DiscordWelcomeLimits.MostRules} rules." });

                if (defaults.Count > DiscordWelcomeLimits.MostDefaults)
                    return Results.BadRequest(new { error = $"At most {DiscordWelcomeLimits.MostDefaults} default steps." });

                if (rules.Any(r => r.RoleId.Length == 0))
                    return Results.BadRequest(new { error = "Pick a role for every rule." });

                if (StepProblem(rules.Select(r => (r.Text, r.Link)).Concat(defaults.Select(d => (d.Text, d.Link)))) is { } problem)
                    return Results.BadRequest(new { error = problem });

                if (body.Enabled && rules.Count + defaults.Count == 0)
                    return Results.BadRequest(new { error = "Add at least one step." });

                var settings = await db.GetSettingsAsync(ct);
                var was = await ViewAsync(db, settings, ct);

                var change = new SettingsChange("discordWelcome")
                    .Field("enabled", settings.DiscordWelcomeEnabled, body.Enabled)
                    .Field("via", settings.DiscordWelcomeVia, via!)
                    .Field("channelId", settings.DiscordWelcomeChannelId, channel)
                    .Field("linkButton", settings.DiscordWelcomeLinkButton, body.LinkButton)
                    .Items("rules", was.Rules.Select(Describe), rules.Select(Describe))
                    .Items("defaults", was.Defaults.Select(Describe), defaults.Select(Describe));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // Switching on starts the clock; switching off stops it.
                if (body.Enabled && !settings.DiscordWelcomeEnabled)
                    settings.DiscordWelcomeStartedAt = clock.UtcNow;
                else if (!body.Enabled)
                    settings.DiscordWelcomeStartedAt = null;

                settings.DiscordWelcomeEnabled = body.Enabled;
                settings.DiscordWelcomeVia = via!;
                settings.DiscordWelcomeChannelId = channel;
                settings.DiscordWelcomeLinkButton = body.LinkButton;

                await db.DiscordWelcomeSteps.ExecuteDeleteAsync(ct);

                for (var i = 0; i < rules.Count; i++)
                {
                    db.DiscordWelcomeSteps.Add(new DiscordWelcomeStep
                    {
                        Kind = DiscordWelcomeStepKinds.Rule,
                        RoleId = rules[i].RoleId,
                        Text = rules[i].Text,
                        Link = rules[i].Link,
                        Position = i,
                    });
                }

                for (var i = 0; i < defaults.Count; i++)
                {
                    db.DiscordWelcomeSteps.Add(new DiscordWelcomeStep
                    {
                        Kind = DiscordWelcomeStepKinds.Default,
                        Text = defaults[i].Text,
                        Link = defaults[i].Link,
                        Position = i,
                    });
                }

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, settings, ct));
            })
            .WithName("SetDiscordWelcomeSettings")
            .WithSummary("Update welcome steps")
            .WithDescription(
                "Save the welcome steps. `via` is dm, channel or both; the channel is needed for the "
                + "last two. Every step is replaced by the lists sent: at most 25 rules (a role, a text "
                + "of at most 80 characters on one line, and an optional https link) and at most 3 "
                + "default steps. A member who holds a rule's role is sent its step; defaults fill "
                + "up to three. `linkButton` adds a button to the account link page for a member who has not linked. Switching on needs at least one step, and welcomes only members who "
                + "join after that.")
            .Produces<DiscordWelcomeSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    /// <summary>What is wrong with the steps' texts and links, or null.</summary>
    public static string? StepProblem(IEnumerable<(string Text, string? Link)> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        foreach (var (text, link) in steps)
        {
            if (text.Length == 0)
                return "Every step needs text.";

            if (text.Length > DiscordWelcomeLimits.MostTextLength)
                return $"A step's text is at most {DiscordWelcomeLimits.MostTextLength} characters.";

            if (text.Contains('\n') || text.Contains('\r'))
                return "A step's text is one line.";

            if (link is null)
                continue;

            if (link.Length > DiscordWelcomeLimits.MostLinkLength)
                return $"A link is at most {DiscordWelcomeLimits.MostLinkLength} characters.";

            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return "A link starts with https://.";
        }

        return null;
    }

    private static async Task<DiscordWelcomeSettingsResponse> ViewAsync(
        ModbotContext db, Core.Data.Entities.Settings settings, CancellationToken ct)
    {
        var steps = await db.DiscordWelcomeSteps.AsNoTracking()
            .OrderBy(s => s.Position)
            .ToListAsync(ct);

        return new DiscordWelcomeSettingsResponse(
            settings.DiscordWelcomeEnabled,
            settings.DiscordWelcomeVia,
            settings.DiscordWelcomeChannelId,
            settings.DiscordWelcomeLinkButton,
            [.. steps.Where(s => s.Kind == DiscordWelcomeStepKinds.Rule)
                .Select(s => new DiscordWelcomeRule(s.RoleId ?? string.Empty, s.Text, s.Link))],
            [.. steps.Where(s => s.Kind == DiscordWelcomeStepKinds.Default)
                .Select(s => new DiscordWelcomeDefault(s.Text, s.Link))],
            DiscordWelcomeLimits.MostRules,
            DiscordWelcomeLimits.MostDefaults,
            DiscordWelcomeLimits.MostTextLength);
    }

    private static string Describe(DiscordWelcomeRule rule) => $"{rule.RoleId}: {rule.Text} {rule.Link}".TrimEnd();

    private static string Describe(DiscordWelcomeDefault step) => $"{step.Text} {step.Link}".TrimEnd();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
