using Modbot.Core.Data.Entities;

namespace Modbot.Core.Calendar;

/// <summary>
/// What the calendar's Discord side would send for an event, drawn by the same code that sends it,
/// for the event form's Preview (calendar design §14).
/// </summary>
/// <remarks>
/// Declared here rather than in <c>Modbot.Discord</c> so the API can ask without a reference to the
/// bot, the way <c>IDiscordBotStatus</c> is: the host wires both. A host without the bot has none,
/// and the preview then shows the VRChat calendar and the phone calendar only.
/// </remarks>
public interface ICalendarDiscordPreview
{
    /// <param name="calendarEvent">Checked and filled in as a save would, but not saved.</param>
    /// <param name="world">The event's world, when Modbot knows it.</param>
    CalendarDiscordPreview Preview(CalendarEvent calendarEvent, VRChatWorld? world, CalendarPreviewContext context);
}

/// <param name="PublicAddress">The public address from settings, for the short join address.</param>
/// <param name="GroupName">The managed group's name, which heads the channel post.</param>
/// <param name="JoinLink">The open instance's join link, only while the event is open and has one.</param>
/// <param name="Now">From <c>IModbotClock</c>: a Discord event that starts in the past is moved to now.</param>
public sealed record CalendarPreviewContext(string? PublicAddress, string? GroupName, string? JoinLink, DateTimeOffset Now);

public sealed record CalendarDiscordPreview(CalendarDiscordEventPreview DiscordEvent, CalendarChannelPostPreview ChannelPost);

/// <summary>The Discord server event, field by field as Discord is sent it.</summary>
/// <param name="Name">Cut to Discord's 100 characters.</param>
/// <param name="Description">Cut to Discord's 1000, with the join link at the top when it does not fit the location.</param>
/// <param name="Location">The world's name, "VRChat" without one, or the join address while an opened instance is open.</param>
/// <param name="CoverUrl">The event's picture link, or the world's picture; only an https address.</param>
public sealed record CalendarDiscordEventPreview(
    string Name,
    string? Description,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Location,
    string? CoverUrl);

/// <summary>The card posted in the channel, as Discord is sent it.</summary>
/// <param name="GroupName">The line above the title.</param>
/// <param name="TitleLink">Where the title leads: the join link while the instance is open.</param>
/// <param name="Colour">The card's colour as a number, <c>0xRRGGBB</c>.</param>
/// <param name="Fields">
/// Discord's own text: times as <c>&lt;t:…:F&gt;</c> and links as <c>[name](address)</c>, which
/// Discord draws in each reader's own time and as a link.
/// </param>
/// <param name="PictureUrl">The event's picture link, or the world's picture, which goes with the message as a file.</param>
public sealed record CalendarChannelPostPreview(
    string? GroupName,
    string Title,
    string? TitleLink,
    string? Description,
    long Colour,
    IReadOnlyList<CalendarPreviewField> Fields,
    string? Footer,
    string? PictureUrl,
    IReadOnlyList<CalendarPreviewButton> Buttons);

public sealed record CalendarPreviewField(string Name, string Value, bool Inline);

public sealed record CalendarPreviewButton(string Label, string Url);
