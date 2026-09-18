using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Configuration;
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
/// <para>
/// <strong>The three picture addresses are Cloud's own</strong> wherever Cloud kept a copy of the
/// picture, because the addresses an administrator types in are usually VRChat's and VRChat will not
/// serve them to anybody else. A reader gets an address that works with nothing to arrange — the web
/// app, the companion, and whatever reads them next. See <see cref="ShowcasePicture"/>.
/// </para>
/// </remarks>
public static class ShowcaseEndpoints
{
    /// <summary>
    /// A picture never changes under its address — a saved row writes a new one — so it is worth
    /// caching for as long as a browser will.
    /// </summary>
    private const string PictureCaching = "public, max-age=31536000, immutable";

    public static IEndpointRouteBuilder MapShowcase(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/sponsors", (
            [FromServices] CloudContext cloud,
            [FromServices] CloudPublicAddress here,
            CancellationToken ct) => ListAsync(cloud, here, ShowcaseKinds.Sponsor, ct));

        app.MapGet("/api/v1/early-adopters", (
            [FromServices] CloudContext cloud,
            [FromServices] CloudPublicAddress here,
            CancellationToken ct) => ListAsync(cloud, here, ShowcaseKinds.EarlyAdopter, ct));

        app.MapGet("/api/v1/contributors", async (
            [FromServices] GitHubContributors contributors,
            CancellationToken ct) => Results.Ok(new { items = await contributors.ReadAsync(ct) }));

        app.MapGet($"{ShowcasePicture.Route}/{{id:guid}}", PictureAsync);

        return app;
    }

    internal static async Task<IResult> ListAsync(
        CloudContext cloud, CloudPublicAddress here, string kind, CancellationToken ct)
    {
        var rows = await cloud.ShowcaseEntries.AsNoTracking()
            .Where(e => e.Kind == kind)
            .OrderBy(e => e.SortOrder)
            .ThenBy(e => e.Name)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Link,
                e.ImageUrl,
                e.VRChatGroupId,
                e.GroupImageUrl,
                e.GroupBannerUrl,
                e.SavedImageId,
                e.SavedGroupImageId,
                e.SavedGroupBannerId,
            })
            .ToListAsync(ct);

        var items = rows.Select(e => new ShowcaseView(
            e.Id,
            e.Name,
            e.Link,
            ShowcasePicture.Address(here.Address, e.SavedImageId, e.ImageUrl) ?? "",
            e.VRChatGroupId,
            ShowcasePicture.Address(here.Address, e.SavedGroupImageId, e.GroupImageUrl),
            ShowcasePicture.Address(here.Address, e.SavedGroupBannerId, e.GroupBannerUrl)));

        return Results.Ok(new { items });
    }

    /// <summary>One stored picture, served from Cloud's own domain to anybody who asks.</summary>
    internal static async Task<IResult> PictureAsync(
        [FromRoute] Guid id,
        HttpResponse response,
        [FromServices] CloudContext cloud,
        CancellationToken ct)
    {
        var picture = await cloud.ShowcasePictures.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.Bytes, p.ContentType })
            .SingleOrDefaultAsync(ct);

        if (picture is null)
            return Results.NotFound();

        response.Headers.CacheControl = PictureCaching;

        return Results.Bytes(picture.Bytes, picture.ContentType);
    }
}
