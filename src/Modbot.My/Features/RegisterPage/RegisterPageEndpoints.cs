using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.My.Auth;
using Modbot.My.Common;
using Modbot.My.Data;
using Modbot.My.Features.Pages;

namespace Modbot.My.Features.RegisterPage;

/// <summary>
/// <c>/register</c>: serves the selector page, which saves the instance in the browser, and notes
/// the instance URL in its own table (central services spec 4.1).
/// </summary>
public static class RegisterPageEndpoints
{
    public static IEndpointRouteBuilder MapRegisterPage(this IEndpointRouteBuilder app)
    {
        app.MapGet("/register", RegisterAsync);
        app.MapGet("/api/register-page-instances", ListAsync).RequireRootApiKey();
        return app;
    }

    internal static async Task<IResult> RegisterAsync(
        [FromQuery] string? modbotInstanceUrl,
        [FromServices] MyContext db,
        [FromServices] TimeProvider time,
        [FromServices] SelectorPage page,
        [FromServices] ILoggerFactory logs,
        CancellationToken ct)
    {
        if (InstanceUrl.TryNormalise(modbotInstanceUrl, out var url))
        {
            try
            {
                await NoteAsync(db, url, time.GetUtcNow(), ct);
            }
            catch (Exception e) when (e is DbUpdateException or Npgsql.NpgsqlException)
            {
                // The page still has to work: saving the instance in the browser is what the person
                // came for, and the server-side note is only a backup count.
                logs.CreateLogger(typeof(RegisterPageEndpoints))
                    .LogWarning(e, "Could not note a register page visit for {InstanceUrl}", url);
            }
        }

        return page.Serve();
    }

    /// <summary>
    /// One statement, so two visits for the same URL at the same moment cannot both insert.
    /// </summary>
    internal static Task NoteAsync(MyContext db, string url, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO register_page_instance (instance_url, first_seen_at, last_seen_at, visits)
            VALUES ({url}, {now}, {now}, 1)
            ON CONFLICT (instance_url) DO UPDATE
            SET last_seen_at = GREATEST(register_page_instance.last_seen_at, EXCLUDED.last_seen_at),
                visits = register_page_instance.visits + 1
            """,
            ct);

    internal static async Task<IResult> ListAsync(
        [FromQuery] int? offset,
        [FromQuery] int? limit,
        [FromServices] MyContext db,
        CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);

        var total = await db.RegisterPageInstances.CountAsync(ct);
        var rows = await db.RegisterPageInstances
            .AsNoTracking()
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
}

/// <param name="AlsoRegistered">Whether a deployment at this URL has also registered itself.</param>
public sealed record RegisterPageInstanceView(
    string InstanceUrl,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int Visits,
    bool AlsoRegistered);
