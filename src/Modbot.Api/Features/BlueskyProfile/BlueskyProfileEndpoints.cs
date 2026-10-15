using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Posts;
using Modbot.Api.Features.Settings;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Files;
using Modbot.Core.Security;

namespace Modbot.Api.Features.BlueskyProfile;

/// <summary>The group's Bluesky profile page: reading the profile, changing it, and its Posts tab.</summary>
public static class BlueskyProfileEndpoints
{
    /// <summary>How many posts a page of the Posts tab holds.</summary>
    public const int PostsPageSize = 20;

    public const string NothingToSave = "Nothing to save.";

    // Two pictures and the form's own overhead.
    private const long MaxRequestBytes = 2L * BlueskyProfileRecord.PictureLimit + 100 * 1024;

    public static IEndpointRouteBuilder MapBlueskyProfile(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/bluesky/profile").WithTags("Bluesky");

        group.MapGet("", async (
                HttpContext http,
                [FromServices] BlueskyProfileService service,
                CancellationToken ct) =>
                Results.Ok(await service.ReadAsync(CanEdit(http), ct)))
            .RequiresFlag(ModbotPermissions.ViewAnalytics)
            .WithName("GetBlueskyProfile")
            .WithSummary("Get the Bluesky profile")
            .WithDescription(
                "The group's Bluesky account as its profile page shows it: name, handle, bio, picture and "
                + "banner addresses and the followers, following and posts counts, read from Bluesky's public "
                + "API (no sign-in). The answer is kept for 10 seconds. State is notSetUp when no account is "
                + "saved and unavailable when it could not be read (Problem says why). CanEdit is true "
                + "for a caller who may change the Bluesky settings; SaveError is why the last save failed.")
            .Produces<BlueskyProfileView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromServices] BlueskyProfileService service,
                CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType)
                    return Results.BadRequest(new { error = NothingToSave });

                if (http.Request.ContentLength > MaxRequestBytes)
                    return TooBig();

                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = MaxRequestBytes;

                IFormCollection form;

                try
                {
                    form = await http.Request.ReadFormAsync(ct);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return TooBig();
                }

                string? name = form.TryGetValue("displayName", out var nameValue) ? nameValue.ToString() : null;
                string? bio = form.TryGetValue("bio", out var bioValue) ? bioValue.ToString() : null;
                var avatarFile = form.Files.GetFile("avatar");
                var bannerFile = form.Files.GetFile("banner");

                if (name is null && bio is null && avatarFile is null && bannerFile is null)
                    return Results.BadRequest(new { error = NothingToSave });

                if (BlueskyProfileRecord.ProblemWithName(name) is { } nameProblem)
                    return Results.BadRequest(new { error = nameProblem });

                if (BlueskyProfileRecord.ProblemWithBio(bio) is { } bioProblem)
                    return Results.BadRequest(new { error = bioProblem });

                if (avatarFile is { Length: > BlueskyProfileRecord.PictureLimit } || bannerFile is { Length: > BlueskyProfileRecord.PictureLimit })
                    return TooBig();

                var avatar = await PictureOfAsync(avatarFile, ct);
                var banner = await PictureOfAsync(bannerFile, ct);

                if ((avatarFile is not null && avatar is null) || (bannerFile is not null && banner is null))
                    return Results.BadRequest(new { error = BlueskyProfileRecord.PictureNotAccepted });

                var save = await service.SaveAsync(name, bio, avatar, banner, ct);

                if (!save.Done)
                    return Results.Json(new { error = save.Error }, statusCode: save.Status);

                var change = new SettingsChange("bluesky")
                    .Field("profileName", Text(save.Before, "displayName"), Text(save.After, "displayName"))
                    .Field("profileBio", Text(save.Before, "description"), Text(save.After, "description"))
                    .Field("profilePicture", Cid(save.Before, "avatar"), Cid(save.After, "avatar"))
                    .Field("profileBanner", Cid(save.Before, "banner"), Cid(save.After, "banner"));

                await change.RecordAsync(http, ct);

                return Results.Ok(await service.ReadAsync(true, ct));
            })
            .RequiresFlag(ModbotPermissions.ManageSettings)
            .Accepts<IFormFile>("multipart/form-data")
            .WithName("SetBlueskyProfile")
            .WithSummary("Change the Bluesky profile")
            .WithDescription(
                "Changes the account's name, bio, picture and banner on Bluesky, and nothing else on the "
                + "profile (the pin and every other field stay as they are). Needs Manage settings. The body is a "
                + "multipart form: text fields displayName (at most 64 characters) and bio (at most 256) and files avatar "
                + "and banner (a PNG or JPEG under 1 MB each). A field that is not sent is kept; an empty text field "
                + "clears it. Answers with the profile as saved. 400 for nothing to save, a value over its limit, or an account "
                + "that is not set up; 413 for a picture over 1 MB; 429 when Bluesky is limiting Modbot or a save came "
                + "within 10 seconds of the last one (a 429 is never retried); 409 when the profile was changed on "
                + "Bluesky meanwhile; 502 for any other failure. A failed save shows on Health and in SaveError.")
            .Produces<BlueskyProfileView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway);

        group.MapGet("/posts", async (
                [FromServices] ModbotContext db,
                [FromQuery] int? page,
                CancellationToken ct) =>
            {
                var number = Math.Max(1, page ?? 1);
                var did = await db.Settings.AsNoTracking()
                    .Where(s => s.Id == 1)
                    .Select(s => s.BlueskyDid)
                    .FirstOrDefaultAsync(ct);

                if (string.IsNullOrEmpty(did))
                    return Results.Ok(new BlueskyProfilePosts([], number, PostsPageSize, 0));

                var sent = db.PostDestinations.AsNoTracking()
                    .Where(d => d.Network == PostNetworks.Bluesky && d.State == PostDestinationStates.Posted && d.Target == did);

                var total = await sent.CountAsync(ct);
                var rows = await sent
                    .OrderByDescending(d => d.PostedAt)
                    .ThenByDescending(d => d.Id)
                    .Skip((number - 1) * PostsPageSize)
                    .Take(PostsPageSize)
                    .ToListAsync(ct);

                var posts = rows.Select(d => new BlueskyProfilePostView(
                    d.PostId,
                    d.SentTitle,
                    d.SentText ?? string.Empty,
                    d.Link,
                    d.PostedAt,
                    d.LikeCount is { } likes && d.CountedAt is { } countedAt
                        ? new BlueskyCountsView(likes, d.RepostCount ?? 0, d.ReplyCount ?? 0, countedAt)
                        : null)).ToList();

                return Results.Ok(new BlueskyProfilePosts(posts, number, PostsPageSize, total));
            })
            .RequiresFlag(ModbotPermissions.ViewPosts)
            .WithName("ListBlueskyProfilePosts")
            .WithSummary("List posts sent to Bluesky")
            .WithDescription(
                "The posts Modbot sent to the group's Bluesky account, newest first, 20 to a page (page "
                + "starts at 1), each with its likes, reposts and replies when last counted.")
            .Produces<BlueskyProfilePosts>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static bool CanEdit(HttpContext http)
        => ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManageSettings);

    private static IResult TooBig()
        => Results.Json(new { error = BlueskyProfileRecord.PictureTooBig }, statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>A picture's bytes and its type, or null for none sent or a type Bluesky's profile does not take.</summary>
    private static async Task<(byte[] Bytes, string ContentType)?> PictureOfAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null)
            return null;

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        var type = PictureFormats.Sniff(bytes);
        return type is not null && BlueskyProfileRecord.PictureTypes.Contains(type) ? (bytes, type) : null;
    }

    private static string Text(JsonObject? record, string field)
        => record?[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static string Cid(JsonObject? record, string field)
        => record?[field]?["ref"]?["$link"] is JsonValue link && link.TryGetValue<string>(out var cid) ? cid : string.Empty;
}
