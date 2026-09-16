using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.Admin;

namespace Modbot.Cloud.Features.InstanceLogs;

/// <summary>One stored log line, as the viewer shows it.</summary>
/// <param name="Id">Row id. Also the paging cursor: ask for lines <c>before</c> this one.</param>
/// <param name="ServerId">The deployment that sent it, by its id in the server registry.</param>
/// <param name="ReceivedAt">When Cloud received it.</param>
/// <param name="At">When the deployment wrote it, on its own clock.</param>
/// <param name="Properties">Everything else it carried, as a JSON object in a string.</param>
public sealed record LogLineView(
    long Id,
    Guid ServerId,
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

/// <summary>A deployment that sends logs, for the viewer's picker.</summary>
public sealed record LogSenderView(Guid ServerId, string? GroupName, string? Version, DateTimeOffset LastSeenAt);

/// <summary>
/// Reading the log lines Modbot deployments sent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A Cloud administrator, or the account that claimed the deployment.</strong> A
/// deployment's log is its operator's, not the project's; Cloud holds it so the project can help
/// with a problem on a deployment it cannot reach, and so the operator can read it from somewhere
/// their own Modbot is not.
/// </para>
/// <para>
/// An owner must name their server — there is no "everyone's logs" for an account — and
/// <see cref="MayReadAsync"/> is the one place that decides. An administrator may leave the server
/// out and read across every deployment, which is what makes "who else is seeing this?" answerable.
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

        // Not under the admin group: an owner reads their own server's logs here too, and the check
        // is per request because it depends on which server was asked for.
        app.MapGet("/api/admin/logs", ListAsync);
        app.MapGet("/api/admin/logs/senders", SendersAsync);

        return app;
    }

    /// <summary>
    /// Whether this request may read <paramref name="serverId"/>'s log.
    /// </summary>
    /// <remarks>
    /// The one place the rule lives. An administrator may read anything, including across every
    /// deployment at once. An account may read a server it has claimed, and must say which.
    /// </remarks>
    private static async Task<bool> MayReadAsync(HttpContext http, Guid? serverId)
    {
        if (await AdminAccess.IsAdminAsync(http))
            return true;

        if (serverId is not { } id)
            return false;

        if (await AccountAccess.ReadAsync(http) is not { } account)
            return false;

        var cloud = http.RequestServices.GetRequiredService<CloudContext>();

        return await cloud.RegisteredServers
            .AnyAsync(s => s.Id == id && s.AccountId == account.Id, http.RequestAborted);
    }

    internal static async Task<IResult> ListAsync(
        [FromQuery] Guid? serverId,
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
        if (!await MayReadAsync(http, serverId))
            return Refused(http);

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var levels = LogLevelNames.AtLeast(level);

        var query = engine.InstanceLogs.AsNoTracking().Where(l => levels.Contains(l.Level));

        if (serverId is { } id)
            query = query.Where(l => l.ServerId == id);

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
                l.Id, l.ServerId, l.ReceivedAt, l.At, l.Level, l.Message, l.Template,
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
    /// The deployments whose logs this request may read. An administrator sees every registered
    /// server; an account sees the ones it claimed.
    /// </summary>
    /// <remarks>
    /// Read from the registry rather than from the log table: asking that for its distinct server
    /// ids would scan every month's partition.
    /// </remarks>
    internal static async Task<IResult> SendersAsync(
        [FromServices] CloudContext cloud,
        HttpContext http,
        CancellationToken ct)
    {
        var servers = cloud.RegisteredServers.AsNoTracking();

        if (!await AdminAccess.IsAdminAsync(http))
        {
            if (await AccountAccess.ReadAsync(http) is not { } account)
                return Refused(http);

            servers = servers.Where(s => s.AccountId == account.Id);
        }

        var items = await servers
            .OrderByDescending(s => s.LastSeenAt)
            .Take(500)
            .Select(s => new LogSenderView(s.Id, s.GroupName, s.Version, s.LastSeenAt))
            .ToListAsync(ct);

        return Results.Ok(new { items });
    }

    private static IResult Refused(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";

        return Results.Json(
            new { error = "Not signed in." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>Makes a person's search text safe for <c>ILIKE</c>.</summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
