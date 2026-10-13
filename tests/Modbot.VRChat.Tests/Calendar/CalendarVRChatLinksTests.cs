using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// The Discord event link on a repeating event (calendar templates plan, step 4, decision 2026-10-13):
/// VRChat is sent one update per date for the series text, and a date held apart is not sent again
/// when the Discord event moves on to the next date.
/// </summary>
public class CalendarVRChatLinksTests
{
    private static readonly DateTimeOffset First = new(2026, 10, 9, 19, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Weekly() => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "Movie night",
        Description = "Chat: {{Discord event}}",
        StartsAt = First,
        EndsAt = First.AddHours(2),
        TimeZone = "UTC",
        Repeat = CalendarRepeats.Weekly,
        RepeatDays = ["FR"],
        State = CalendarEventStates.Scheduled,
    };

    private static CalendarLinks Links(string discord) => new(null, discord, null, null);

    [Fact]
    public void TheSeriesIsSentOnceForEachDateWhenItsDiscordAddressMoves_NotWhileItIsTheSameOne()
    {
        var e = Weekly();

        e.AutoLinks = Links("https://discord.com/events/1/2");
        var week1 = CalendarVRChatRequests.Fingerprint(e);

        // The date moves on and the old address is still the last known: no update.
        e.OccurrenceStartsAt = First.AddDays(7);
        Assert.Equal(week1, CalendarVRChatRequests.Fingerprint(e));

        // Discord made the new date's event: one update, with the new address.
        e.AutoLinks = Links("https://discord.com/events/1/3");
        Assert.NotEqual(week1, CalendarVRChatRequests.Fingerprint(e));
    }

    [Fact]
    public void ADateHeldApartIsNotSentAgainWhenTheDiscordAddressMoves()
    {
        var e = Weekly();
        var change = new CalendarDateChange { PlannedStartsAt = First.AddDays(14), Description = "Own: {{Discord event}}" };
        var plain = new CalendarDateChange { PlannedStartsAt = First.AddDays(21), StartsAt = First.AddDays(21).AddHours(1) };

        e.AutoLinks = Links("https://discord.com/events/1/2");
        var before = (CalendarVRChatRequests.DateFingerprint(e, change), CalendarVRChatRequests.DateFingerprint(e, plain));

        e.AutoLinks = Links("https://discord.com/events/1/3");
        var after = (CalendarVRChatRequests.DateFingerprint(e, change), CalendarVRChatRequests.DateFingerprint(e, plain));

        Assert.Equal(before, after);
        Assert.DoesNotContain("discord.com", CalendarVRChatRequests.UpdateDate(e, change).Description, StringComparison.Ordinal);
    }
}
