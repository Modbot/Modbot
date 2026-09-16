using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Data;

namespace Modbot.Cloud.Features.Showcase;

/// <param name="Id">The row, so admin can edit it.</param>
/// <param name="Name">What to call them.</param>
/// <param name="Link">Where clicking the name goes. Empty when there is nowhere.</param>
/// <param name="ImageUrl">Their picture. Empty when there is none.</param>
/// <param name="VRChatGroupId">Their VRChat group, when they have one.</param>
/// <param name="GroupImageUrl">The group's icon.</param>
/// <param name="GroupBannerUrl">The group's banner.</param>
public sealed record ShowcaseView(
    Guid Id,
    string Name,
    string Link,
    string ImageUrl,
    string? VRChatGroupId,
    string? GroupImageUrl,
    string? GroupBannerUrl);

/// <summary>
/// The showcase every Modbot reads: sponsors, early adopters and the repository's contributors.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Unauthenticated, on purpose.</strong> Every Modbot in the world shows these on its
/// Credits page, and asking each one to hold a key to read a list of names the project publishes
/// anyway would be a credential for nothing. Nothing here is about a group, an install or a person
/// using Modbot.
/// </para>
/// <para>
/// A Modbot caches what it reads and shows nothing when Cloud cannot be reached, so these being slow
/// or down costs a section of one page and nothing else.
/// </para>
/// </remarks>
public static class ShowcaseEndpoints
{
    public static IEndpointRouteBuilder MapShowcase(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/sponsors", (
            [FromServices] CloudContext cloud,
            CancellationToken ct) => ListAsync(cloud, ShowcaseKinds.Sponsor, ct));

        app.MapGet("/api/v1/early-adopters", (
            [FromServices] CloudContext cloud,
            CancellationToken ct) => ListAsync(cloud, ShowcaseKinds.EarlyAdopter, ct));

        app.MapGet("/api/v1/contributors", async (
            [FromServices] GitHubContributors contributors,
            CancellationToken ct) => Results.Ok(new { items = await contributors.ReadAsync(ct) }));

        return app;
    }

    internal static async Task<IResult> ListAsync(CloudContext cloud, string kind, CancellationToken ct)
    {
        var items = await cloud.ShowcaseEntries.AsNoTracking()
            .Where(e => e.Kind == kind)
            .OrderBy(e => e.SortOrder)
            .ThenBy(e => e.Name)
            .Select(e => new ShowcaseView(
                e.Id, e.Name, e.Link, e.ImageUrl, e.VRChatGroupId, e.GroupImageUrl, e.GroupBannerUrl))
            .ToListAsync(ct);

        return Results.Ok(new { items });
    }
}
