using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;
using NodaTime;

namespace Modbot.Core.Calendar;

/// <summary>
/// What an event is sent to Google Calendar as (Google Calendar design §3.3): one Google event per
/// Modbot event, its repeat as <c>RRULE</c> and <c>EXDATE</c> lines written by the same code as the
/// calendar feed.
/// </summary>
/// <remarks>
/// <para>
/// Only the world page goes with it as a link (decision 4 A): never Modbot's join address or public
/// address. No picture (Google takes Drive files only), no reminders (they would be the service
/// account's own), and no attendees, so nobody is ever emailed. A colour goes only when the event
/// was given one, and the event is free (not busy) on the calendar unless it was set to busy.
/// </para>
/// <para>
/// Modbot's event id goes in the event's private properties, which only the calendar's own copy
/// holds, so a read-back can tell Modbot's event from somebody else's (§3.4).
/// </para>
/// </remarks>
/// <param name="Summary">The title, with "Cancelled: " in front for a cancelled event (decision 3 B).</param>
/// <param name="Description">The description, HTML-escaped, then the world's name and page; null for none.</param>
/// <param name="Location">The world's name, its id when Modbot has no name, or <c>VRChat</c>.</param>
/// <param name="StartsAt">The first start.</param>
/// <param name="EndsAt">The first end.</param>
/// <param name="TimeZone">The IANA zone the times and the repeat are counted in.</param>
/// <param name="Recurrence">The <c>RRULE</c> and <c>EXDATE</c> lines; empty for an event that does not repeat.</param>
/// <param name="SourceTitle">The group's name, for Google's link back; null without a world page.</param>
/// <param name="SourceUrl">The world page; null without a world.</param>
/// <param name="ColourId">Google's id of the event's colour; null for the calendar's own.</param>
/// <param name="Transparency">Google's <c>opaque</c> (busy) or <c>transparent</c> (free).</param>
public sealed record CalendarGoogleBody(
    Guid EventId,
    string Summary,
    string? Description,
    string Location,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TimeZone,
    IReadOnlyList<string> Recurrence,
    string Visibility,
    string? SourceTitle,
    string? SourceUrl,
    string? ColourId,
    string Transparency)
{
    /// <summary>Who sees the event's details on Google: always the calendar's own setting.</summary>
    public const string DefaultVisibility = "default";

    /// <summary>The name of the private property that holds Modbot's event id.</summary>
    public const string OwnerProperty = "modbotEvent";

    /// <summary>The event as Google is sent it.</summary>
    /// <param name="worldNames">World names by id.</param>
    /// <param name="groupName">The group's name, shown as where the world link comes from.</param>
    public static CalendarGoogleBody For(CalendarEvent calendarEvent, IReadOnlyDictionary<string, string> worldNames, string? groupName)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(worldNames);

        var zone = CalendarRepeat.ZoneOf(calendarEvent);
        var cancelled = calendarEvent.State == CalendarEventStates.Cancelled;
        var worldName = WorldName(calendarEvent, worldNames);
        var page = WorldPage(calendarEvent.WorldId);

        // A cancelled repeating event stops at the date it was cancelled on (§3.6).
        DateOnly? cut = null;
        if (cancelled && calendarEvent.Repeat != CalendarRepeats.None)
        {
            var day = Instant.FromDateTimeOffset(CalendarGoogle.CancelledDate(calendarEvent).PlannedStartsAt).InZone(zone).Date;
            cut = new DateOnly(day.Year, day.Month, day.Day);
        }

        var recurrence = new List<string>();

        if (CalendarFeedWriter.Rule(calendarEvent, zone, cut) is { } rule)
        {
            recurrence.Add("RRULE:" + rule);

            foreach (var dropped in calendarEvent.DateChanges.Where(c => c.Cancelled).OrderBy(c => c.PlannedStartsAt))
                recurrence.Add(CalendarICalText.ExDate(dropped.PlannedStartsAt, zone));
        }

        return new CalendarGoogleBody(
            calendarEvent.Id,
            SummaryOf(calendarEvent, CalendarSiteText.Title(calendarEvent, CalendarSite.Google)),
            DescriptionOf(TextOf(calendarEvent, null), worldName, page),
            worldName ?? "VRChat",
            calendarEvent.StartsAt,
            calendarEvent.EndsAt,
            zone.Id,
            recurrence,
            DefaultVisibility,
            page is null ? null : (string.IsNullOrWhiteSpace(groupName) ? "Modbot" : groupName.Trim()),
            page,
            CalendarGoogleColours.ColourOf(calendarEvent.GoogleColour),
            CalendarGoogleColours.TransparencyOf(calendarEvent.GoogleShowAs));
    }

    /// <summary>
    /// A hash of everything Google is sent, and the calendar it goes to: the place is written only
    /// when this changes. The event's Open or Scheduled state is not in it, so a date opening costs
    /// no write. The Google event id is not in it either: a new turn is a new insert anyway.
    /// </summary>
    public string Fingerprint(string calendarId) => CalendarFingerprint.Of(
        [
            "google", calendarId, EventId.ToString("D"), Summary, Description, Location, StartsAt, EndsAt, TimeZone,
            Recurrence, Visibility, SourceTitle, SourceUrl,
            .. LookParts(ColourId, Transparency),
        ]);

    /// <summary>
    /// The colour and busy parts of a fingerprint, written only when they are not the defaults (no
    /// colour, Free), so an event that never chose either keeps the hash it had before they existed
    /// and is not written again on deploy.
    /// </summary>
    internal static object?[] LookParts(string? colourId, string transparency)
    {
        var parts = new List<object?>();

        if (colourId is not null)
            parts.AddRange(["colour", colourId]);

        if (transparency != CalendarGoogleColours.TransparencyOf(CalendarGoogleColours.Free))
            parts.AddRange(["show-as", transparency]);

        return [.. parts];
    }

    /// <summary>The body for <c>events.insert</c> and <c>events.update</c>, with the id Modbot gave it.</summary>
    public JsonObject ToJson(string googleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(googleId);

        var body = new JsonObject
        {
            ["id"] = googleId,
            ["status"] = "confirmed",
            ["summary"] = Summary,
            ["location"] = Location,
            ["start"] = TimeOf(StartsAt, TimeZone),
            ["end"] = TimeOf(EndsAt, TimeZone),
            ["visibility"] = Visibility,

            // Free unless the event was set to busy, so a group event does not make the owner busy on their own calendar.
            ["transparency"] = Transparency,

            // Reminders are each reader's own; any set here would be the service account's.
            ["reminders"] = new JsonObject { ["useDefault"] = true },
            ["extendedProperties"] = new JsonObject
            {
                ["private"] = new JsonObject { [OwnerProperty] = EventId.ToString("D") },
            },
        };

        if (Description is not null)
            body["description"] = Description;

        // No colour says nothing, and an update that leaves the key out takes a colour off.
        if (ColourId is not null)
            body["colorId"] = ColourId;

        if (Recurrence.Count > 0)
            body["recurrence"] = new JsonArray([.. Recurrence.Select(line => (JsonNode?)JsonValue.Create(line))]);

        if (SourceUrl is not null && SourceTitle is not null)
            body["source"] = new JsonObject { ["title"] = SourceTitle, ["url"] = SourceUrl };

        return body;
    }

    /// <summary>Whether an event read back from Google is this Modbot event's own.</summary>
    public static bool IsOwnedBy(JsonObject googleEvent, Guid eventId)
    {
        ArgumentNullException.ThrowIfNull(googleEvent);

        return googleEvent["extendedProperties"]?["private"]?[OwnerProperty] is JsonValue value
            && value.TryGetValue<string>(out var owner)
            && string.Equals(owner, eventId.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether an event read back from Google was deleted there (kept by Google as cancelled).</summary>
    public static bool IsCancelled(JsonObject googleEvent)
    {
        ArgumentNullException.ThrowIfNull(googleEvent);

        return googleEvent["status"] is JsonValue value && value.TryGetValue<string>(out var status) && status == "cancelled";
    }

    // ── One date of a series (§3.5) ──────────────────────────────────────────────────────

    /// <summary>
    /// What one date changed on its own is on Google: cancelled, put back as planned, or with its
    /// own times and words. A cancelled event's dates after the one it was cancelled on are
    /// cancelled too: the series stops there.
    /// </summary>
    public static CalendarGoogleDate DateFor(
        CalendarEvent calendarEvent, CalendarDateChange change, IReadOnlyDictionary<string, string> worldNames)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(worldNames);

        var zone = CalendarRepeat.ZoneOf(calendarEvent);
        var length = CalendarRepeat.LengthOf(calendarEvent);
        var planned = change.PlannedStartsAt;

        var cutAfter = calendarEvent.State == CalendarEventStates.Cancelled
            ? CalendarGoogle.CancelledDate(calendarEvent).PlannedStartsAt
            : (DateTimeOffset?)null;

        if (change.Cancelled || (cutAfter is { } cut && planned > cut))
        {
            return new CalendarGoogleDate(
                CalendarGoogleDateKind.Cancel, planned, planned + length, zone.Id, string.Empty, null,
                CalendarFingerprint.Of("google-date", "cancel", planned));
        }

        var colourId = CalendarGoogleColours.ColourOf(calendarEvent.GoogleColour);
        var transparency = CalendarGoogleColours.TransparencyOf(calendarEvent.GoogleShowAs);

        var kind = CalendarDates.IsPlain(change, length) ? CalendarGoogleDateKind.Planned : CalendarGoogleDateKind.Changed;
        var occurrence = CalendarRepeat.Changed(change, length);
        var summary = SummaryOf(calendarEvent, CalendarRepeat.TitleOf(calendarEvent, occurrence, CalendarSite.Google));
        var description = DescriptionOf(
            TextOf(calendarEvent, occurrence),
            WorldName(calendarEvent, worldNames),
            WorldPage(calendarEvent.WorldId));

        return new CalendarGoogleDate(
            kind, occurrence.StartsAt, occurrence.EndsAt, zone.Id, summary, description,
            CalendarFingerprint.Of(
            [
                "google-date", kind.ToString(), planned, occurrence.StartsAt, occurrence.EndsAt, summary, description,
                .. LookParts(colourId, transparency),
            ]),
            colourId,
            transparency);
    }

    /// <summary>The world's page on vrchat.com: the one link a Google event carries (decision 4 A).</summary>
    public static string? WorldPage(string? worldId) =>
        string.IsNullOrWhiteSpace(worldId) ? null : "https://vrchat.com/home/world/" + Uri.EscapeDataString(worldId.Trim());

    /// <summary>A time as Google takes it: RFC 3339 with the zone's offset at that moment, and the zone.</summary>
    public static JsonObject TimeOf(DateTimeOffset at, string timeZone)
    {
        var zone = CalendarRepeat.FindZone(timeZone) ?? DateTimeZone.Utc;
        var local = Instant.FromDateTimeOffset(at).InZone(zone).ToDateTimeOffset();

        return new JsonObject
        {
            ["dateTime"] = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["timeZone"] = timeZone,
        };
    }

    private static string SummaryOf(CalendarEvent calendarEvent, string title) =>
        calendarEvent.State == CalendarEventStates.Cancelled ? CalendarGoogle.CancelledTitle + title : title;

    private static string? WorldName(CalendarEvent calendarEvent, IReadOnlyDictionary<string, string> worldNames)
    {
        if (calendarEvent.WorldId is not { Length: > 0 } worldId)
            return null;

        return worldNames.TryGetValue(worldId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : worldId;
    }

    /// <summary>
    /// The HTML of the description's own words, or null for none: a date's own description escaped,
    /// else the one typed for Google with its links made, else the shared one escaped. A date's own
    /// words win over the Google description, as they do on every site (<see cref="CalendarSiteText"/>).
    /// </summary>
    private static string? TextOf(CalendarEvent calendarEvent, CalendarOccurrence? occurrence)
    {
        // One date of a series is sent apart from the series' own text and is not sent again when the
        // Discord event moves on to the next date, so it holds no Discord address (CalendarAutoLinks.For).
        var links = CalendarAutoLinks.For(calendarEvent, CalendarSite.Google, ofADate: occurrence is not null);

        if (occurrence?.Change?.Description is { Length: > 0 } date && !string.IsNullOrWhiteSpace(date))
            return Escape(CalendarAutoLinks.Fill(date, links).Trim());

        if (CalendarSiteText.OwnDescription(calendarEvent, CalendarSite.Google) is { } own)
            return CalendarGoogleLinks.Html(CalendarAutoLinks.Fill(own, links).Trim());

        return string.IsNullOrWhiteSpace(calendarEvent.Description) ? null : Escape(CalendarAutoLinks.Fill(calendarEvent.Description, links).Trim());
    }

    /// <summary>
    /// The description's HTML (Google reads a description as HTML, so its words come escaped), with
    /// its line breaks, then a blank line and the world's name and page.
    /// </summary>
    private static string? DescriptionOf(string? html, string? worldName, string? worldPage)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(html))
            parts.Add(html);

        if (worldPage is not null)
            parts.Add($"World: {Escape(worldName ?? string.Empty)}\n{worldPage}");

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>The three characters HTML would read as markup.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var escaped = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            escaped.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => c.ToString(),
            });
        }

        return escaped.ToString();
    }
}

/// <summary>What one date of a series is on Google.</summary>
public enum CalendarGoogleDateKind
{
    /// <summary>Taken out of the series.</summary>
    Cancel,

    /// <summary>Put back as planned: the planned time and the event's own words.</summary>
    Planned,

    /// <summary>Moved, or given its own title or description.</summary>
    Changed,
}

/// <summary>One date of a series as Google is sent it, and a hash of it.</summary>
public sealed record CalendarGoogleDate(
    CalendarGoogleDateKind Kind,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TimeZone,
    string Summary,
    string? Description,
    string Fingerprint,
    string? ColourId = null,
    string Transparency = "transparent")
{
    /// <summary>
    /// The date as Google gave it (from <c>events.instances</c>), with its status, times, words,
    /// colour and busy or free set to this. The rest is sent back as it came, as Google's update
    /// replaces the whole event.
    /// </summary>
    public JsonObject ApplyTo(JsonObject instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var body = (JsonObject)instance.DeepClone();

        if (Kind == CalendarGoogleDateKind.Cancel)
        {
            body["status"] = "cancelled";
            return body;
        }

        body["status"] = "confirmed";
        body["start"] = CalendarGoogleBody.TimeOf(StartsAt, TimeZone);
        body["end"] = CalendarGoogleBody.TimeOf(EndsAt, TimeZone);
        body["summary"] = Summary;

        if (Description is null)
            body.Remove("description");
        else
            body["description"] = Description;

        body["transparency"] = Transparency;

        if (ColourId is null)
            body.Remove("colorId");
        else
            body["colorId"] = ColourId;

        return body;
    }
}
