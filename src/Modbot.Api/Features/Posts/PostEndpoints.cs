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
using Modbot.Core.Bluesky;
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
    public const string ChangedTryAgain = "This post changed. Try again.";

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
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                return Results.Ok(new PostList(
                    name,
                    await PostRequests.ViewsAsync(db, posts, sites, ct),
                    counts,
                    number,
                    PageSize,
                    total,
                    ModbotAuth.Allows(ModbotAuth.PermissionsOf(http.User), ModbotPermissions.ManagePosts),
                    PostRequests.SitesView(sites, settings?.VRChatPictureUploads ?? false),
                    clock.UtcNow,
                    PostRequests.VRChatRoles(settings)));
            })
            .RequiresFlag(ModbotPermissions.ViewPosts)
            .WithName("ListPosts")
            .WithSummary("List posts")
            .WithDescription(
                "One page of one list on the Marketing tab, with how many posts each list holds. "
                + "`list` is `scheduled` (soonest first), `sent` and `drafts` (newest first), `failed`, "
                + "or `cancelled`. A post that went to one site and failed on another is in `failed` "
                + "and in whatever else it is. Each destination's `shown` says what the list shows: "
                + "its state, or for one waiting on a site that sends nothing now, `paused`, `off` or `notSetUp`. "
                + "`vrChatRoles` are the VRChat group's roles as the last group read found them, for choosing "
                + "who a VRChat post is for.")
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
                + "`timeZone`. Each site is ticked by sending its section (`discord`, `vrChat`, "
                + "`bluesky`); none is ticked unless sent. VRChat needs a title, the post's or its own. "
                + "Bluesky gets the title and text, or its own text, of at most 300 characters and 3000 "
                + "bytes. Nothing is sent by this request: Modbot's Discord loop sends a post within "
                + "about twenty seconds of its time, its VRChat loop within about fifteen and its Bluesky "
                + "loop within about thirty, unless Pause all posting is on or that site's posts are off. "
                + "A refusal (400) lists everything wrong at once, in `problems`.")
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
                    && destination.Network == PostNetworks.Discord
                    && PostTexts.DiscordOptionsOf(destination).Publish
                    && destination.PublishedAt is null
                    && destination.ExternalId is { } messageId)
                {
                    var published = await discord.PublishAsync(destination.Target, messageId, ct);
                    if (published.BotOffline)
                        return Unavailable(Offline);

                    var written = await WriteAfterSiteAsync(db, post.Id, destination.Id, messageId, (_, d) =>
                    {
                        d.PublishedAt = published.Done ? now : null;
                        d.Error = published.Done ? null : Trim(published.Error);
                        d.ErrorAt = published.Done ? null : now;
                        d.UpdatedAt = now;
                    }, ct);

                    if (!published.Done)
                        return Results.Json(new { error = published.Error }, statusCode: StatusCodes.Status502BadGateway);

                    return written is null ? Conflict(ChangedTryAgain) : Results.Ok(await ViewAsync(db, written, discordBot, ct));
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
                [FromServices] IVRChatPostActions vrchat,
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

                if (destination.Network is not (PostNetworks.Discord or PostNetworks.VRChat))
                    return Conflict("This site's posts cannot be edited.");

                var onVRChat = destination.Network == PostNetworks.VRChat;
                var title = PostTexts.TidyTitle(body.Title);
                var text = PostTexts.Tidy(body.Text);
                var problems = new List<string>();

                if (text.Length == 0)
                    problems.Add("Write some text.");

                if (title is { Length: > Post.MaxTitleLength })
                    problems.Add($"The title is longer than {Post.MaxTitleLength} characters.");

                if (onVRChat && title is null)
                    problems.Add("VRChat needs a title.");

                // What the site is sent: the whole Discord message, or VRChat's text under its title.
                var content = onVRChat ? text : PostTexts.Discord(title, text, PostTexts.DiscordOptionsOf(destination).RoleId);
                if (!onVRChat && !PostTexts.DiscordFits(content))
                    problems.Add($"The Discord text is longer than {PostTexts.DiscordLimit} characters.");

                if (problems.Count > 0)
                    return Refused(problems);

                PostSiteOutcome outcome;

                if (onVRChat)
                {
                    // VRChat replaces the whole post: who sees it, the roles and the picture it went
                    // with are sent again, and nobody is notified again.
                    var options = PostTexts.VRChatOptionsOf(destination);
                    outcome = await vrchat.EditAsync(
                        destination.Target, messageId, title!, text, options.Visibility, PostTexts.VRChatRoles(options), options.ImageId, ct);

                    if (outcome.BotOffline)
                        return Unavailable(outcome.Error ?? NoVRChatPostActions.NotSetUp);
                }
                else
                {
                    outcome = await discord.EditAsync(destination.Target, messageId, content, ct);

                    if (outcome.BotOffline)
                        return Unavailable(Offline);
                }

                var now = clock.UtcNow;

                if (!outcome.Done)
                {
                    if (outcome.Gone)
                        await GoneAsync(db, facts, http, post, destination, messageId, now, ct);

                    var gone = onVRChat ? "That post is gone from VRChat." : "That post is gone from Discord.";

                    return Results.Json(
                        new { error = outcome.Gone ? gone : outcome.Error },
                        statusCode: outcome.Gone ? StatusCodes.Status409Conflict : StatusCodes.Status502BadGateway);
                }

                var was = destination.SentText;

                // The site has the new words now, so the row and the fact follow whatever else
                // changed the post meanwhile (WriteAfterSiteAsync).
                var written = await WriteAfterSiteAsync(db, post.Id, destination.Id, messageId, (_, d) =>
                {
                    d.TitleOverride = title;
                    d.TextOverride = text;
                    d.SentTitle = title;
                    d.SentText = content;
                    d.UpdatedAt = now;
                }, ct);

                await facts.RecordAsync(
                    FactType.PostEdited,
                    post.Id.ToString(),
                    Actor.Of(http),
                    new JsonObject { ["title"] = post.Title, ["network"] = destination.Network, ["before"] = was, ["after"] = content },
                    ct);

                return written is null ? Conflict(ChangedTryAgain) : Results.Ok(await ViewAsync(db, written, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("EditPostOnSite")
            .WithSummary("Edit post on site")
            .WithDescription(
                "Changes the title and text of a post that went out, on that site, at once. Discord "
                + "keeps the picture and pings nobody again. VRChat is sent the whole post again, with "
                + "the picture it went with and who sees it, and notifies nobody again; it needs a "
                + "title. One call to the site, never sent again.")
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
                [FromServices] IVRChatPostActions vrchat,
                [FromServices] IBlueskyPostActions bluesky,
                [FromServices] IDiscordBotStatus? discordBot,
                CancellationToken ct) =>
            {
                var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == id, ct);
                var destination = post?.Destinations.FirstOrDefault(d => d.Id == destinationId);
                if (post is null || destination is null)
                    return Results.NotFound(new { error = NotFound });

                if (destination.State != PostDestinationStates.Posted || destination.ExternalId is not { } messageId)
                    return Conflict("Only a post that went out can be deleted there.");

                var outcome = destination.Network switch
                {
                    PostNetworks.VRChat => await vrchat.DeleteAsync(destination.Target, messageId, ct),
                    PostNetworks.Bluesky => await bluesky.DeleteAsync(destination.Target, BlueskyKeyOf(destination), ct),
                    _ => await discord.DeleteAsync(destination.Target, messageId, "Post deleted from Modbot", ct),
                };

                if (outcome.BotOffline)
                {
                    return Unavailable(destination.Network switch
                    {
                        PostNetworks.VRChat => outcome.Error ?? NoVRChatPostActions.NotSetUp,
                        PostNetworks.Bluesky => outcome.Error ?? BlueskyPostActions.NotSetUp,
                        _ => Offline,
                    });
                }

                if (!outcome.Done)
                    return Results.Json(new { error = outcome.Error }, statusCode: StatusCodes.Status502BadGateway);

                var written = await GoneAsync(db, facts, http, post, destination, messageId, clock.UtcNow, ct);

                return written is null ? Conflict(ChangedTryAgain) : Results.Ok(await ViewAsync(db, written, discordBot, ct));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("DeletePostOnSite")
            .WithSummary("Delete post on site")
            .WithDescription(
                "Deletes a post that went out from that site, at once, and marks it deleted. One that "
                + "is already gone there counts as deleted (VRChat's 404 too). One call to the site, "
                + "never sent again. A Bluesky post is deleted from the account in Settings; deleting "
                + "removes it from Bluesky, not from copies others already took.")
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

                var vrchatPreview = request.VRChat is { } vrchatSection
                    ? new VRChatPostPreview(
                        vrchatSection.Title,
                        vrchatSection.Text,
                        vrchatSection.Options.Visibility,
                        vrchatSection.RoleNames,
                        vrchatSection.Options.Notify,
                        vrchatSection.PictureUrl,
                        PostTexts.VRChatLength(vrchatSection.Text))
                    : null;

                BlueskyPostPreview? blueskyPreview = null;

                if (request.Bluesky is { } blueskySection)
                {
                    var account = await db.Settings.AsNoTracking()
                        .Where(s => s.Id == 1)
                        .Select(s => new { s.BlueskyHandle, s.BlueskyDisplayName })
                        .FirstOrDefaultAsync(ct);

                    // The card picture is sent only with a card, as the sender does.
                    var card = blueskySection.Card is { } built
                        ? new BlueskyCardPreview(
                            built.Uri,
                            built.Title,
                            built.Description,
                            BlueskyText.CardHost(built.Uri),
                            blueskySection.CardPictureId is { } copy ? PicturePath + copy : null)
                        : null;

                    blueskyPreview = new BlueskyPostPreview(
                        blueskySection.Text,
                        [.. BlueskyText.Parts(blueskySection.Text).Select(p => new BlueskyTextPartView(p.Kind, p.Text))],
                        BlueskyText.Graphemes(blueskySection.Text),
                        BlueskyText.Bytes(blueskySection.Text),
                        BlueskyText.GraphemeLimit,
                        card,
                        account?.BlueskyHandle,
                        account?.BlueskyDisplayName);
                }

                return Results.Ok(new PostPreview(discord, problems, vrchatPreview, blueskyPreview));
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

        // The post's picture sent on to VRChat, for a VRChat group post (posts design §3.6,
        // decision 14). Its own request, like the calendar's: the upload waits on its own
        // one-a-minute budget and can be refused, and saving a post never waits on VRChat. Asked
        // when the person ticks VRChat with a picture, only while VRChat picture uploads are on.
        group.MapPost("/vrchat-picture", async (
                HttpContext http,
                [FromBody] PostVRChatPictureRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                // Optional: the VRChat services are wired by the host, not by the API.
                [FromServices] VRChat.Files.VRChatPictureUploads? uploads,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                // The operator's switch (Settings, Modbot's VRChat login). Asked before anything is
                // read or sent to VRChat.
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings is not { VRChatPictureUploads: true })
                    return Conflict("Picture uploads are off.");

                if (uploads is null)
                    return Unavailable("This build of Modbot cannot upload pictures to VRChat.");

                if (body.PictureId is not { } pictureId)
                    return Results.BadRequest(new { error = "Choose a picture first." });

                var bytes = await db.CalendarCoverPictures.AsNoTracking()
                    .Where(c => c.Id == pictureId)
                    .Select(c => c.Bytes)
                    .FirstOrDefaultAsync(ct);

                if (bytes is null)
                    return Results.BadRequest(new { error = "That picture is gone. Choose it again." });

                // VRChat takes a PNG or a JPEG; a post's picture may be a GIF or WebP for Discord.
                if (VRChat.Files.VRChatPictureUploads.Problem(bytes) is { } problem)
                    return Results.BadRequest(new { error = problem });

                var answer = await uploads.UploadAsync(bytes, ct);

                if (!answer.Success)
                {
                    // Never sent again from here (foundation §4.3.1). Ticking VRChat again is the
                    // person's decision.
                    var said = answer.IsRateLimited || answer.Kind == VRChat.VRChatFailureKind.RateLimited
                        ? "VRChat is not taking uploads right now. Try again in a few minutes."
                        : GroupPage.GroupPageAnswers.Said(answer);

                    return Results.Json(new { error = said }, statusCode: GroupPage.GroupPageAnswers.StatusFor(answer));
                }

                if (answer.Value?.Id is not { Length: > 0 } fileId)
                    return Results.Json(new { error = "VRChat did not give the picture an id." }, statusCode: StatusCodes.Status502BadGateway);

                await facts.RecordAsync(
                    FactType.PostPictureUploaded,
                    pictureId.ToString(),
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["fileId"] = fileId,
                        ["pictureId"] = pictureId.ToString(),
                        ["bytes"] = bytes.Length,
                        ["type"] = VRChat.Files.VRChatPictureUploads.TypeOf(bytes),
                    },
                    ct);

                return Results.Ok(new PostVRChatPictureView(fileId, pictureId));
            })
            .RequiresFlag(ModbotPermissions.ManagePosts)
            .WithName("UploadPostVRChatPicture")
            .WithSummary("Upload post picture to VRChat")
            .WithDescription(
                "Uploads a post's picture, kept with `POST /api/posts/picture`, to VRChat, on the VRChat "
                + "account Modbot signs in as, for a VRChat group post. Answers with the file id VRChat "
                + "gave it; send that as the VRChat section's `imageId`. Only a PNG or JPEG of at most "
                + "10 MB. One request to VRChat, at most one a minute and never retried. Answers 409 "
                + "\"Picture uploads are off.\" until the operator turns VRChat picture uploads on in "
                + "Settings; while they are off a VRChat post goes as text only.")
            .Produces<PostVRChatPictureView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

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

    internal static IQueryable<Post> Ordered(IQueryable<Post> posts, string list) => list switch
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
                x.Destination.ErrorAt ?? x.Destination.SentAt,
                x.Destination.State == PostDestinationStates.Failed ? x.Destination.MissingPermission : null))
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

    /// <summary>
    /// A post that is not on its site any more: deleted, here or there. The row is marked removed
    /// and the fact written, whatever else changed the post meanwhile. Null when the row could not
    /// be written; the fact is written all the same.
    /// </summary>
    private static async Task<Post?> GoneAsync(
        ModbotContext db,
        AccountFacts facts,
        HttpContext http,
        Post post,
        PostDestination destination,
        string messageId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var title = post.Title;
        var network = destination.Network;
        var link = destination.Link;

        var written = await WriteAfterSiteAsync(db, post.Id, destination.Id, messageId, (_, d) =>
        {
            d.State = PostDestinationStates.Removed;
            d.UpdatedAt = now;
        }, ct);

        await facts.RecordAsync(
            FactType.PostRemoved,
            post.Id.ToString(),
            Actor.Of(http),
            new JsonObject
            {
                ["title"] = title,
                ["network"] = network,
                ["by"] = Actor.Of(http)?.Username,
                ["link"] = link,
            },
            ct);

        return written;
    }

    /// <summary>How many times a row write after a site call is read again and tried.</summary>
    private const int WriteAfterSiteTries = 3;

    /// <summary>
    /// Writes what a call to a site already did onto its destination row. The site has done it, so
    /// this must not be lost to a concurrent change: on a version conflict the post is read again
    /// and the change made again, as long as the destination is still the same message on the site
    /// (still posted, with that id). Safe to make again: the sender never touches a posted row, and
    /// the change only records the site's own answer. Null when the row has become something else,
    /// or the conflicts did not stop; the caller says so (409) and the person tries again.
    /// </summary>
    private static async Task<Post?> WriteAfterSiteAsync(
        ModbotContext db,
        Guid postId,
        Guid destinationId,
        string messageId,
        Action<Post, PostDestination> change,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < WriteAfterSiteTries; attempt++)
        {
            db.ChangeTracker.Clear();

            var post = await db.Posts.Include(p => p.Destinations).FirstOrDefaultAsync(p => p.Id == postId, ct);
            var destination = post?.Destinations.FirstOrDefault(d => d.Id == destinationId);

            if (post is null
                || destination is null
                || destination.State != PostDestinationStates.Posted
                || !string.Equals(destination.ExternalId, messageId, StringComparison.Ordinal))
            {
                return null;
            }

            change(post, destination);
            post.Version++;

            try
            {
                await db.SaveChangesAsync(ct);
                return post;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Somebody wrote the post since it was read: read it again and make the change again.
            }
        }

        db.ChangeTracker.Clear();
        return null;
    }

    /// <summary>
    /// A Bluesky post's record key: the one Modbot made, or the last part of its <c>at://</c> address
    /// for a row that somehow lacks it.
    /// </summary>
    private static string BlueskyKeyOf(PostDestination destination) =>
        destination.ClientKey is { Length: > 0 } key
            ? key
            : destination.ExternalId?.Split('/').LastOrDefault() ?? string.Empty;

    private static IResult Refused(IReadOnlyList<string> problems) =>
        Results.BadRequest(new { error = string.Join(" ", problems), problems });

    private static IResult Conflict(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict);

    private static IResult Unavailable(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static string? Trim(string? error) =>
        error is null ? null : error.Length <= 1024 ? error : error[..1024];
}
