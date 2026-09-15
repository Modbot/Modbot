using System.Text;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>Calendar design §6: valid iCalendar, compared against a written-out reference.</summary>
public class CalendarFeedWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFeedMatchesTheReference()
    {
        var weekly = new CalendarEvent
        {
            Id = Guid.Parse("01923456-7890-7abc-8def-0123456789ab"),
            Title = "Friday night, at the Cat; bring friends",
            Description = "Line one\nLine two \\ done",
            // Sunday 20 September 2026, 20:00 in London (summer time).
            StartsAt = new DateTimeOffset(2026, 9, 20, 19, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 9, 20, 21, 0, 0, TimeSpan.Zero),
            TimeZone = "Europe/London",
            Repeat = CalendarRepeats.Weekly,
            RepeatDays = ["SU"],
            RepeatUntil = new DateOnly(2026, 11, 29),
            WorldId = "wrld_1",
            Version = 3,
            UpdatedAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
            State = CalendarEventStates.Scheduled,
        };

        var once = new CalendarEvent
        {
            Id = Guid.Parse("01923456-7890-7abc-8def-0123456789ac"),
            Title = "Quiz",
            Description = "",
            StartsAt = new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 9, 18, 19, 0, 0, TimeSpan.Zero),
            TimeZone = "UTC",
            Version = 1,
            UpdatedAt = new DateTimeOffset(2026, 9, 14, 8, 30, 0, TimeSpan.Zero),
            State = CalendarEventStates.Scheduled,
        };

        var feed = CalendarFeedWriter.Write(
            "Night Owls",
            [weekly, once],
            new Dictionary<string, string> { ["wrld_1"] = "The Black Cat" },
            Now);

        var expected = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Modbot//Calendar//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "X-WR-CALNAME:Night Owls",
            "BEGIN:VTIMEZONE",
            "TZID:Europe/London",
            "BEGIN:DAYLIGHT",
            "DTSTART:20260329T010000",
            "TZOFFSETFROM:+0000",
            "TZOFFSETTO:+0100",
            "TZNAME:BST",
            "END:DAYLIGHT",
            "BEGIN:STANDARD",
            "DTSTART:20261025T020000",
            "TZOFFSETFROM:+0100",
            "TZOFFSETTO:+0000",
            "TZNAME:GMT",
            "END:STANDARD",
            "END:VTIMEZONE",
            "BEGIN:VEVENT",
            "UID:01923456-7890-7abc-8def-0123456789ac@modbot",
            "DTSTAMP:20260914T083000Z",
            "SEQUENCE:1",
            "DTSTART:20260918T180000Z",
            "DTEND:20260918T190000Z",
            "SUMMARY:Quiz",
            "STATUS:CONFIRMED",
            "END:VEVENT",
            "BEGIN:VEVENT",
            "UID:01923456-7890-7abc-8def-0123456789ab@modbot",
            "DTSTAMP:20260915T120000Z",
            "SEQUENCE:3",
            "DTSTART;TZID=Europe/London:20260920T200000",
            "DTEND;TZID=Europe/London:20260920T220000",
            "RRULE:FREQ=WEEKLY;BYDAY=SU;UNTIL=20261129T235959Z",
            @"SUMMARY:Friday night\, at the Cat\; bring friends",
            @"DESCRIPTION:Line one\nLine two \\ done",
            "LOCATION:The Black Cat",
            "STATUS:CONFIRMED",
            "END:VEVENT",
            "END:VCALENDAR",
            "");

        Assert.Equal(expected, feed);
    }

    [Fact]
    public void LongLinesAreFoldedAt75OctetsWithoutSplittingACharacter()
    {
        var e = new CalendarEvent
        {
            Id = Guid.Parse("01923456-7890-7abc-8def-0123456789ad"),
            Title = string.Concat(Enumerable.Repeat("Größe Nachtveranstaltung ", 12)),
            StartsAt = new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2026, 9, 18, 19, 0, 0, TimeSpan.Zero),
            TimeZone = "UTC",
            UpdatedAt = Now,
            State = CalendarEventStates.Scheduled,
        };

        var feed = CalendarFeedWriter.Write("Modbot", [e], new Dictionary<string, string>(), Now);
        var lines = feed.Split("\r\n");

        Assert.All(lines, line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));

        // Unfolding (RFC 5545 §3.1) gives back the whole title.
        var unfolded = feed.Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:" + e.Title + "\r\n", unfolded, StringComparison.Ordinal);
        Assert.DoesNotContain('�', feed);
    }

    [Theory]
    [InlineData(CalendarRepeats.Daily, "FREQ=DAILY")]
    [InlineData(CalendarRepeats.Monthly, "FREQ=MONTHLY")]
    public void RepeatsAreRules(string repeat, string rule)
    {
        var e = CalendarRepeatTests.Event(new DateTimeOffset(2026, 9, 18, 18, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), repeat);

        Assert.Equal(rule, CalendarFeedWriter.Rule(e, CalendarRepeat.ZoneOf(e)));
    }

    [Fact]
    public void TextIsEscaped()
    {
        Assert.Equal(@"a\\b\;c\,d\ne", CalendarFeedWriter.Escape("a\\b;c,d\r\ne"));
    }
}
