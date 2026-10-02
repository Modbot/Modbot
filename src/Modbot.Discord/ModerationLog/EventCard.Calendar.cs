using System.Text;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// The cards for Modbot's own calendar: an event planned, changed, cancelled, published, opened.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The event is the title.</strong> These went through the one shape every card had, and
/// the subject of a calendar fact is the event's id, so a channel read "01a0fe12-162e-7be7-a174-…
/// · Planned event changed" with nothing to say which event or what changed (reported
/// 2026-10-02). Every calendar fact carries the event's title, so the title heads the card, linked
/// to the event on Modbot's calendar page, and what happened sits on the author line where the
/// group's name would be. The footer still names the group.
/// </para>
/// <para>
/// <strong>A change lists what changed</strong>, in the calendar form's own words for each field,
/// before and after. Times are Discord's timestamp markup, which every reader sees in their own
/// time zone, as on every other card. The fields an edit touches as bookkeeping, and the ones that
/// hold an id or an address, are named as changed and never printed.
/// </para>
/// <para>
/// <strong>A failure says where and why</strong>: the VRChat calendar, the Discord event, the
/// channel post; then VRChat's or Discord's own words, and how to fix it when the producer knew.
/// </para>
/// </remarks>
public static partial class EventCard
{
    /// <summary>What a calendar card is titled when the fact carries no title.</summary>
    private const string NoEventTitle = "A calendar event";

    private static bool IsPlannedEvent(string type) => type switch
    {
        FactType.PlannedEventCreated
            or FactType.PlannedEventChanged
            or FactType.PlannedEventCancelled
            or FactType.PlannedEventDeleted
            or FactType.PlannedDateCancelled
            or FactType.PlannedDateChanged
            or FactType.PlannedEventOpened
            or FactType.PlannedEventFinished
            or FactType.PlannedEventInstanceOpened
            or FactType.PlannedEventInstanceFailed
            or FactType.PlannedEventPublishFailed
            or FactType.PlannedEventPublished
            or FactType.PlannedEventTakenDown
            or FactType.PlannedEventPictureUploaded
            or FactType.PlannedEventInviteSent
            or FactType.PlannedEventInviteFailed
            or FactType.CalendarWorldPicked => true,
        _ => false,
    };

    /// <summary>One of Modbot's own calendar facts, by kind.</summary>
    private static DiscordEmbedContent PlannedEvent(ModerationEventView e, CardStyle style, CardPicture picture)
    {
        var own = e.What.Own;

        switch (e.Type)
        {
            case FactType.PlannedEventCreated:
                return EventCardFor(
                    e, style,
                    [.. Field("Starts", Time(own.StartsAt)), .. Field("World", WorldOf(e, own.WorldId, null, style)), .. OnVRChat(own)]);

            case FactType.PlannedEventChanged:
                return EventCardFor(e, style, [.. OnVRChat(own), .. CalendarChanges(e, style)]);

            case FactType.PlannedDateChanged:
                return EventCardFor(e, style, [.. Field("Date", Time(own.Date)), .. CalendarChanges(e, style)]);

            case FactType.PlannedDateCancelled:
                return EventCardFor(e, style, Field("Date", Time(own.StartsAt) ?? Time(own.Date)));

            case FactType.PlannedEventDeleted:
                // Gone from the calendar page, so there is nothing for a link to open.
                return EventCardFor(e, style, OnVRChat(own), link: false);

            case FactType.PlannedEventOpened:
            case FactType.PlannedEventFinished:
                return EventCardFor(e, style, Field("Date", Time(own.OccurrenceStartsAt)));

            case FactType.PlannedEventInstanceOpened:
                return EventCardFor(
                    e, style,
                    [.. Field("Where", Where(e, style)), .. Field("Date", Time(own.OccurrenceStartsAt))]);

            case FactType.PlannedEventInstanceFailed:
                return EventCardFor(
                    e, style,
                    [.. Field("Where", Where(e, style)), .. Why(own.Error, own.Fix)]);

            case FactType.PlannedEventPublishFailed:
                return EventCardFor(
                    e, style,
                    [
                        .. Field("Where", PlaceName(own.Place)),
                        .. Field("Date", own.Action == "date" ? Time(own.Date) : null),
                        .. Why(own.Error, own.Fix),
                    ]);

            case FactType.PlannedEventPublished:
            case FactType.PlannedEventTakenDown:
                return EventCardFor(e, style, Field("Where", PlaceName(own.Place)));

            case FactType.CalendarWorldPicked:
                return EventCardFor(
                    e, style,
                    [
                        .. Field("World", WorldOf(e, own.WorldId ?? e.WorldId, own.WorldName, style)),
                        .. Field("From list", Word(own.List)),
                        .. Field("Date", Time(own.OccurrenceStartsAt)),
                    ]);

            case FactType.PlannedEventInviteSent:
            case FactType.PlannedEventInviteFailed:
                return Invite(e, style, picture);

            default:
                return EventCardFor(e, style, []);
        }
    }

    /// <summary>
    /// A calendar card: the event's title, linked to it on the calendar page, with what happened
    /// above it.
    /// </summary>
    private static DiscordEmbedContent EventCardFor(
        ModerationEventView e, CardStyle style, IReadOnlyList<DiscordEmbedField> extra, bool link = true)
    {
        // A picture upload made before its event was saved names no event, and its subject is the
        // file; every other calendar fact is about the event, by its id.
        var eventId = e.What.Own.EventId
            ?? (e.Type == FactType.PlannedEventPictureUploaded ? null : e.SubjectId);

        var url = link && !string.IsNullOrWhiteSpace(eventId)
            ? CardLink.CalendarEvent(eventId, style.PublicAddress)
            : null;

        return Named(e, style, EventTitle(e), url, description: null, extra);
    }

    private static string EventTitle(ModerationEventView e)
        => string.IsNullOrWhiteSpace(e.What.Title) ? NoEventTitle : e.What.Title.Trim();

    /// <summary>
    /// An invite to an event: about the person it went to, titled by the event, and why it failed
    /// when it did.
    /// </summary>
    private static DiscordEmbedContent Invite(ModerationEventView e, CardStyle style, CardPicture picture)
    {
        var sent = e.Type == FactType.PlannedEventInviteSent;
        var title = sent ? $"Invited to {EventTitle(e)}" : $"Invite to {EventTitle(e)} failed";

        IReadOnlyList<DiscordEmbedField> extra =
            [.. Field("Date", Time(e.What.Own.OccurrenceStartsAt)), .. (sent ? [] : Why(e.What.Own.Problem, null))];

        return e.SubjectPlatform == FactPlatform.Discord
            ? DiscordPersonCard(e, style, title, extra)
            : Build(e, style, picture, title, null, extra);
    }

    /// <summary>"VRChat calendar" for a change that came from there, as the page marks it.</summary>
    private static DiscordEmbedField[] OnVRChat(ModbotDetails own)
        => string.Equals(own.On, "vrchat", StringComparison.Ordinal) ? Field("Where", "VRChat calendar") : [];

    /// <summary>Where an event is published, in the calendar page's own words for each place.</summary>
    private static string? PlaceName(string? place) => place switch
    {
        null or "" => null,
        CalendarPlaces.VRChat => "VRChat calendar",
        CalendarPlaces.DiscordEvent => "Discord event",
        CalendarPlaces.ChannelPost => "Discord channel post",
        CalendarPlaces.CancelPost => "Cancelled post in the channel",
        var other => Word(other),
    };

    /// <summary>
    /// Why something failed: the other side's own words, cut to a line or two, and the fix when the
    /// producer knew it. A field of its own, not inline, so the words have the card's width.
    /// </summary>
    private static DiscordEmbedField[] Why(string? error, string? fix)
    {
        var lines = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(error))
            lines.Add(CardText.Fit(CardText.EscapeText(error.Trim()), DescriptionLength));

        if (!string.IsNullOrWhiteSpace(fix))
            lines.Add(CardText.Fit(CardText.EscapeText(fix.Trim()), DescriptionLength));

        return lines.Count == 0
            ? []
            : [new DiscordEmbedField("Why", CardText.Fit(string.Join('\n', lines), FieldLength))];
    }

    /// <summary>A world by its name, linked to it in Modbot, or nothing when Modbot has not read it.</summary>
    private static string? WorldOf(ModerationEventView e, string? worldId, string? nameFromFact, CardStyle style)
    {
        if (string.IsNullOrWhiteSpace(worldId))
            return null;

        var name = nameFromFact ?? e.WorldNamed(worldId);

        return string.IsNullOrWhiteSpace(name) ? null : CardLink.World(name, worldId, style.PublicAddress);
    }

    // ── What changed ─────────────────────────────────────────────────────────────────────────

    /// <summary>The fields an edit changes without anybody changing them, which say nothing about the edit.</summary>
    private static readonly HashSet<string> CalendarBookkeeping = new(StringComparer.Ordinal)
    {
        "id", "eventId", "version", "updatedAt", "createdAt", "occurrenceStartsAt",
    };

    /// <summary>The fields that hold an id or an address: named as changed, never printed.</summary>
    private static readonly HashSet<string> CalendarIds = new(StringComparer.Ordinal)
    {
        "imageUrl", "vrchatImageId", "worldListId", "inviteHost", "inviteStaff", "inviteList",
    };

    /// <summary>The fields that are a yes or a no.</summary>
    private static readonly HashSet<string> CalendarSwitches = new(StringComparer.Ordinal)
    {
        "notifyMembers", "publishToVRChat", "publishToDiscord", "postToChannel", "autoOpen",
        "announceFirstJoinInDiscord", "announceFirstJoinInVRChat",
    };

    /// <summary>
    /// What a change touched, one line per field in the calendar form's own words, up to
    /// <see cref="ChangesListed"/>; "No visible change" when nothing a moderator can see differs.
    /// </summary>
    /// <remarks>
    /// A change fact with nothing visible in it is still posted, saying so, rather than left out:
    /// a route that takes event changes asked for every one, and a card that is missing reads as a
    /// channel that stopped working.
    /// </remarks>
    private static DiscordEmbedField[] CalendarChanges(ModerationEventView e, CardStyle style)
    {
        var own = e.What.Own;

        // An older fact, or one written without the two sides: nothing to compare.
        if (own.Before is null || own.After is null)
            return [];

        var before = own.Before.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        var after = own.After.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

        var lines = new List<string>();
        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in own.After.Select(v => v.Key).Concat(own.Before.Select(v => v.Key)).Distinct(StringComparer.Ordinal))
        {
            if (CalendarBookkeeping.Contains(key))
                continue;

            var was = before.GetValueOrDefault(key);
            var now = after.GetValueOrDefault(key);

            if (Same(was, now))
                continue;

            // The picture's address and its VRChat id change together; one line says it.
            var name = CalendarFieldName(key);
            if (!named.Add(name))
                continue;

            lines.Add("**" + CardText.EscapeText(name) + "**: " + CalendarChange(e, key, was, now, style));
        }

        if (lines.Count == 0)
            return [new DiscordEmbedField("Changed", "No visible change")];

        var text = new StringBuilder();

        for (var i = 0; i < lines.Count; i++)
        {
            if (i == ChangesListed)
            {
                text.Append("\nand ").Append(lines.Count - i).Append(" more");
                break;
            }

            if (i > 0)
                text.Append('\n');

            text.Append(lines[i]);
        }

        return [new DiscordEmbedField("Changed", CardText.Fit(text.ToString(), FieldLength))];
    }

    /// <summary>Whether two sides read the same: nothing and an empty value are the same.</summary>
    private static bool Same(string? a, string? b)
        => string.Equals(
            string.IsNullOrWhiteSpace(a) ? null : a.Trim(),
            string.IsNullOrWhiteSpace(b) ? null : b.Trim(),
            StringComparison.Ordinal);

    /// <summary>What one field went from and to.</summary>
    private static string CalendarChange(ModerationEventView e, string key, string? was, string? now, CardStyle style)
    {
        if (CalendarIds.Contains(key))
            return "changed";

        if (CalendarSwitches.Contains(key))
            return $"{YesNo(was)} → {YesNo(now)}";

        switch (key)
        {
            case "startsAt" or "endsAt" or "repeatUntil":
                return $"{Time(was) ?? Side(was)} → {Time(now) ?? Side(now)}";

            case "title" or "description":
                return GroupWords(new EventChange(key, was, now));

            case "accessType":
                return $"{Openness(was) ?? "nothing"} → {Openness(now) ?? "nothing"}";

            case "channelId":
                return $"{Channel(was, null) ?? "nothing"} → {Channel(now, null) ?? "nothing"}";

            case "mentionRoleId":
                return $"{(string.IsNullOrWhiteSpace(was) ? "nobody" : CardLink.RoleMention(was))} → "
                       + (string.IsNullOrWhiteSpace(now) ? "nobody" : CardLink.RoleMention(now));

            case "worldId":
            {
                var from = WorldOf(e, was, null, style);
                var to = WorldOf(e, now, null, style);

                // A world Modbot has not read has no name to show, and its id is not shown either.
                if ((from is null && !string.IsNullOrWhiteSpace(was)) || (to is null && !string.IsNullOrWhiteSpace(now)))
                    return "changed";

                return $"{from ?? "nothing"} → {to ?? "nothing"}";
            }
        }

        // Any other id or address the payload gains later is named as changed, as on every card.
        return Hidden(key) ? "changed" : $"{Side(was)} → {Side(now)}";
    }

    /// <summary>The calendar form's own words for each field it saves.</summary>
    private static string CalendarFieldName(string key) => key switch
    {
        "title" => "Title",
        "description" => "Description",
        "imageUrl" or "vrchatImageId" => "Picture",
        "category" => "Category",
        "languages" => "Languages",
        "platforms" => "Platforms",
        "tags" => "Tags",
        "visibility" => "Visible to",
        "notifyMembers" => "Notify group members",
        "startsAt" => "Starts",
        "endsAt" => "Ends",
        "timeZone" => "Time zone",
        "repeat" => "Repeat",
        "repeatDays" => "Repeat days",
        "repeatUntil" => "Last date",
        "worldId" => "World",
        "worldListId" => "World list",
        "accessType" => "Who can join",
        "region" => "Region",
        "state" => "Status",
        "publishToVRChat" => "VRChat calendar",
        "publishToDiscord" => "Discord event",
        "postToChannel" => "Discord channel post",
        "channelId" => "Channel",
        "mentionRoleId" => "Mention role",
        "autoOpen" => "Open the instance",
        "openMinutesBefore" => "Minutes early",
        "inviteHost" => "Host",
        "inviteStaff" => "Staff",
        "inviteList" => "Invite list",
        "announceFirstJoinInDiscord" => "Announce in Discord when the first person joins",
        "announceFirstJoinInVRChat" => "Announce in VRChat when the first person joins",
        _ => PlainName(key),
    };
}
