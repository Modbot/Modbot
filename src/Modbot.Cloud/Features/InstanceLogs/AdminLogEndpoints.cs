using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.Admin;

namespace Modbot.Cloud.Features.InstanceLogs;

/// <summary>One stored log line, as the viewer shows it.</summary>
/// <param name="Id">Row id. Also the paging cursor: ask for lines <c>before</c> this one.</param>
/// <param name="ReceivedAt">When Cloud received it.</param>
/// <param name="At">When the deployment wrote it, on its own clock.</param>
/// <param name="Properties">Everything else it carried, as a JSON object in a string.</param>
public sealed record LogLineView(
    long Id,
    Guid InstallId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset At,
    string Level,
    string Message,
    string? Template,
    string? Source,
    string? Area,
    string? Service,
    string? Version,
    string? Exception,
    string Properties);

/// <param name="Next">The id to pass as <c>before</c> for the next page. Null at the end.</param>
public sealed record LogLinePage(IReadOnlyList<LogLineView> Items, long? Next);

/// <summary>A deployment that has sent logs, for the viewer's picker.</summary>
public sealed record LogSenderView(Guid InstallId, string Version, DateTimeOffset LastSeenAt);

/// <summary>
/// Reading the log lines Modbot deployments sent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Cloud administrators today.</strong> A deployment's log is its operator's, not the
/// project's, and the only reason Cloud holds it is so the project can help with a problem on a
/// deployment it cannot reach.
/// </para>
/// <para>
/// <strong>The owner of the deployment is meant to read it too</strong>, through the Cloud account
/// their Modbot is linked to. Accounts and the instance registry are not built yet, so there is
/// exactly one place to add that: <see cref="MayReadAsync"/>. It answers "admin only" today; when
/// accounts land it also answers yes for the account that owns <c>installId</c>, and nothing else in
/// this file changes.
/// </para>
/// <para>
/// The lines are somebody else's text. They are returned as plain strings and the viewer renders
/// them as text, never as markup.
/// </para>
/// </remarks>
public static class AdminLogEndpoints
{
    public const int MaxPageSize = 500;
    public const int DefaultPageSize = 100;

    public static IEndpointRouteBuilder MapAdminLogs(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var admin = app.MapGroup("/api/admin").RequireAdmin();

        admin.MapGet("/logs", ListAsync);
        admin.MapGet("/logs/senders", SendersAsync);

        return app;
    }

    /// <summary>
    /// Whether this request may read <paramref name="installId"/>'s log.
    /// </summary>
    /// <remarks>
    /// The one place the owner check belongs. Today the whole group is behind
    /// <see cref="AdminAccess.RequireAdmin"/>, so this can only be reached by an administrator and
    /// it says yes. When Cloud has accounts and knows which account a deployment belongs to, this
    /// becomes "an administrator, or the account that owns this install", and the group above it
    /// stops requiring admin.
    /// </remarks>
    private static Task<bool> MayReadAsync(HttpContext http, Guid? installId, CancellationToken ct)
    {
        _ = http;
        _ = installId;
        _ = ct;

        return Task.FromResult(true);
    }

    internal static async Task<IResult> ListAsync(
        [FromQuery] Guid? installId,
        [FromQuery] string? level,
        [FromQuery] string? source,
        [FromQuery] string? text,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] long? before,
        [FromQuery] int? limit,
        [FromServices] EngineContext engine,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await MayReadAsync(http, installId, ct))
            return Results.Json(new { error = "Not allowed." }, statusCode: StatusCodes.Status403Forbidden);

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var levels = LogLevelNames.AtLeast(level);

        var query = engine.InstanceLogs.AsNoTracking().Where(l => levels.Contains(l.Level));

        if (installId is { } id)
            query = query.Where(l => l.InstallId == id);

        if (!string.IsNullOrWhiteSpace(source))
            query = query.Where(l => l.Source == source);

        if (from is { } start)
            query = query.Where(l => l.ReceivedAt >= start);

        if (to is { } end)
            query = query.Where(l => l.ReceivedAt < end);

        if (!string.IsNullOrWhiteSpace(text))
        {
            var pattern = "%" + Escape(text.Trim()) + "%";

            query = query.Where(l =>
                EF.Functions.ILike(l.Message, pattern, "\\")
                || (l.Exception != null && EF.Functions.ILike(l.Exception, pattern, "\\")));
        }

        if (before is { } cursor)
            query = query.Where(l => l.Id < cursor);

        var lines = await query
            .OrderByDescending(l => l.ReceivedAt)
            .ThenByDescending(l => l.Id)
            .Take(take + 1)
            .Select(l => new LogLineView(
                l.Id, l.InstallId, l.ReceivedAt, l.At, l.Level, l.Message, l.Template,
                l.Source, l.Area, l.Service, l.Version, l.Exception, l.Properties))
            .ToListAsync(ct);

        long? next = null;

        if (lines.Count > take)
        {
            lines.RemoveAt(take);
            next = lines[^1].Id;
        }

        return Results.Ok(new LogLinePage(lines, next));
    }

    /// <summary>
    /// The deployments that send logs, for the picker. Read from the install table rather than from
    /// the lines: asking the log table for its distinct install ids would scan every partition.
    /// </summary>
    internal static async Task<IResult> SendersAsync(
        [FromServices] CloudContext cloud,
        CancellationToken ct)
    {
        var senders = await cloud.Installs.AsNoTracking()
            .Where(i => i.Platform == InstanceLogEndpoints.ServerPlatform)
            .OrderByDescending(i => i.LastSeenAt)
            .Take(500)
            .Select(i => new LogSenderView(i.Id, i.ClientVersion, i.LastSeenAt))
            .ToListAsync(ct);

        return Results.Ok(new { items = senders });
    }

    /// <summary>Makes a person's search text safe for <c>ILIKE</c>.</summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
