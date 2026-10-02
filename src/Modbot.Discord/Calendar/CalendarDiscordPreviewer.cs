using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Instances;

namespace Modbot.Discord.Calendar;

/// <summary>
/// The event form's Discord preview, from the publisher's own builders (calendar design §14).
/// </summary>
/// <remarks>
/// Asks Discord nothing and uploads nothing. The one difference from what is posted: the world's
/// picture goes with a real post as a file, and the preview shows the address it came from.
/// </remarks>
public sealed class CalendarDiscordPreviewer : ICalendarDiscordPreview
{
    /// <summary>Where the web app reads a picture cropped for Discord: <c>GET /api/calendar/covers/{id}</c>.</summary>
    public const string CoverPath = "/api/calendar/covers/";

    public CalendarDiscordPreview Preview(CalendarEvent calendarEvent, VRChatWorld? world, CalendarPreviewContext context)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(context);

        var publicAddress = context.PublicAddress?.TrimEnd('/');
        var details = CalendarDiscordPublisher.ServerEventDetails(
            calendarEvent, world, publicAddress, context.JoinLink, context.Now);

        // The picture cropped for Discord (§15.4) is sent as a file; the preview shows Modbot's own
        // address for it.
        var cover = calendarEvent.CoverPictureId is { } coverId ? CoverPath + coverId : null;

        var picture = cover ?? CalendarCard.OwnPicture(calendarEvent) ?? InstanceCard.PictureOf(world);
        var (card, links) = CalendarDiscordPublisher.Post(
            calendarEvent, world, context.JoinLink, new CardStyle(publicAddress, context.GroupName), picture);

        return new CalendarDiscordPreview(
            new CalendarDiscordEventPreview(
                details.Name, details.Description, details.StartsAt, details.EndsAt, details.Location, cover ?? details.CoverImageUrl),
            new CalendarChannelPostPreview(
                card.AuthorName,
                card.Title,
                card.Url,
                card.Description,
                card.Color,
                [.. card.Fields.Select(f => new CalendarPreviewField(f.Name, f.Value, f.Inline))],
                card.Footer,
                card.ImageUrl,
                [.. links.Select(l => new CalendarPreviewButton(l.Label, l.Url))]));
    }
}
