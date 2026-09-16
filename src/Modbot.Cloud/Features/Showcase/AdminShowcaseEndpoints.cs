using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Common;
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
    int SortOrder,
    DateTimeOffset AddedAt);

/// <summary>
/// Cloud admin: typing in the sponsors and early adopters every Modbot shows.
/// </summary>
/// <remarks>
/// Behind the admin sign-in, because these rows appear on every Modbot's Credits page and there is
/// no undoing a name that should not have been there.
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

    internal static async Task<IResult> ListAsync([FromServices] CloudContext cloud, CancellationToken ct)
    {
        var items = await cloud.ShowcaseEntries.AsNoTracking()
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.SortOrder)
            .ThenBy(e => e.Name)
            .Select(e => View(e))
            .ToListAsync(ct);

        return Results.Ok(new { items });
    }

    internal static async Task<IResult> AddAsync(
        [FromBody] ShowcaseUpdate? request,
        [FromServices] CloudContext cloud,
        [FromServices] TimeProvider time,
        CancellationToken ct)
    {
        if (Problem(request) is { } problem)
            return problem;

        var entry = new ShowcaseEntry { Id = Guid.CreateVersion7(), AddedAt = time.GetUtcNow() };

        Apply(entry, request!);
        cloud.ShowcaseEntries.Add(entry);

        await cloud.SaveChangesAsync(ct);

        return Results.Json(View(entry), statusCode: StatusCodes.Status201Created);
    }

    internal static async Task<IResult> SaveAsync(
        [FromRoute] Guid id,
        [FromBody] ShowcaseUpdate? request,
        [FromServices] CloudContext cloud,
        CancellationToken ct)
    {
        if (Problem(request) is { } problem)
            return problem;

        var entry = await cloud.ShowcaseEntries.SingleOrDefaultAsync(e => e.Id == id, ct);

        if (entry is null)
            return Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound);

        Apply(entry, request!);
        await cloud.SaveChangesAsync(ct);

        return Results.Ok(View(entry));
    }

    internal static async Task<IResult> RemoveAsync(
        [FromRoute] Guid id,
        [FromServices] CloudContext cloud,
        CancellationToken ct)
    {
        var removed = await cloud.ShowcaseEntries.Where(e => e.Id == id).ExecuteDeleteAsync(ct);

        return removed == 0
            ? Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound)
            : Results.NoContent();
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

    private static AdminShowcaseView View(ShowcaseEntry e) => new(
        e.Id, e.Kind, e.Name, e.Link, e.ImageUrl, e.VRChatGroupId, e.GroupImageUrl, e.GroupBannerUrl,
        e.SortOrder, e.AddedAt);
}
