using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Retention;

namespace Modbot.Cloud.Features.AdminInstalls;

/// <summary>One install, as the admin list shows it.</summary>
/// <param name="EventsStored">Events received within the retention window, from the daily totals.</param>
/// <param name="ClockOffsetMs">The offset Cloud trusts for its latest batch, or null before its first.</param>
/// <param name="ClockDisagrees">Its own clock measure and Cloud's are more than five minutes apart.</param>
public sealed record InstallView(
    Guid InstallId,
    string ClientVersion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    long EventsStored,
    long? ClockOffsetMs,
    bool ClockDisagrees,
    string? ModbotServerId);

public sealed record InstallPage(int Total, int Offset, int Limit, IReadOnlyList<InstallView> Items);

/// <summary>One stored event, for debugging. Every field is plain text.</summary>
public sealed record EventView(
    string ClientEventId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset SentAt,
    DateTimeOffset OccurredAt,
    string Type,
    string? TypeRaw,
    string SubjectId,
    string? DisplayName,
    string WorldId,
    string InstanceId,
    string? GroupId,
    string Data);

public sealed record DayCount(DateOnly Day, long Events);

public sealed record SettingsView(int EventKeepDays);

/// <summary>
/// Cloud admin: installs, events per day, one install's recent events, and retention.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every endpoint here needs the admin sign-in.</strong> Events name other players and where
/// they were (cloud event backup spec 10), so nothing that reads them is reachable any other way —
/// not with an install's secret, not anonymously.
/// </para>
/// <para>
/// <strong>Locations are never join links.</strong> The world and instance come back as separate plain
/// strings and are shown as text. They never carry an instance's <c>nonce</c>, which the client throws
/// away before anything is sent.
/// </para>
/// </remarks>
public static class AdminInstallEndpoints
{
    public const int MaxPageSize = 200;
    public const int MaxEvents = 500;
    public const int MaxDays = 365;

    public static IEndpointRouteBuilder MapAdminInstalls(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAdmin();

        admin.MapGet("/installs", ListAsync);
        admin.MapGet("/installs/{installId:guid}", OneAsync);
        admin.MapGet("/installs/{installId:guid}/events", EventsAsync);
        admin.MapGet("/events-per-day", EventsPerDayAsync);
        admin.MapGet("/settings", SettingsAsync);
        admin.MapPut("/settings", SaveSettingsAsync);

        return app;
    }

    internal static async Task<IResult> ListAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] CloudContext cloud,
        [FromServices] EngineContext engine,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var skip = Math.Max(0, offset ?? 0);
        var take = Math.Clamp(limit ?? 50, 1, MaxPageSize);

        var total = await cloud.Installs.CountAsync(ct);
        var installs = await cloud.Installs.AsNoTracking()
            .OrderByDescending(i => i.LastSeenAt)
            .ThenBy(i => i.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        var items = await ViewsAsync(installs, cloud, engine, time, ct);
        return Results.Ok(new InstallPage(total, skip, take, items));
    }

    internal static async Task<IResult> OneAsync(
        [FromRoute] Guid installId,
        [FromServices] CloudContext cloud,
        [FromServices] EngineContext engine,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var install = await cloud.Installs.AsNoTracking().SingleOrDefaultAsync(i => i.Id == installId, ct);
        if (install is null)
            return Results.Json(new { error = "Install not found." }, statusCode: StatusCodes.Status404NotFound);

        return Results.Ok((await ViewsAsync([install], cloud, engine, time, ct))[0]);
    }

    internal static async Task<IResult> EventsAsync(
        [FromRoute] Guid installId,
        [FromQuery] int? limit,
        [FromServices] EngineContext engine,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 100, 1, MaxEvents);

        var events = await engine.Events.AsNoTracking()
            .Where(e => e.InstallId == installId)
            .OrderByDescending(e => e.ReceivedAt)
            .ThenByDescending(e => e.OccurredAt)
            .Take(take)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = events.Select(e => new EventView(
                e.ClientEventId,
                e.ReceivedAt,
                e.SentAt,
                e.OccurredAt,
                e.Type,
                e.TypeRaw,
                e.SubjectId,
                DisplayName(e.Data),
                e.WorldId,
                e.InstanceId,
                e.GroupId,
                e.Data)).ToList(),
        });
    }

    internal static async Task<IResult> EventsPerDayAsync(
        [FromQuery] int? days,
        [FromServices] EngineContext engine,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var span = Math.Clamp(days ?? 30, 1, MaxDays);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var first = today.AddDays(-(span - 1));

        var counts = await engine.EventDayTotals.AsNoTracking()
            .Where(t => t.Day >= first && t.Day <= today)
            .GroupBy(t => t.Day)
            .Select(g => new { Day = g.Key, Events = g.Sum(t => t.Events) })
            .ToDictionaryAsync(x => x.Day, x => x.Events, ct);

        // Every day in the range, zeros included, so the chart's bars line up with the calendar.
        var items = Enumerable.Range(0, span)
            .Select(i => first.AddDays(i))
            .Select(day => new DayCount(day, counts.GetValueOrDefault(day)))
            .ToList();

        return Results.Ok(new { items });
    }

    internal static async Task<IResult> SettingsAsync([FromServices] CloudContext cloud, CancellationToken ct)
    {
        var settings = await cloud.GetSettingsAsync(ct);
        return Results.Ok(new SettingsView(settings.EventKeepDays));
    }

    internal static async Task<IResult> SaveSettingsAsync([FromBody] SettingsView? request, [FromServices] CloudContext cloud, CancellationToken ct)
    {
        if (request is null || !CloudSettings.IsValidKeepDays(request.EventKeepDays))
        {
            return Results.Json(
                new { error = $"Days must be between 0 and {CloudSettings.MaxKeepDays}." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var settings = await cloud.Settings.SingleOrDefaultAsync(s => s.Id == CloudSettings.SingleRowId, ct);
        if (settings is null)
        {
            settings = new CloudSettings();
            cloud.Settings.Add(settings);
        }

        settings.EventKeepDays = request.EventKeepDays;
        await cloud.SaveChangesAsync(ct);

        return Results.Ok(new SettingsView(settings.EventKeepDays));
    }

    private static string? DisplayName(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<List<InstallView>> ViewsAsync(
        IReadOnlyList<Installs.Install> installs,
        CloudContext cloud,
        EngineContext engine,
        TimeProvider time,
        CancellationToken ct)
    {
        var ids = installs.Select(i => i.Id).ToList();
        var settings = await cloud.GetSettingsAsync(ct);

        var totals = engine.EventDayTotals.AsNoTracking().Where(t => ids.Contains(t.InstallId));
        if (settings.EventKeepDays > 0)
        {
            var from = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-settings.EventKeepDays);
            totals = totals.Where(t => t.Day >= from);
        }

        var events = await totals
            .GroupBy(t => t.InstallId)
            .Select(g => new { InstallId = g.Key, Events = g.Sum(t => t.Events) })
            .ToDictionaryAsync(x => x.InstallId, x => x.Events, ct);

        var clocks = await engine.InstallClocks.AsNoTracking()
            .Where(c => ids.Contains(c.InstallId))
            .ToDictionaryAsync(c => c.InstallId, ct);

        return
        [
            .. installs.Select(i => new InstallView(
                i.Id,
                i.ClientVersion,
                i.RegisteredAt,
                i.LastSeenAt,
                events.GetValueOrDefault(i.Id),
                clocks.TryGetValue(i.Id, out var clock) ? clock.AppliedOffsetMs : null,
                clock?.Disagrees ?? false,
                i.ModbotServerId)),
        ];
    }
}
