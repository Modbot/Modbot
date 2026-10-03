using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Calendar;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Posts;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Posts;

/// <summary>
/// The Marketing tab's posts (posts design §4.7): the lists, writing and scheduling, Post now,
/// Cancel, Try again, and edit or delete on a site once a post has gone out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This stores what a person decides; the senders send.</strong> Scheduling, Post now and
/// Try again only write rows, and each site's loop picks them up (the Discord one every twenty
/// seconds), so a request never waits on a site and nothing is sent twice by a request racing a
/// loop. Edit and delete on a site are the exception: they are one call each, made at once, so the
/// person sees whether it worked.
/// </para>
/// <para>
/// <strong>Every write raises the post's version</strong>, and a change says which version it was
/// read at, so a change saved while the post is being sent fails instead of changing words already
/// on their way (§3.4). Every change is a fact, saved in the same transaction as the change.
/// </para>
/// </remarks>
public static class PostEndpoints
{
    public const int PageSize = 25;

    /// <summary>Where a post's picture is read from: <c>GET /api/posts/pictures/{id}</c>.</summary>
    public const string PicturePath = "/api/posts/pictures/";

    public const string NotFound = "That post does not exist.";
    public const string ChangedElsewhere = "Someone else changed this post. Open it again.";
    public const string Offline = "The Discord bot is not connected.";

    public static IEndpointRouteBuilder MapPosts(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/posts").WithTags("Posts");

        group.MapGet("/", async (
                HttpContext http,
                [FromQuery] string? list,
                [FromQuery] int? page,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                // Optional: the bot is wired by the host, not by the API. Without it Discord is not set up.
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                var name = string.IsNullOrWhiteSpace(list) ? PostLists.Scheduled : list.Trim().ToLowerInvariant();
                if (!PostLists.All.Contains(name))
                    return Results.BadRequest(new { error = "The list must be scheduled, sent, drafts, failed or cancelled." });

                var number = Math.Max(1, page ?? 1);
                var query = InList(db.Posts.AsNoTracking(), name);
                var total = await query.CountAsync(ct);

                var posts = await Ordered(query, name)
                    .Include(p => p.Destinations)
                    .AsSplitQuery()
                    .Skip((number - 1) * PageSize)
                    .Take(PageSize)
                    .ToListAsync(ct);

                var counts = new PostCounts(
                    await InList(db.Posts, PostLists.Scheduled).CountAsync(ct),
                    await InList(db.Posts, PostLists.Sent).CountAsync(ct),
                    await InList(db.Posts, PostLists.Drafts).CountAsync(ct),
                    await InList(db.Posts, PostLists.Failed).CountAsync(ct),
                    await InList(db.Posts, PostLists.Cancelled).CountAsync(ct));

                var sites = await PostRequests.SitesAsync(db, discordBot, ct);

                return Results.Ok(new PostList(
                    name,
                    await PostRequests.ViewsAsync(db, posts, sites, ct),
                    counts,
                    number,
                    PageSize,
                    total,
                    ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManagePosts),
                    PostRequests.SitesView(sites),
                    clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewPosts)
            .WithName("ListPosts")
            .WithSummary("List posts")
            .WithDescription(
                "One page of one list on the Marketing tab, with how many posts each list holds. "
                + "`list` is `scheduled` (soonest first), `sent` and `drafts` (newest first), `failed`, "
                + "or `cancelled`. A post that went to one site and failed on another is in `failed` "
                + "and in whatever else it is. Each destination's `shown` says what the list shows: "
                + "its state, or for one waiting on a site that sends nothing now, `paused`, `off` or `notSetUp`.")
            .Produces<PostList>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/health", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) => Results.Ok(await HealthAsync(db, clock.UtcNow, discordBot, ct)))
            .RequiresFlag(ModbotPermissions.ViewPosts)
            .WithName("GetPostsHealth")
            .WithSummary("Get posts health")
            .WithDescription(
                "What the Health page's Posts card shows: whether posting is paused, destinations that "
                + "failed in the last 7 days, posts Modbot has been looking for on a site for more than "
                + "15 minutes, and sites scheduled posts wait on that are switched off or not set up.")
            .Produces<PostsHealth>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", async (
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                var post = await db.Posts.AsNoTracking().Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct);
                if (post is null)
                    return Results.NotFound(new { error = NotFound });

                return Results.Ok(await ViewAsync(db, post, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ViewPosts)
            .WithName("GetPost")
            .WithSummary("Get post")
            .WithDescription("One post, with each site it goes to and how it went there.")
            .Produces<PostView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
                HttpContext http,
                [FromBody] PostRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var now = clock.UtcNow;
                var (request, problems) = await PostRequests.CheckAsync(db, body, now, keptPicture: null, ct);
                if (problems.Count > 0)
                    return Refused(problems);

                var post = new Post
                {
                    Id = Guid.CreateVersion7(),
                    CreatedAt = now,
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                };

                PostRequests.Apply(post, request, body.Draft, now);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Posts.Add(post);
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(FactType.PostCreated, post.Id.ToString(), Actor.Of(http), PostRequests.Describe(post), ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, post, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("CreatePost")
            .WithSummary("Write post")
            .WithDescription(
                "Saves a draft, or schedules a post: `when` is `now` or `later` with `sendAt` in "
                + "`timeZone`. Each site is ticked by sending its section (`discord`); none is ticked "
                + "unless sent. Nothing is sent by this request: Modbot's Discord loop sends a post "
                + "within about twenty seconds of its time, unless Pause all posting is on or Discord "
                + "posts are off. A refusal (400) lists everything wrong at once, in `problems`.")
            .Produces<PostView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromBody] PostRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct);
                if (post is null)
                    return Results.NotFound(new { error = NotFound });

                if (body.Version is { } version && version != post.Version)
                    return Conflict(post.Destinations.Any(d => PostDestinationStates.IsUnderWay(d.State)) ? PostRules.BeingSent : ChangedElsewhere);

                if (PostRules.CannotEdit(post) is { } cannot)
                    return Conflict(cannot);

                var now = clock.UtcNow;
                var (request, problems) = await PostRequests.CheckAsync(db, body, now, post.PictureId, ct);
                if (problems.Count > 0)
                    return Refused(problems);

                var before = PostRequests.Describe(post);
                PostRequests.Apply(post, request, body.Draft, now);
                post.Version++;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (await SaveAsync(db, post.Id, ct) is { } conflict)
                    return conflict;

                var changed = PostRequests.Changed(before, PostRequests.Describe(post));
                if (changed.Count > 0)
                {
                    await facts.RecordAsync(
                        FactType.PostChanged, post.Id.ToString(), Actor.Of(http), new JsonObject { ["title"] = post.Title, ["changed"] = changed }, ct);
                }

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, post, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("ChangePost")
            .WithSummary("Change post")
            .WithDescription(
                "Changes the whole of a post before it goes, as `POST /api/posts` takes it, with the "
                + "`version` it was read at. Refused (409) while it is being sent, once it has gone out "
                + "on a site (edit it there instead), and while a site may already have it after an "
                + "unclear answer (Try again settles that first).")
            .Produces<PostView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/send-now", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct);
                if (post is null)
                    return Results.NotFound(new { error = NotFound });

                if (post.Status == PostStatuses.Cancelled)
                    return Conflict("This post was cancelled.");

                if (post.Destinations.Any(d => PostDestinationStates.IsUnderWay(d.State)))
                    return Conflict(PostRules.BeingSent);

                if (PostRequests.NotReadyToSend(post) is { Count: > 0 } problems)
                    return Refused(problems);

                var now = clock.UtcNow;
                var before = PostRequests.Describe(post);
                PostChanges.SendNow(post, now);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (await SaveAsync(db, post.Id, ct) is { } conflict)
                    return conflict;

                await facts.RecordAsync(
                    FactType.PostChanged,
                    post.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["title"] = post.Title, ["postNow"] = true, ["changed"] = PostRequests.Changed(before, PostRequests.Describe(post)) },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, post, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("SendPostNow")
            .WithSummary("Post now")
            .WithDescription(
                "Sends a draft or a scheduled post now, and a destination that failed for being late, "
                + "or that a site refused, goes again. One a site may already have stays failed: Try "
                + "again looks for it first. Nothing is sent by this request; the loop sends it next.")
            .Produces<PostView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/cancel", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct);
                if (post is null)
                    return Results.NotFound(new { error = NotFound });

                if (PostRules.CannotCancel(post) is { } cannot)
                    return Conflict(cannot);

                PostChanges.Cancel(post, clock.UtcNow);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (await SaveAsync(db, post.Id, ct) is { } conflict)
                    return conflict;

                await facts.RecordAsync(FactType.PostCancelled, post.Id.ToString(), Actor.Of(http), new JsonObject { ["title"] = post.Title }, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, post, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("CancelPost")
            .WithSummary("Cancel post")
            .WithDescription(
                "Calls a scheduled post off. Sites it has not gone to are skipped; a site it already "
                + "went to keeps it, to be deleted there by hand. Refused (409) while it is being sent.")
            .Produces<PostView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct);
                if (post is null)
                    return Results.NotFound(new { error = NotFound });

                if (post.Status != PostStatuses.Draft)
                    return Conflict("Only a draft can be deleted.");

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                db.Posts.Remove(post);
                if (await SaveAsync(db, post.Id, ct) is { } conflict)
                    return conflict;

                await facts.RecordAsync(
                    FactType.PostCancelled, post.Id.ToString(), Actor.Of(http), new JsonObject { ["title"] = post.Title, ["deleted"] = true }, ct);
                await transaction.CommitAsync(ct);

                return Results.NoContent();
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("DeletePostDraft")
            .WithSummary("Delete draft")
            .WithDescription("Deletes a draft. A post that was scheduled is cancelled instead, and keeps its record.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/destinations/{destinationId:guid}/try-again", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromRoute] Guid destinationId,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordPostActions discord,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct);
                var destination = post?.Destinations.FirstOrDefault(d => d.Id == destinationId);
                if (post is null || destination is null)
                    return Results.NotFound(new { error = NotFound });

                var now = clock.UtcNow;

                // Posted, and publishing to followers did not go through: that step alone, at once.
                if (destination.State == PostDestinationStates.Posted
                    && PostTexts.DiscordOptionsOf(destination).Publish
                    && destination.PublishedAt is null
                    && destination.ExternalId is { } messageId)
                {
                    var published = await discord.PublishAsync(destination.Target, messageId, ct);
                    if (published.BotOffline)
                        return Unavailable(Offline);

                    destination.PublishedAt = published.Done ? now : null;
                    destination.Error = published.Done ? null : Trim(published.Error);
                    destination.ErrorAt = published.Done ? null : now;
                    destination.UpdatedAt = now;
                    post.Version++;

                    if (await SaveAsync(db, post.Id, ct) is { } publishConflict)
                        return publishConflict;

                    return published.Done
                        ? Results.Ok(await ViewAsync(db, post, discordBot, ct))
                        : Results.Json(new { error = published.Error }, statusCode: StatusCodes.Status502BadGateway);
                }

                if (destination.State != PostDestinationStates.Failed)
                    return Conflict("Only a post that failed can be tried again.");

                if (post.Status != PostStatuses.Scheduled)
                    return Conflict("This post was cancelled.");

                PostChanges.TryAgain(post, destination, now);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (await SaveAsync(db, post.Id, ct) is { } conflict)
                    return conflict;

                await facts.RecordAsync(
                    FactType.PostChanged,
                    post.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["title"] = post.Title, ["tryAgain"] = destination.Network, ["looksFirst"] = destination.SendIfMissing },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, post, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("TryPostAgain")
            .WithSummary("Try again")
            .WithDescription(
                "Sends a failed destination again. When the site may already have the post (it gave "
                + "no clear answer), Modbot looks for it first and sends only if it is not there. For a "
                + "Discord post that went in but was not published to followers, publishes it now.")
            .Produces<PostView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id:guid}/destinations/{destinationId:guid}/edit", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromRoute] Guid destinationId,
                [FromBody] PostEditRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordPostActions discord,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct);
                var destination = post?.Destinations.FirstOrDefault(d => d.Id == destinationId);
                if (post is null || destination is null)
                    return Results.NotFound(new { error = NotFound });

                if (destination.State != PostDestinationStates.Posted || destination.ExternalId is not { } messageId)
                    return Conflict("Only a post that went out can be edited there.");

                if (destination.Network != PostNetworks.Discord)
                    return Conflict("This site's posts cannot be edited.");

                var title = PostTexts.TidyTitle(body.Title);
                var text = PostTexts.Tidy(body.Text);
                var problems = new List<string>();

                if (text.Length == 0)
                    problems.Add("Write some text.");

                if (title is { Length: > Post.MaxTitleLength })
                    problems.Add($"The title is longer than {Post.MaxTitleLength} characters.");

                var content = PostTexts.Discord(title, text, PostTexts.DiscordOptionsOf(destination).RoleId);
                if (!PostTexts.DiscordFits(content))
                    problems.Add($"The Discord text is longer than {PostTexts.DiscordLimit} characters.");

                if (problems.Count > 0)
                    return Refused(problems);

                var outcome = await discord.EditAsync(destination.Target, messageId, content, ct);
                if (outcome.BotOffline)
                    return Unavailable(Offline);

                var now = clock.UtcNow;

                if (!outcome.Done)
                {
                    if (outcome.Gone)
                        await GoneAsync(db, facts, http, post, destination, now, ct);

                    return Results.Json(
                        new { error = outcome.Gone ? "That post is gone from Discord." : outcome.Error },
                        statusCode: outcome.Gone ? StatusCodes.Status409Conflict : StatusCodes.Status502BadGateway);
                }

                var was = destination.SentText;
                destination.TitleOverride = title;
                destination.TextOverride = text;
                destination.SentTitle = title;
                destination.SentText = content;
                destination.UpdatedAt = now;
                post.Version++;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                if (await SaveAsync(db, post.Id, ct) is { } conflict)
                    return conflict;

                await facts.RecordAsync(
                    FactType.PostEdited,
                    post.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["title"] = post.Title, ["network"] = destination.Network, ["before"] = was, ["after"] = content },
                    ct);

                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, post, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("EditPostOnSite")
            .WithSummary("Edit post on site")
            .WithDescription(
                "Changes the title and text of a post that went out, on that site, at once. Discord "
                + "keeps the picture and pings nobody again. One call to Discord.")
            .Produces<PostView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/{id:guid}/destinations/{destinationId:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromRoute] Guid destinationId,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordPostActions discord,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct);
                var destination = post?.Destinations.FirstOrDefault(d => d.Id == destinationId);
                if (post is null || destination is null)
                    return Results.NotFound(new { error = NotFound });

                if (destination.State != PostDestinationStates.Posted || destination.ExternalId is not { } messageId)
                    return Conflict("Only a post that went out can be deleted there.");

                var outcome = await discord.DeleteAsync(destination.Target, messageId, "Post deleted from Modbot", ct);
                if (outcome.BotOffline)
                    return Unavailable(Offline);

                if (!outcome.Done)
                    return Results.Json(new { error = outcome.Error }, statusCode: StatusCodes.Status502BadGateway);

                await GoneAsync(db, facts, http, post, destination, clock.UtcNow, ct);

                return Results.Ok(await ViewAsync(db, post, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("DeletePostOnSite")
            .WithSummary("Delete post on site")
            .WithDescription(
                "Deletes a post that went out from that site, at once, and marks it deleted. One that "
                + "is already gone there counts as deleted. One call to Discord.")
            .Produces<PostView>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/preview", async (
                [FromBody] PostRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var (request, problems) = await PostRequests.CheckAsync(db, body, clock.UtcNow, keptPicture: null, ct);

                var discord = request.Discord is { } section
                    ? new DiscordPostPreview(
                        section.Content,
                        request.Title,
                        section.OwnText ?? request.Text,
                        section.RoleName,
                        section.RoleColour,
                        section.ChannelName,
                        request.PictureId is { } picture ? PicturePath + picture : null,
                        PostTexts.DiscordLength(section.Content),
                        PostTexts.DiscordLimit,
                        section.Options.Publish)
                    : null;

                return Results.Ok(new PostPreview(discord, problems));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("PreviewPost")
            .WithSummary("Preview post")
            .WithDescription(
                "What each ticked site would be sent, built by the code that sends it, and what would "
                + "stop the post being scheduled. Saves nothing and asks no site anything.")
            .Produces<PostPreview>()
            .Produces(StatusCodes.Status403Forbidden);

        // The picture cropped in the composer, kept with the calendar's pictures (posts design §2.2):
        // its own request, so saving stays plain JSON. One no post was saved with is deleted by
        // CalendarCoverSweep once it is a day old.
        group.MapPost("/picture", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (http.Request.ContentLength > CalendarCoverPicture.MaxBytes)
                    return Results.Json(new { error = CalendarEndpoints.CoverTooBig }, statusCode: StatusCodes.Status413PayloadTooLarge);

                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = CalendarCoverPicture.MaxBytes + 1;

                var bytes = await CalendarEndpoints.ReadPictureAsync(http.Request.Body, http.Request.ContentLength, ct);

                if (bytes.Length > CalendarCoverPicture.MaxBytes)
                    return Results.Json(new { error = CalendarEndpoints.CoverTooBig }, statusCode: StatusCodes.Status413PayloadTooLarge);

                var type = Core.Files.PictureFormats.Sniff(bytes);
                if (!Core.Files.PictureFormats.DiscordTakes(type))
                    return Results.BadRequest(new { error = "The picture must be a PNG, JPEG, GIF or WebP." });

                var picture = new CalendarCoverPicture
                {
                    Id = Guid.CreateVersion7(),
                    Bytes = bytes,
                    ContentType = type!,
                    CreatedAt = clock.UtcNow,
                    CreatedByUserId = ModbotAuth.UserIdOf(http.User),
                };

                db.CalendarCoverPictures.Add(picture);
                await db.SaveChangesAsync(ct);

                return Results.Ok(new PostPictureView(picture.Id));
            })
            // No declared request body: the body is a file.
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("UploadPostPicture")
            .WithSummary("Upload post picture")
            .WithDescription(
                "Keeps a picture for a post. The body is the picture itself: a PNG, JPEG, GIF or WebP "
                + "of at most 8 MB, told apart by its first bytes. Answers with its id; save that as the "
                + "post's pictureId. Discord is sent it as a file when the post goes. One no post was "
                + "saved with is deleted once it is a day old.")
            .Produces<PostPictureView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        group.MapGet("/pictures/{id:guid}", async (
                HttpContext http,
                [FromRoute] Guid id,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var picture = await db.CalendarCoverPictures.AsNoTracking()
                    .Where(c => c.Id == id)
                    .Select(c => new { c.Bytes, c.ContentType })
                    .FirstOrDefaultAsync(ct);

                if (picture is null)
                    return Results.NotFound();

                // A picture never changes once kept.
                http.Response.Headers.CacheControl = "private, max-age=604800, immutable";
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                http.Response.Headers.ContentSecurityPolicy = "sandbox";

                return Results.Bytes(picture.Bytes, picture.ContentType);
            })
            .RequiresFlag(ModbotPermissions.ViewPosts)
            .WithName("GetPostPicture")
            .WithSummary("Get post picture")
            .WithDescription("A picture kept for a post.")
            .Produces<byte[]>(StatusCodes.Status200OK, "image/png", "image/jpeg", "image/gif", "image/webp")
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/picture-link", (
                HttpContext http,
                [FromBody] PostPictureLinkRequest body,
                [FromServices] ModbotContext db,
                // Optional: the VRChat side is wired by the host, not by the API.
                [FromServices] Core.Files.IPictures? vrchatPictures,
                CancellationToken ct) => PictureLinkFetch.AnswerAsync(http, body.Url, db, vrchatPictures, ct))
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("FetchPostPictureLink")
            .WithSummary("Fetch picture link for a post")
            .WithDescription(
                "Fetches the picture behind a link and returns its bytes, for the composer to crop in "
                + "the browser. Only https addresses on the public internet, at most 10 MB, and only a "
                + "picture by its bytes. A VRChat file link is fetched with Modbot's VRChat session, "
                + "only while VRChat pictures are fetched through this server. Nothing is kept.")
            .Produces<byte[]>(StatusCodes.Status200OK, "image/png", "image/jpeg", "image/gif", "image/webp")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway);

        return app;
    }

    // ── Lists ────────────────────────────────────────────────────────────────────────────

    /// <summary>The posts in one list (posts design §2.3), the same rule as <see cref="PostRules.ListsOf"/>.</summary>
    internal static IQueryable<Post> InList(IQueryable<Post> posts, string list) => list switch
    {
        PostLists.Drafts => posts.Where(p => p.Status == PostStatuses.Draft),
        PostLists.Cancelled => posts.Where(p => p.Status == PostStatuses.Cancelled),
        PostLists.Failed => posts.Where(p => p.Status == PostStatuses.Scheduled
            && p.Destinations.Any(d => d.State == PostDestinationStates.Failed)),
        PostLists.Sent => posts.Where(p => p.Status == PostStatuses.Scheduled
            && p.Destinations.Any()
            && p.Destinations.All(d => d.State == PostDestinationStates.Posted
                || d.State == PostDestinationStates.Removed
                || d.State == PostDestinationStates.Skipped)
            && p.Destinations.Any(d => d.State == PostDestinationStates.Posted || d.State == PostDestinationStates.Removed)),
        _ => posts.Where(p => p.Status == PostStatuses.Scheduled
            && p.Destinations.Any(d => d.State == PostDestinationStates.Waiting
                || d.State == PostDestinationStates.Sending
                || d.State == PostDestinationStates.Checking)),
    };

    private static IQueryable<Post> Ordered(IQueryable<Post> posts, string list) => list switch
    {
        PostLists.Scheduled => posts.OrderBy(p => p.SendAt).ThenBy(p => p.CreatedAt),
        PostLists.Sent => posts.OrderByDescending(p => p.SendAt).ThenByDescending(p => p.CreatedAt),
        PostLists.Cancelled => posts.OrderByDescending(p => p.CancelledAt).ThenByDescending(p => p.CreatedAt),
        _ => posts.OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.CreatedAt),
    };

    // ── Health ───────────────────────────────────────────────────────────────────────────

    internal static async Task<PostsHealth> HealthAsync(
        ModbotContext db, DateTimeOffset now, IDiscordBotStatus? discordBot, CancellationToken ct)
    {
        var sites = await PostRequests.SitesAsync(db, discordBot, ct);
        var weekAgo = now - TimeSpan.FromDays(7);
        var longAgo = now - PostRules.LongCheckAfter;

        var rows = await db.PostDestinations.AsNoTracking()
            .Join(db.Posts.AsNoTracking(), d => d.PostId, p => p.Id, (d, p) => new { Destination = d, p.Title, p.Text, p.Status })
            .Where(x => (x.Destination.State == PostDestinationStates.Failed && x.Destination.ErrorAt >= weekAgo)
                || (x.Destination.State == PostDestinationStates.Checking && x.Destination.SentAt < longAgo))
            .OrderByDescending(x => x.Destination.ErrorAt ?? x.Destination.SentAt)
            .Take(50)
            .ToListAsync(ct);

        var problems = rows
            .Select(x => new PostHealthProblem(
                x.Destination.PostId,
                Headline(x.Title, x.Text),
                x.Destination.Network,
                x.Destination.State == PostDestinationStates.Failed ? PostDestinationStates.Failed : PostDestinationStates.Checking,
                x.Destination.Error,
                x.Destination.ErrorAt ?? x.Destination.SentAt))
            .ToList();

        var waiting = await db.PostDestinations.AsNoTracking()
            .Join(db.Posts.AsNoTracking(), d => d.PostId, p => p.Id, (d, p) => new { d.Network, d.State, p.Status })
            .Where(x => x.State == PostDestinationStates.Waiting && x.Status == PostStatuses.Scheduled)
            .GroupBy(x => x.Network)
            .Select(g => new { Network = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Paused is said once, on its own; a site switched off or not set up is said per site.
        var holds = sites.Paused
            ? []
            : waiting
                .Select(w => (w.Network, w.Count, Hold: sites.HoldFor(w.Network)))
                .Where(w => w.Hold is not null)
                .Select(w => new PostHealthHold(w.Network, w.Hold!, w.Count))
                .ToList();

        return new PostsHealth(sites.Paused, problems, holds);
    }

    /// <summary>The title, or the first line of the text, the way the list names a post.</summary>
    internal static string Headline(string? title, string text)
    {
        if (!string.IsNullOrWhiteSpace(title))
            return title.Trim();

        var first = text.Split('\n', 2)[0].Trim();
        return first.Length <= 80 ? first : first[..80] + "…";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static async Task<PostView> ViewAsync(ModbotContext db, Post post, IDiscordBotStatus? discordBot, CancellationToken ct)
    {
        var sites = await PostRequests.SitesAsync(db, discordBot, ct);
        return (await PostRequests.ViewsAsync(db, [post], sites, ct))[0];
    }

    /// <summary>
    /// Saves a change to a post. When the post was written since it was read -- most often the
    /// sender claiming it -- nothing is saved and the answer says why.
    /// </summary>
    private static async Task<IResult?> SaveAsync(ModbotContext db, Guid postId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();

            var underWay = await db.PostDestinations.AsNoTracking()
                .AnyAsync(d => d.PostId == postId
                    && (d.State == PostDestinationStates.Sending || d.State == PostDestinationStates.Checking), ct);

            return Conflict(underWay ? PostRules.BeingSent : ChangedElsewhere);
        }
    }

    /// <summary>A post that is not on its site any more: deleted, here or there.</summary>
    private static async Task GoneAsync(
        ModbotContext db, AccountFacts facts, HttpContext http, Post post, PostDestination destination, DateTimeOffset now, CancellationToken ct)
    {
        destination.State = PostDestinationStates.Removed;
        destination.UpdatedAt = now;
        post.Version++;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (await SaveAsync(db, post.Id, ct) is not null)
            return;

        await facts.RecordAsync(
            FactType.PostRemoved,
            post.Id.ToString(),
            Actor.Of(http),
            new JsonObject
            {
                ["title"] = post.Title,
                ["network"] = destination.Network,
                ["by"] = Actor.Of(http)?.Username,
                ["link"] = destination.Link,
            },
            ct);

        await transaction.CommitAsync(ct);
    }

    private static IResult Refused(IReadOnlyList<string> problems) =>
        Results.BadRequest(new { error = string.Join(" ", problems), problems });

    private static IResult Conflict(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict);

    private static IResult Unavailable(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static string? Trim(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];
}
