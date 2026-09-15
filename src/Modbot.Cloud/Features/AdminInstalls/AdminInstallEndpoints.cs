using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Retention;

namespace Modbot.Cloud.Features.AdminInstalls;

/// <summary>One install, as the admin list shows it.</summary>
/// <param name="LinesStored">Lines received within the log line window, from the daily totals.</param>
/// <param name="ClockOffsetMs">The correction applied to its latest batch, or null before its first.</param>
/// <param name="ClockDisagrees">Its own clock measure and Cloud's are more than five minutes apart.</param>
public sealed record InstallView(
    Guid InstallId,
    string ClientVersion,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    long LinesStored,
    long? ClockOffsetMs,
    bool ClockDisagrees,
    string? ModbotServerId);

public sealed record InstallPage(int Total, int Offset, int Limit, IReadOnlyList<InstallView> Items);

/// <summary>One stored line, for debugging.</summary>
/// <param name="Text">The line, with any instance <c>nonce</c> hidden.</param>
public sealed record LineView(
    DateTimeOffset ReceivedAt,
    DateTimeOffset SentAt,
    DateTime? LoggedAt,
    short? UtcOffsetMinutes,
    string File,
    long Offset,
    string Text);

public sealed record DayCount(DateOnly Day, long Lines);

public sealed record SettingsView(int LogLineKeepDays, int LogEventKeepDays);

/// <summary>
/// Cloud admin: installs, lines per day, one install's recent lines, and retention.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every endpoint here needs the admin sign-in.</strong> Raw lines hold other players' names
/// and ids and private instance locations (cloud log backup spec 10), so nothing that reads them is
/// reachable any other way — not with an install's secret, not anonymously.
/// </para>
/// <para>
/// <strong>Locations are never join links.</strong> Lines go out as plain text, and the
/// <c>nonce(…)</c> part of a location — the part that lets someone into a private or friends-only
/// instance — is replaced before it leaves the server, so it never reaches the admin's browser at
/// all. The stored line is untouched.
/// </para>
/// </remarks>
public static partial class AdminInstallEndpoints
{
    public const int MaxPageSize = 200;
    public const int MaxLines = 500;
    public const int MaxDays = 365;

    public static IEndpointRouteBuilder MapAdminInstalls(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAdmin();

        admin.MapGet("/installs", ListAsync);
        admin.MapGet("/installs/{installId:guid}", OneAsync);
        admin.MapGet("/installs/{installId:guid}/lines", LinesAsync);
        admin.MapGet("/lines-per-day", LinesPerDayAsync);
        admin.MapGet("/settings", SettingsAsync);
        admin.MapPut("/settings", SaveSettingsAsync);

        return app;
    }

    /// <summary>Hides the value inside every <c>nonce(…)</c>.</summary>
    public static string HideNonces(string text) =>
        text.Contains("nonce(", StringComparison.OrdinalIgnoreCase) ? Nonce().Replace(text, "nonce(hidden)") : text;

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

    internal static async Task<IResult> LinesAsync(
        [FromRoute] Guid installId,
        [FromQuery] int? limit,
        [FromServices] EngineContext engine,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 100, 1, MaxLines);

        var lines = await engine.LogLines.AsNoTracking()
            .Where(l => l.InstallId == installId)
            .OrderByDescending(l => l.ReceivedAt)
            .ThenByDescending(l => l.LineOffset)
            .Take(take)
            .Join(engine.LogFiles, l => l.LogFileId, f => f.Id, (l, f) => new { Line = l, File = f.Name })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = lines.Select(x => new LineView(
                x.Line.ReceivedAt,
                x.Line.SentAt,
                x.Line.LoggedAt,
                x.Line.UtcOffsetMinutes,
                x.File,
                x.Line.LineOffset,
                HideNonces(x.Line.Text))).ToList(),
        });
    }

    internal static async Task<IResult> LinesPerDayAsync(
        [FromQuery] int? days,
        [FromServices] EngineContext engine,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var span = Math.Clamp(days ?? 30, 1, MaxDays);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var first = today.AddDays(-(span - 1));

        var counts = await engine.LineDayTotals.AsNoTracking()
            .Where(t => t.Day >= first && t.Day <= today)
            .GroupBy(t => t.Day)
            .Select(g => new { Day = g.Key, Lines = g.Sum(t => t.Lines) })
            .ToDictionaryAsync(x => x.Day, x => x.Lines, ct);

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
        return Results.Ok(new SettingsView(settings.LogLineKeepDays, settings.LogEventKeepDays));
    }

    internal static async Task<IResult> SaveSettingsAsync([FromBody] SettingsView? request, [FromServices] CloudContext cloud, CancellationToken ct)
    {
        if (request is null
            || !CloudSettings.IsValidKeepDays(request.LogLineKeepDays)
            || !CloudSettings.IsValidKeepDays(request.LogEventKeepDays))
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

        settings.LogLineKeepDays = request.LogLineKeepDays;
        settings.LogEventKeepDays = request.LogEventKeepDays;
        await cloud.SaveChangesAsync(ct);

        return Results.Ok(new SettingsView(settings.LogLineKeepDays, settings.LogEventKeepDays));
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

        var totals = engine.LineDayTotals.AsNoTracking().Where(t => ids.Contains(t.InstallId));
        if (settings.LogLineKeepDays > 0)
        {
            var from = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-settings.LogLineKeepDays);
            totals = totals.Where(t => t.Day >= from);
        }

        var lines = await totals
            .GroupBy(t => t.InstallId)
            .Select(g => new { InstallId = g.Key, Lines = g.Sum(t => t.Lines) })
            .ToDictionaryAsync(x => x.InstallId, x => x.Lines, ct);

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
                lines.GetValueOrDefault(i.Id),
                clocks.TryGetValue(i.Id, out var clock) ? clock.AppliedOffsetMs : null,
                clock?.Disagrees ?? false,
                i.ModbotServerId)),
        ];
    }

    [GeneratedRegex(@"nonce\([^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Nonce();
}
