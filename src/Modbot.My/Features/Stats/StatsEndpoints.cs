using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.My.Data;
using Modbot.My.Features.Admin;

namespace Modbot.My.Features.Stats;

/// <summary>Counts across the registry, for admins.</summary>
public static class StatsEndpoints
{
    public static IEndpointRouteBuilder MapStats(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/stats", GetAsync).RequireAdmin();
        return app;
    }

    internal static async Task<IResult> GetAsync(
        [FromServices] MyContext db,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        var activeSince = time.GetUtcNow().AddDays(-30);

        var registered = await db.RegisteredInstances.CountAsync(ct);
        var active = await db.RegisteredInstances.CountAsync(i => i.LastSeenAt >= activeSince, ct);
        var withAnalytics = await db.RegisteredInstances.CountAsync(i => i.AnalyticsEnabled, ct);

        var byVersion = await db.RegisteredInstances
            .Where(i => i.Version != null)
            .GroupBy(i => i.Version!)
            .Select(g => new { Version = g.Key, Count = g.Count() })
            .ToDictionaryAsync(v => v.Version, v => v.Count, ct);

        var registerPage = await db.RegisterPageInstances.CountAsync(ct);
        var registerPageOnly = await db.RegisterPageInstances
            .CountAsync(p => !db.RegisteredInstances.Any(r => r.InstanceUrl == p.InstanceUrl), ct);

        return Results.Ok(new RegistryStats(
            registered, active, withAnalytics, byVersion, registerPage, registerPageOnly));
    }
}

/// <param name="RegisteredInstances">Deployments that registered themselves.</param>
/// <param name="ActiveLast30Days">Of those, seen in the last 30 days.</param>
/// <param name="WithAnalytics">Of those, have sent at least one usage report.</param>
/// <param name="ByVersion">Registered deployments per release.</param>
/// <param name="RegisterPageInstances">URLs noted by the pages.</param>
/// <param name="RegisterPageOnly">
/// Of those, URLs no registered deployment uses: deployments known only from the pages.
/// </param>
public sealed record RegistryStats(
    int RegisteredInstances,
    int ActiveLast30Days,
    int WithAnalytics,
    IReadOnlyDictionary<string, int> ByVersion,
    int RegisterPageInstances,
    int RegisterPageOnly);
