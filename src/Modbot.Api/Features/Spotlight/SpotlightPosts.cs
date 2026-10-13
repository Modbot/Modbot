using Modbot.Api.Features.Posts;
using Modbot.Api.Features.Twitch;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Files;
using Modbot.Core.Net;
using Modbot.Core.Profiles;
using Modbot.Core.Twitch;

namespace Modbot.Api.Features.Spotlight;

/// <summary>
/// Builds the Marketing draft for a week's spotlight (spotlight design §6). It makes an ordinary
/// draft with the sites an operator ticked in Settings → Spotlight, through the checking and copying
/// the Marketing tab's composer uses (<see cref="PostRequests"/>), so a spotlight draft and a
/// hand-written one cannot mean different things by a channel, a role or a group. Nothing here
/// sends: staff finish the draft and send it from Marketing.
/// </summary>
internal static class SpotlightPosts
{
    /// <summary>
    /// A new draft, added to <paramref name="db"/> and not saved. With a member it holds their card
    /// and avatar; without one (staff pick later) it holds the title only.
    /// </summary>
    public static async Task<Post> BuildAsync(
        ModbotContext db,
        Core.Data.Entities.Settings settings,
        SpotlightMember? member,
        PictureLinks pictures,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var (title, text) = member is null
            ? (MemberSpotlight.Title, string.Empty)
            : await MemberSpotlight.CardAsync(db, member.Name, member.UserId, ct).ConfigureAwait(false);

        var pictureId = member is null ? null : await KeepAvatarAsync(db, member.AvatarUrl, pictures, now, ct).ConfigureAwait(false);

        var places = TwitchPostPlaces.Parse(settings.SpotlightPlaces);
        var request = TwitchPosts.RequestFor(places, title, text, draft: true, pictureId);

        // Problems are not refusals here: a draft is for staff to finish, and the composer shows
        // them what to fix (a Bluesky text over its limit, a site that is off) when they open it.
        var (checkedRequest, _) = await PostRequests.CheckAsync(db, request, now, keptPicture: null, ct).ConfigureAwait(false);

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = now,
            CreatedByUserId = null,
            Kind = PostKinds.Spotlight,
        };

        PostRequests.Apply(post, checkedRequest, draft: true, now);
        db.Posts.Add(post);

        return post;
    }

    /// <summary>
    /// Puts a member's card and avatar into a draft that has none yet. Not saved. What staff already
    /// typed is kept: the card goes above their text, their own title and picture stay.
    /// </summary>
    public static async Task FillAsync(
        ModbotContext db, Post post, SpotlightMember member, PictureLinks pictures, DateTimeOffset now, CancellationToken ct)
    {
        var (title, card) = await MemberSpotlight.CardAsync(db, member.Name, member.UserId, ct).ConfigureAwait(false);

        // The title is replaced only while it is still the one the draft started with.
        if (string.IsNullOrWhiteSpace(post.Title) || post.Title == MemberSpotlight.Title)
            post.Title = title;

        var typed = post.Text.Trim();
        if (typed.Length == 0)
        {
            post.Text = card;
        }
        else
        {
            // The card is cut to leave room, never the words staff wrote.
            const string gap = "\n\n";
            var room = Math.Max(0, Post.MaxTextLength - typed.Length - gap.Length);
            var shown = card.Length > room ? card[..room].TrimEnd() : card;
            post.Text = shown.Length == 0 ? typed : shown + gap + typed;
        }

        post.PictureId ??= await KeepAvatarAsync(db, member.AvatarUrl, pictures, now, ct).ConfigureAwait(false);
        post.UpdatedAt = now;
        post.Version++;
    }

    /// <summary>
    /// Fetches the member's avatar and keeps it with the pictures posts use. Null when Modbot has no
    /// avatar or cannot get a picture from it: the card goes without one.
    /// </summary>
    private static async Task<Guid?> KeepAvatarAsync(
        ModbotContext db, string? url, PictureLinks pictures, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        PictureLinkResult fetched;

        try
        {
            fetched = await pictures.FetchAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        if (fetched.Picture is not { } picture || picture.Bytes.Length > CalendarCoverPicture.MaxBytes)
            return null;

        // The type its bytes have, never what the host said.
        var type = PictureFormats.Sniff(picture.Bytes);
        if (!PictureFormats.DiscordTakes(type))
            return null;

        var kept = new CalendarCoverPicture
        {
            Id = Guid.CreateVersion7(),
            Bytes = picture.Bytes,
            ContentType = type!,
            CreatedAt = now,
            CreatedByUserId = null,
        };

        db.CalendarCoverPictures.Add(kept);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return kept.Id;
    }
}
