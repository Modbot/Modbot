using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.AI;
using Modbot.AI.Insights;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Insights;

/// <summary>
/// AI insights: reading them, and Settings → AI → Insights (AI insights design).
/// </summary>
/// <remarks>
/// <para>
/// Reading needs <c>ViewAnalytics</c>, because an insight is the analytics pages' own figures in
/// sentences. Changing when they are written, and Generate now, need <c>ManageSettings</c> like the
/// rest of the AI tab -- Generate now spends the deployment's AI budget.
/// </para>
/// <para>
/// Failed attempts are left out of the reading list: somebody opening My Group wants summaries, and
/// the error belongs on the settings card of whoever can fix it.
/// </para>
/// </remarks>
public static class InsightEndpoints
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;
    public const int MaxChannelIdLength = 32;

    public static IEndpointRouteBuilder MapInsights(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGroup("/api/insights").WithTags("Insights")
            .MapGet("", async (
                [FromServices] ModbotContext db,
                [FromQuery] string? kind,
                [FromQuery] DateTimeOffset? before,
                [FromQuery] int? limit,
                CancellationToken ct) =>
            {
                if (kind is not null && !InsightKinds.IsKnown(kind))
                    return Results.BadRequest(new { error = "Not a kind of insight." });

                var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

                var query = db.Insights.AsNoTracking().Where(i => i.Text != null);
                if (kind is not null)
                    query = query.Where(i => i.Kind == kind);
                if (before is not null)
                    query = query.Where(i => i.CreatedAt < before);

                var rows = await query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Take(take).ToListAsync(ct);

                return Results.Ok(new InsightPage([.. rows.Select(InsightView.From)]));
            })
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("ListInsights")
            .WithSummary("List insights")
            .WithDescription(
                "AI-written summaries of the group's own figures, newest first. "
                + "Each carries the figures the model was given, so every sentence can be checked. "
                + "Figures are counts and world names only. Failed attempts are not listed.")
            .Produces<InsightPage>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        var settings = app.MapGroup("/api/settings/ai/insights").WithTags("AI settings");

        settings.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                CancellationToken ct) => Results.Ok(await ViewAsync(db, ai, ct)))
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("GetAiInsightsSettings")
            .WithSummary("Get AI insight settings")
            .WithDescription("When each kind of insight is written, and where it goes.")
            .Produces<AiInsightsSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        settings.MapPut("", async (
                [FromBody] AiInsightsSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IAiClients ai,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var timeZone = string.IsNullOrWhiteSpace(body.TimeZone) ? null : body.TimeZone.Trim();
                if (timeZone is not null && InsightTimes.FindTimeZone(timeZone) is null)
                    return Results.BadRequest(new { error = "Choose a time zone from the list." });

                var model = string.IsNullOrWhiteSpace(body.Model) ? null : body.Model.Trim();
                if (model is { Length: > AiSettingsRules.MaxModelLength })
                    return Results.BadRequest(new { error = "The model name is too long." });

                foreach (var k in body.Kinds ?? [])
                {
                    if (Problem(k) is { } problem)
                        return Results.BadRequest(new { error = problem });
                }

                var stored = await SettingsRowAsync(db, ct);
                stored.TimeZone = timeZone;
                stored.Model = model;

                var schedules = await SchedulesAsync(db, ct);

                foreach (var k in body.Kinds ?? [])
                {
                    var schedule = schedules.Single(s => s.Kind == k.Kind);
                    schedule.Enabled = k.Enabled;
                    schedule.Every = k.Every;
                    schedule.Hour = k.Hour;
                    schedule.Weekday = k.Weekday;
                    schedule.DiscordChannelId = string.IsNullOrWhiteSpace(k.DiscordChannelId) ? null : k.DiscordChannelId.Trim();
                }

                // Design §3: turning a kind on at 3 pm with a 9 am time must not write one at once.
                InsightScheduler.MarkPastMomentsHandled(schedules, timeZone, clock.UtcNow);

                await db.SaveChangesAsync(ct);

                return Results.Ok(await ViewAsync(db, ai, ct));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("SetAiInsightsSettings")
            .WithSummary("Update AI insight settings")
            .WithDescription("Save when each kind of insight is written, and where it goes.")
            .Produces<AiInsightsSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        settings.MapPost("/{kind}/generate", async (
                HttpContext http,
                [FromRoute] string kind,
                [FromServices] ModbotContext db,
                [FromServices] InsightWriter writer,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (!InsightKinds.IsKnown(kind))
                    return Results.NotFound(new { error = "Not a kind of insight." });

                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Forbid();

                var username = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Username).FirstOrDefaultAsync(ct) ?? "";
                var schedule = (await SchedulesAsync(db, ct)).Single(s => s.Kind == kind);

                var attempt = await writer.WriteAsync(
                    kind, schedule.Every, InsightPeriod.DayOf(clock.UtcNow), InsightStart.Button(userId, username), ct);

                return attempt.Insight is null
                    ? Results.Conflict(new { error = attempt.NotAsked })
                    : Results.Ok(InsightView.From(attempt.Insight));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("GenerateInsight")
            .WithSummary("Write an insight")
            .WithDescription(
                "Write one insight of this kind now, for the stretch ending yesterday. "
                + "Waits for the model. Answers 200 with the stored insight whether the model wrote "
                + "something or the call failed -- `text` or `error` says which. Never posted to Discord. "
                + "409 when AI is off or the spend limit for insights is reached.")
            .Produces<InsightView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static string? Problem(InsightKindUpdate k)
    {
        if (!InsightKinds.IsKnown(k.Kind))
            return "Not a kind of insight.";
        if (k.Every is not (InsightKinds.EveryDay or InsightKinds.EveryWeek))
            return "Choose every day or every week.";
        if (k.Hour is < 0 or > 23)
            return "Choose a time.";
        if (k.Weekday is < 0 or > 6)
            return "Choose a day of the week.";

        // A Discord channel id is a snowflake: digits only. This is Discord, not VRChat, whose ids
        // Modbot never checks (foundation §3.1.1).
        var channel = k.DiscordChannelId?.Trim();
        if (!string.IsNullOrEmpty(channel) && (channel.Length > MaxChannelIdLength || !channel.All(char.IsAsciiDigit)))
            return "Choose a Discord channel.";

        return null;
    }

    private static async Task<InsightSettings> SettingsRowAsync(ModbotContext db, CancellationToken ct)
    {
        var row = await db.InsightSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (row is null)
        {
            row = new InsightSettings { Id = 1 };
            db.InsightSettings.Add(row);
        }

        return row;
    }

    /// <summary>One row per kind, created on first read with every kind off.</summary>
    private static async Task<List<InsightSchedule>> SchedulesAsync(ModbotContext db, CancellationToken ct)
    {
        var rows = await db.InsightSchedules.ToListAsync(ct);

        foreach (var kind in InsightKinds.All.Where(k => rows.All(r => r.Kind != k)))
        {
            var row = new InsightSchedule { Kind = kind };
            db.InsightSchedules.Add(row);
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<AiInsightsSettingsResponse> ViewAsync(ModbotContext db, IAiClients ai, CancellationToken ct)
    {
        var settings = await db.InsightSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var schedules = await db.InsightSchedules.AsNoTracking().ToListAsync(ct);
        var baseModel = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.AiModel).FirstOrDefaultAsync(ct);
        var aiOn = await ai.GetChatAsync(ct) is not null;

        var kinds = new List<InsightKindSettings>();

        foreach (var kind in InsightKinds.All)
        {
            var schedule = schedules.FirstOrDefault(s => s.Kind == kind) ?? new InsightSchedule { Kind = kind };
            var last = await db.Insights.AsNoTracking()
                .Where(i => i.Kind == kind)
                .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
                .FirstOrDefaultAsync(ct);

            kinds.Add(new InsightKindSettings(
                kind,
                InsightKinds.Label(kind),
                schedule.Enabled,
                schedule.Every,
                schedule.Hour,
                schedule.Weekday,
                schedule.DiscordChannelId,
                last is null ? null : InsightView.From(last)));
        }

        return new AiInsightsSettingsResponse(settings?.TimeZone, settings?.Model, baseModel, aiOn, kinds);
    }
}
