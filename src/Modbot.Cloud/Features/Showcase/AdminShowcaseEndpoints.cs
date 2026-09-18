using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
using Modbot.Cloud.Configuration;
using Modbot.Cloud.Data;
using Modbot.Cloud.Features.Admin;

namespace Modbot.Cloud.Features.Showcase;

/// <param name="Kind"><c>sponsor</c> or <c>early-adopter</c>.</param>
/// <param name="Name">What to call them.</param>
/// <param name="Link">Where clicking the name goes.</param>
/// <param name="ImageUrl">Their picture.</param>
/// <param name="VRChatGroupId">Their VRChat group, when they have one.</param>
/// <param name="GroupImageUrl">The group's icon.</param>
/// <param name="GroupBannerUrl">The group's banner.</param>
/// <param name="SortOrder">Lowest first.</param>
public sealed record ShowcaseUpdate(
    string Kind,
    string? Name,
    string? Link,
    string? ImageUrl,
    string? VRChatGroupId,
    string? GroupImageUrl,
    string? GroupBannerUrl,
    int SortOrder);

/// <param name="Kind"><c>sponsor</c> or <c>early-adopter</c>.</param>
/// <param name="ImageUrl">The picture address as it was typed in.</param>
/// <param name="GroupImageUrl">The group icon address as it was typed in.</param>
/// <param name="GroupBannerUrl">The group banner address as it was typed in.</param>
/// <param name="SavedImageUrl">Cloud's own copy of the picture, or null when it could not be fetched.</param>
/// <param name="SavedGroupImageUrl">Cloud's own copy of the group icon, or null.</param>
/// <param name="SavedGroupBannerUrl">Cloud's own copy of the group banner, or null.</param>
/// <param name="AddedAt">When it was typed in.</param>
public sealed record AdminShowcaseView(
    Guid Id,
    string Kind,
    string Name,
    string Link,
    string ImageUrl,
    string? VRChatGroupId,
    string? GroupImageUrl,
    string? GroupBannerUrl,
    string? SavedImageUrl,
    string? SavedGroupImageUrl,
    string? SavedGroupBannerUrl,
    int SortOrder,
    DateTimeOffset AddedAt);

/// <summary>
/// Cloud admin: typing in the sponsors and early adopters every Modbot shows.
/// </summary>
/// <remarks>
/// <para>
/// Behind the admin sign-in, because these rows appear on every Modbot's Credits page and there is
/// no undoing a name that should not have been there.
/// </para>
/// <para>
/// <strong>Saving a row fetches its pictures.</strong> Cloud keeps its own copy of the picture, the
/// group icon and the group banner and serves them from its own domain from then on, because the
/// addresses typed in here are usually VRChat's and VRChat refuses to serve them to anybody else.
/// The typed-in addresses are kept exactly as entered, so an administrator sees what they wrote and
/// the next save fetches from there again. A fetch that fails leaves whatever copy the row already
/// had — one bad minute at somebody else's host should not empty a picture that was working.
/// </para>
/// </remarks>
public static class AdminShowcaseEndpoints
{
    /// <summary>Only <c>http</c> and <c>https</c> links are stored, so a row can never carry a script.</summary>
    private static bool IsSafeLink(string? url) =>
        string.IsNullOrWhiteSpace(url)
        || (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https");

    public static IEndpointRouteBuilder MapAdminShowcase(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var admin = app.MapGroup("/api/admin").RequireAdmin();

        admin.MapGet("/showcase", ListAsync);
        admin.MapPost("/showcase", AddAsync);
        admin.MapPut("/showcase/{id:guid}", SaveAsync);
        admin.MapDelete("/showcase/{id:guid}", RemoveAsync);

        return app;
    }

    internal static async Task<IResult> ListAsync(
        [FromServices] CloudContext cloud,
        [FromServices] CloudPublicAddress here,
        CancellationToken ct)
    {
        var entries = await cloud.ShowcaseEntries.AsNoTracking()
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.SortOrder)
            .ThenBy(e => e.Name)
            .ToListAsync(ct);

        var items = entries.Select(e => View(e, here));

        return Results.Ok(new { items });
    }

    internal static async Task<IResult> AddAsync(
        [FromBody] ShowcaseUpdate? request,
        [FromServices] CloudContext cloud,
        [FromServices] ShowcasePictures pictures,
        [FromServices] CloudPublicAddress here,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (Problem(request) is { } problem)
            return problem;

        var entry = new ShowcaseEntry { Id = Guid.CreateVersion7(), AddedAt = time.GetUtcNow() };

        Apply(entry, request!);
        await SavePicturesAsync(cloud, pictures, entry, time.GetUtcNow(), ct);
        cloud.ShowcaseEntries.Add(entry);

        await cloud.SaveChangesAsync(ct);

        return Results.Json(View(entry, here), statusCode: StatusCodes.Status201Created);
    }

    internal static async Task<IResult> SaveAsync(
        [FromRoute] Guid id,
        [FromBody] ShowcaseUpdate? request,
        [FromServices] CloudContext cloud,
        [FromServices] ShowcasePictures pictures,
        [FromServices] CloudPublicAddress here,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (Problem(request) is { } problem)
            return problem;

        var entry = await cloud.ShowcaseEntries.SingleOrDefaultAsync(e => e.Id == id, ct);

        if (entry is null)
            return Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound);

        Apply(entry, request!);
        await SavePicturesAsync(cloud, pictures, entry, time.GetUtcNow(), ct);
        await cloud.SaveChangesAsync(ct);

        return Results.Ok(View(entry, here));
    }

    internal static async Task<IResult> RemoveAsync(
        [FromRoute] Guid id,
        [FromServices] CloudContext cloud,
        CancellationToken ct)
    {
        var saved = await cloud.ShowcaseEntries.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new { e.SavedImageId, e.SavedGroupImageId, e.SavedGroupBannerId })
            .SingleOrDefaultAsync(ct);

        var removed = await cloud.ShowcaseEntries.Where(e => e.Id == id).ExecuteDeleteAsync(ct);

        if (removed == 0)
            return Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound);

        // The pictures go with the row. Nothing else points at them.
        if (saved is not null)
        {
            await ForgetAsync(cloud, saved.SavedImageId, ct);
            await ForgetAsync(cloud, saved.SavedGroupImageId, ct);
            await ForgetAsync(cloud, saved.SavedGroupBannerId, ct);
        }

        return Results.NoContent();
    }

    private static IResult? Problem(ShowcaseUpdate? request)
    {
        if (request is null)
            return Results.Json(new { error = "Nothing to save." }, statusCode: StatusCodes.Status400BadRequest);

        if (!ShowcaseKinds.IsKnown(request.Kind))
            return Results.Json(new { error = "Kind is sponsor or early-adopter." }, statusCode: StatusCodes.Status400BadRequest);

        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Json(new { error = "A name is needed." }, statusCode: StatusCodes.Status400BadRequest);

        foreach (var url in new[] { request.Link, request.ImageUrl, request.GroupImageUrl, request.GroupBannerUrl })
        {
            if (!IsSafeLink(url))
                return Results.Json(new { error = "Links must start with http or https." }, statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }

    private static void Apply(ShowcaseEntry entry, ShowcaseUpdate request)
    {
        entry.Kind = request.Kind;
        entry.Name = ClientText.Clean(request.Name, ShowcaseEntry.MaxNameLength) ?? "";
        entry.Link = ClientText.Clean(request.Link, ShowcaseEntry.MaxUrlLength) ?? "";
        entry.ImageUrl = ClientText.Clean(request.ImageUrl, ShowcaseEntry.MaxUrlLength) ?? "";

        // Never checked for shape: a legacy VRChat id follows no structure (foundation 3.1.1).
        entry.VRChatGroupId = ClientText.Clean(request.VRChatGroupId, ShowcaseEntry.MaxGroupIdLength);

        entry.GroupImageUrl = ClientText.Clean(request.GroupImageUrl, ShowcaseEntry.MaxUrlLength);
        entry.GroupBannerUrl = ClientText.Clean(request.GroupBannerUrl, ShowcaseEntry.MaxUrlLength);
        entry.SortOrder = Math.Clamp(request.SortOrder, -1_000_000, 1_000_000);
    }

    /// <summary>Fetches the row's three pictures and points the row at the copies Cloud keeps.</summary>
    private static async Task SavePicturesAsync(
        CloudContext cloud, ShowcasePictures pictures, ShowcaseEntry entry, DateTimeOffset now, CancellationToken ct)
    {
        entry.SavedImageId = await RefreshAsync(cloud, pictures, entry.SavedImageId, entry.ImageUrl, now, ct);
        entry.SavedGroupImageId = await RefreshAsync(cloud, pictures, entry.SavedGroupImageId, entry.GroupImageUrl, now, ct);
        entry.SavedGroupBannerId = await RefreshAsync(cloud, pictures, entry.SavedGroupBannerId, entry.GroupBannerUrl, now, ct);
    }

    private static async Task<Guid?> RefreshAsync(
        CloudContext cloud,
        ShowcasePictures pictures,
        Guid? saved,
        string? typed,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            await ForgetAsync(cloud, saved, ct);
            return null;
        }

        if (await pictures.FetchAsync(typed, now, ct) is { } fetched)
        {
            cloud.ShowcasePictures.Add(fetched);
            await ForgetAsync(cloud, saved, ct);
            return fetched.Id;
        }

        if (saved is null)
            return null;

        // Nothing usable at that address just now. A copy already kept for the same address stays —
        // the picture on every Modbot's Credits page should not empty because the other host had a
        // bad minute. A copy of a different address goes: it is no longer what the row says.
        var stillTheSameAddress = await cloud.ShowcasePictures.AsNoTracking()
            .AnyAsync(p => p.Id == saved && p.SourceUrl == typed.Trim(), ct);

        if (stillTheSameAddress)
            return saved;

        await ForgetAsync(cloud, saved, ct);
        return null;
    }

    private static async Task ForgetAsync(CloudContext cloud, Guid? id, CancellationToken ct)
    {
        if (id is { } saved)
            await cloud.ShowcasePictures.Where(p => p.Id == saved).ExecuteDeleteAsync(ct);
    }

    private static AdminShowcaseView View(ShowcaseEntry e, CloudPublicAddress here) => new(
        e.Id, e.Kind, e.Name, e.Link, e.ImageUrl, e.VRChatGroupId, e.GroupImageUrl, e.GroupBannerUrl,
        ShowcasePicture.Address(here.Address, e.SavedImageId, null),
        ShowcasePicture.Address(here.Address, e.SavedGroupImageId, null),
        ShowcasePicture.Address(here.Address, e.SavedGroupBannerId, null),
        e.SortOrder, e.AddedAt);
}
