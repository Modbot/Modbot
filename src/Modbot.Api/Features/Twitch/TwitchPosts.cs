using Modbot.Api.Features.Posts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Twitch;

namespace Modbot.Api.Features.Twitch;

/// <summary>
/// Makes the "We're live on Twitch" post (Twitch design, step 2). It builds an ordinary post, with
/// the sites an operator ticked in Settings → Twitch and their own choices, and hands it to the
/// shared posting system: the senders claim it, send it at most once, read it back, and apply Pause
/// all posting, the hourly cap and the 15-minute late rule. This adds no sending code.
/// </summary>
/// <remarks>
/// The post is built by the same checking and copying the Marketing tab's composer uses
/// (<see cref="PostRequests"/>), so a "live" post and a hand-written one cannot mean different things
/// by a channel, a role or a group.
/// </remarks>
internal static class TwitchPosts
{
    /// <summary>The request a "live" post is checked and built from: now, UTC, nothing but the ticked sites.</summary>
    public static PostRequest RequestFor(TwitchPostPlaces places, string title, string text)
    {
        ArgumentNullException.ThrowIfNull(places);

        return new PostRequest(
            title,
            text,
            PictureId: null,
            When: PostRequests.WhenNow,
            SendAt: null,
            TimeZone: "UTC",
            Draft: false,
            EventId: null,
            Discord: places.Discord is { } discord
                ? new PostDiscordRequest(discord.ChannelId, discord.RoleId, discord.Publish)
                : null,
            Version: null,
            VRChat: places.VRChat is { } vrchat
                ? new PostVRChatRequest(vrchat.Visibility, vrchat.RoleIds, vrchat.Notify)
                : null,
            Bluesky: places.Bluesky ? new PostBlueskyRequest() : null);
    }

    /// <summary>
    /// What is wrong with the sites ticked, one sentence each, found the way the Marketing tab finds
    /// it. Empty when nothing is ticked or all is well.
    /// </summary>
    public static async Task<List<string>> CheckPlacesAsync(
        ModbotContext db, TwitchPostPlaces places, DateTimeOffset now, CancellationToken ct)
    {
        if (!places.AnyTicked)
            return [];

        var (_, problems) = await PostRequests.CheckAsync(db, RequestFor(places, "Live", "Live"), now, keptPicture: null, ct);
        return problems;
    }

    /// <summary>
    /// A new post for <paramref name="stream"/>, added to <paramref name="db"/> and not saved. The
    /// title and text are the operator's template (or the built-in one) with the stream's title,
    /// category and the channel's link put in. Null when no site is ticked.
    /// </summary>
    public static async Task<Post?> BuildAsync(
        ModbotContext db,
        Core.Data.Entities.Settings settings,
        TwitchStream stream,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var places = TwitchPostPlaces.Parse(settings.TwitchPostPlaces);
        if (!places.AnyTicked)
            return null;

        var link = TwitchRules.ChannelLink(settings.TwitchChannelLogin ?? string.Empty);
        var title = TwitchRules.Fill(
            settings.TwitchPostTitle ?? TwitchRules.DefaultTitle, stream.Title, stream.Category, link, Post.MaxTitleLength).Trim();
        var text = TwitchRules.Fill(
            settings.TwitchPostText ?? TwitchRules.DefaultText, stream.Title, stream.Category, link, Post.MaxTextLength);

        // A template that came out empty still says where to watch.
        if (string.IsNullOrWhiteSpace(text))
            text = link;

        var request = RequestFor(places, title.Length == 0 ? TwitchRules.DefaultTitle : title, text);

        // Problems are not refusals here: the stream is live now and the post is the group's own
        // setting. A site that cannot take it fails on its own sender's words, where staff see it.
        var (checkedRequest, _) = await PostRequests.CheckAsync(db, request, now, keptPicture: null, ct);

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now,
            CreatedByUserId = null,
            Kind = PostKinds.TwitchLive,
            ExternalKey = stream.Id,
        };

        PostRequests.Apply(post, checkedRequest, draft: false, now);
        db.Posts.Add(post);

        return post;
    }
}
