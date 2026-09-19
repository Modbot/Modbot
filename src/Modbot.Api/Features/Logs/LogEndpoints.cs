using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging.Store;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Logs;

/// <summary>
/// Modbot's own log, read from the database.
/// </summary>
/// <remarks>
/// <para>
/// Behind <see cref="ModbotPermissions.ViewOperationalLog"/>, the same line spec 5.9.4 draws for the
/// operational half of the audit log and for the Health page: the log is Modbot talking about
/// itself, and it carries settings changes and sign-in failures that a moderator who may see bans
/// has no business reading.
/// </para>
/// <para>
/// Paged by row id rather than by page number. The table is written to constantly, so a page number
/// would show the same line twice as soon as anything arrived between one page and the next.
/// </para>
/// <para>
/// The list is <em>ordered</em> by that same row id, and that is not incidental: paging on one
/// column while ordering by another drops rows between the pages. Ordering by <c>at</c> while
/// paging on <c>id</c> did exactly that — a line written late but stamped early sat above the
/// boundary row in the sort and below it in the filter, so it appeared on neither page. For a log
/// the row id is also the more honest order: it is the order the lines were written, where the
/// timestamp is whatever the writer put on them.
/// </para>
/// </remarks>
public static class LogEndpoints
{
    /// <summary>The most lines in one page.</summary>
    public const int MaxPageSize = 200;

    public const int DefaultPageSize = 100;

    public static IEndpointRouteBuilder MapLogs(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/logs").WithTags("Logs").RequireAuthorization();

        group.MapGet("", async (
                [FromQuery] string? level,
                [FromQuery] string? source,
                [FromQuery] string? area,
                [FromQuery] string? text,
                [FromQuery] DateTimeOffset? from,
                [FromQuery] DateTimeOffset? to,
                [FromQuery] long? before,
                [FromQuery] int? limit,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var size = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
                var levels = LogLevels.AtLeast(level);

                var query = db.Logs.AsNoTracking().Where(e => levels.Contains(e.Level));

                if (!string.IsNullOrWhiteSpace(source))
                    query = query.Where(e => e.Source == source);

                if (!string.IsNullOrWhiteSpace(area))
                    query = query.Where(e => e.Area == area);

                if (from is { } start)
                    query = query.Where(e => e.At >= start);

                if (to is { } end)
                    query = query.Where(e => e.At < end);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    // ILIKE rather than full-text search: an operator looking for a log line types
                    // a fragment of an id or a world name, not a word, and a tsvector index would
                    // not match either. The window is always narrowed by level or time first.
                    var pattern = "%" + Escape(text.Trim()) + "%";

                    query = query.Where(e =>
                        EF.Functions.ILike(e.Message, pattern, "\\")
                        || (e.Exception != null && EF.Functions.ILike(e.Exception, pattern, "\\")));
                }

                if (before is { } cursor)
                    query = query.Where(e => e.Id < cursor);

                // Ordered by id, which is the one thing `before` narrows on. It used to order by
                // the time the line carries and page on the id, which are two different orders:
                // a line written late but stamped early sits above the cursor's row in the sort
                // and below it in the filter, so it was never shown on either page. The id is the
                // order the lines were written, which for a log is the honest one -- the time is
                // whatever the writer put on it, and a batch can hand them over out of order.
                var lines = await query
                    .OrderByDescending(e => e.Id)
                    .Take(size + 1)
                    .Select(e => new LogLine(
                        e.Id, e.At, e.Level, e.Message, e.Template, e.Source, e.Area, e.Exception, e.Properties))
                    .ToListAsync(ct);

                long? next = null;

                if (lines.Count > size)
                {
                    lines.RemoveAt(size);
                    next = lines[^1].Id;
                }

                return Results.Ok(new LogPage(lines, next, clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewOperationalLog)
            .WithName("GetLogs")
            .WithSummary("List log lines")
            .WithDescription(
                "Modbot's own log, newest first. "
                + "`level` is the lowest level shown, so `Warning` gives warnings, errors and fatal "
                + "lines. `text` matches the message and the exception, anywhere in either.\n\n"
                + "Outbound API traffic is not in this table. It is written to "
                + "`modbot_log_http_*.jsonl` and to Seq, and there is far too much of it to keep "
                + "for six months in the database.")
            .Produces<LogPage>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/filters", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var sources = await db.Logs.AsNoTracking()
                    .Where(e => e.Source != null)
                    .Select(e => e.Source!)
                    .Distinct()
                    .OrderBy(s => s)
                    .Take(500)
                    .ToListAsync(ct);

                var areas = await db.Logs.AsNoTracking()
                    .Where(e => e.Area != null)
                    .Select(e => e.Area!)
                    .Distinct()
                    .OrderBy(a => a)
                    .ToListAsync(ct);

                var stored = await db.Logs.AsNoTracking().LongCountAsync(ct);

                var oldest = stored == 0
                    ? (DateTimeOffset?)null
                    : await db.Logs.AsNoTracking().MinAsync(e => (DateTimeOffset?)e.At, ct);

                return Results.Ok(new LogFilters(LogLevels.All, sources, areas, stored, oldest));
            })
            .RequiresFlag(ModbotPermissions.ViewOperationalLog)
            .WithName("GetLogFilters")
            .WithSummary("Get log filters")
            .WithDescription("The sources and areas that have written a line, and how many are stored.")
            .Produces<LogFilters>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/settings", async (
                [FromServices] ModbotContext db,
                // Optional, like the rest of the read surface: a host that mapped the API without
                // registering the Cloud address still answers, and says sending is allowed.
                [FromServices] ModbotCloudAddress? cloud,
                CancellationToken ct) =>
            {
                var settings = await db.Settings.AsNoTracking()
                    .Where(s => s.Id == 1)
                    .Select(s => new { s.LogRetentionDays, s.ShipLogsToCloud })
                    .FirstOrDefaultAsync(ct);

                return Results.Ok(new LogSettings(
                    settings?.LogRetentionDays ?? LogStore.DefaultRetentionDays,
                    settings?.ShipLogsToCloud ?? true,
                    cloud is null || !cloud.Disabled));
            })
            .RequiresFlag(ModbotPermissions.ViewOperationalLog)
            .WithName("GetLogSettings")
            .WithSummary("Get log settings")
            .WithDescription("How long stored log lines are kept, and whether they go to Modbot Cloud.")
            .Produces<LogSettings>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/settings", async (
                [FromBody] LogSettingsUpdate body,
                [FromServices] ModbotContext db,
                // Optional, like the rest of the read surface: a host that mapped the API without
                // registering the Cloud address still answers, and says sending is allowed.
                [FromServices] ModbotCloudAddress? cloud,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (!LogStore.IsValidRetention(body.KeepDays))
                {
                    return Results.BadRequest(new
                    {
                        error = $"Keep logs for 0 to {LogStore.MaxRetentionDays} days. 0 keeps them forever.",
                    });
                }

                var settings = await db.GetSettingsAsync(ct);

                settings.LogRetentionDays = body.KeepDays;
                settings.ShipLogsToCloud = body.SendToCloud;

                await db.SaveChangesAsync(ct);

                return Results.Ok(new LogSettings(body.KeepDays, body.SendToCloud, cloud is null || !cloud.Disabled));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .WithName("SetLogSettings")
            .WithSummary("Update log settings")
            .WithDescription(
                "Set how long stored log lines are kept, and whether they go to Modbot Cloud.")
            .Produces<LogSettings>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>
    /// Makes a person's search text safe for <c>ILIKE</c>. Without this, a message containing a
    /// percent sign searches for everything.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
