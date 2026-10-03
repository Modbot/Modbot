using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Api.Auth;
using Modbot.Api.Features.Posts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Api.Features.Settings;

/// <summary>
/// The Settings topic "Posts" (posts design §4.6): Pause all posting, and whether posts go to Discord,
/// to the VRChat group and to Bluesky.
/// </summary>
/// <remarks>
/// Each switch is the operator's say over what leaves the server. Paused, no post goes to any site,
/// Marketing posts and event posts alike; the calendar's own copies are not posts and carry on
/// (decision 12). A site's posts off, its destinations wait. Either way a post more than an hour
/// late turns Failed with Post now. Every change is recorded as a settings change.
/// <para>
/// Bluesky posts are the Bluesky topic's Posting switch, the same column, shown here too so every
/// site's switch is in one place. As there, it goes on only for an account that passed Check.
/// </para>
/// </remarks>
public static class PostSettingsEndpoints
{
    public static IEndpointRouteBuilder MapPostSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/posts")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                return Results.Ok(View(settings));
            })
            .WithName("GetPostSettings")
            .WithSummary("Get posts settings")
            .WithDescription("Whether all posting is paused, and whether posts go to Discord, to the VRChat group and to Bluesky.")
            .Produces<PostSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/", async (
                HttpContext http,
                [FromBody] PostSettingsRequest body,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var blueskyAfter = body.Bluesky ?? settings.BlueskyPostingOn;

                // The Bluesky topic's own rule: on only over a Check that passed.
                if (blueskyAfter && !settings.BlueskyPostingOn && !PostSites.BlueskyReady(settings))
                    return Results.BadRequest(new { error = BlueskySettingsEndpoints.CheckFirst });

                var change = new SettingsChange("posts")
                    .Field("paused", settings.PostsPaused, body.Paused ?? settings.PostsPaused)
                    .Field("discord", settings.DiscordPostsOn, body.Discord ?? settings.DiscordPostsOn)
                    .Field("vrchat", settings.VRChatPostsOn, body.VRChat ?? settings.VRChatPostsOn)
                    .Field("bluesky", settings.BlueskyPostingOn, blueskyAfter);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.PostsPaused = body.Paused ?? settings.PostsPaused;
                settings.DiscordPostsOn = body.Discord ?? settings.DiscordPostsOn;
                settings.VRChatPostsOn = body.VRChat ?? settings.VRChatPostsOn;
                settings.BlueskyPostingOn = blueskyAfter;
                await db.SaveChangesAsync(ct);

                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetPostSettings")
            .WithSummary("Set posts settings")
            .WithDescription(
                "Pauses or resumes all posting, and turns Discord, VRChat and Bluesky posts on or off. "
                + "A switch left out stays as it is. Paused, nothing is sent to any site and posts wait; "
                + "a site switched off, its posts wait. One more than an hour late is not sent and "
                + "turns Failed, with Post now. Bluesky posts are the Bluesky topic's Posting switch, and "
                + "go on only for an account that passed Check.")
            .Produces<PostSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static PostSettingsView View(Core.Data.Entities.Settings settings) =>
        new(settings.PostsPaused, settings.DiscordPostsOn, settings.VRChatPostsOn, settings.BlueskyPostingOn, PostSites.BlueskyReady(settings));
}
