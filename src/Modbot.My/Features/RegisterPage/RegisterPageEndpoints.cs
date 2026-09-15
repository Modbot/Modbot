using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.My.Common;
using Modbot.My.Data;
using Modbot.My.Features.Admin;

namespace Modbot.My.Features.RegisterPage;

/// <summary>
/// The instance URLs noted by the pages (central services spec 4.1), as an admin reads and deletes
/// them. The noting itself is in <c>Features/Visits</c>.
/// </summary>
public static class RegisterPageEndpoints
{
    public static IEndpointRouteBuilder MapRegisterPage(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/register-page-instances", ListAsync).RequireAdmin();
        app.MapDelete("/api/register-page-instances", DeleteAsync).RequireAdmin();
        return app;
    }

    internal static async Task<IResult> ListAsync(
        [FromQuery] string? search,
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] MyContext db,
        CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);

        var query = db.RegisterPageInstances.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = Search.Contains(search);
            query = query.Where(i => EF.Functions.ILike(i.InstanceUrl, pattern));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(i => i.LastSeenAt)
            .ThenBy(i => i.InstanceUrl)
            .Skip(skip)
            .Take(take)
            .Select(i => new RegisterPageInstanceView(
                i.InstanceUrl,
                i.FirstSeenAt,
                i.LastSeenAt,
                i.Visits,
                db.RegisteredInstances.Any(r => r.InstanceUrl == i.InstanceUrl)))
            .ToListAsync(ct);

        return Results.Ok(new Page<RegisterPageInstanceView>(total, skip, take, rows));
    }

    /// <summary>
    /// Forgets a noted URL: its row here, and the IP history that would otherwise keep offering it to
    /// the people who opened it.
    /// </summary>
    internal static async Task<IResult> DeleteAsync(
        [FromQuery] string? url,
        [FromServices] MyContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Results.BadRequest(new { error = "url is required." });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var deleted = await db.RegisterPageInstances.Where(i => i.InstanceUrl == url).ExecuteDeleteAsync(ct);
        await db.VisitorInstances.Where(v => v.InstanceUrl == url).ExecuteDeleteAsync(ct);

        await transaction.CommitAsync(ct);

        return deleted == 0
            ? Results.NotFound(new { error = "No entry has that URL." })
            : Results.NoContent();
    }
}

/// <param name="AlsoRegistered">Whether a deployment at this URL has also registered itself.</param>
public sealed record RegisterPageInstanceView(
    string InstanceUrl,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int Visits,
    bool AlsoRegistered);
