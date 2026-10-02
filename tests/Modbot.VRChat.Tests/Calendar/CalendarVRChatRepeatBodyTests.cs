using System.Globalization;
using System.Text.Json;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// Calendar design §3.1 (2026-10-02): VRChat's series carries the repeat's interval and its number of
/// times as VRChat's own <c>interval</c> and "after N times", and Featured as the form has it. Read
/// from the body as the SDK writes it, since that is what VRChat is sent.
/// </summary>
public class CalendarVRChatRepeatBodyTests
{
    // Thursday 1 October 2026.
    private static readonly DateTimeOffset Thursday = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Event(int every = 1, int? times = null, bool featured = false) => new()
    {
        Id = Guid.CreateVersion7(),
        Title = "Movie night",
        Description = "Bring snacks",
        StartsAt = Thursday,
        EndsAt = Thursday.AddHours(2),
        TimeZone = "UTC",
        Repeat = CalendarRepeats.Weekly,
        RepeatDays = ["TH"],
        RepeatEvery = every,
        RepeatTimes = times,
        Featured = featured,
        State = CalendarEventStates.Scheduled,
    };

    private static JsonElement Recurrence(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("recurrence").Clone();

    [Fact]
    public void ACreateCarriesTheIntervalAndTheNumberOfTimes()
    {
        var rule = Recurrence(CalendarVRChatRequests.Create(Event(every: 2, times: 4)).ToJson());

        Assert.Equal("weekly", rule.GetProperty("frequency").GetString());
        Assert.Equal(2, rule.GetProperty("interval").GetInt32());
        Assert.Equal("afterOccurrences", rule.GetProperty("end").GetProperty("type").GetString());
        Assert.Equal(4, rule.GetProperty("end").GetProperty("count").GetInt32());
        Assert.False(rule.GetProperty("end").TryGetProperty("date", out _));
    }

    [Fact]
    public void AnUpdateCarriesTheIntervalToo()
    {
        var rule = Recurrence(CalendarVRChatRequests.Update(Event(every: 3)).ToJson());

        Assert.Equal(3, rule.GetProperty("interval").GetInt32());
        Assert.False(rule.TryGetProperty("end", out _));
    }

    [Fact]
    public void ASeriesSentFromALaterDateCountsOnlyTheDatesLeft()
    {
        // Six times every other week; the third date (four weeks on) is the one Modbot is on now.
        // The series VRChat is sent starts there, so it has four dates left.
        var e = Event(every: 2, times: 6);
        e.OccurrenceStartsAt = Thursday.AddDays(28);

        using var body = JsonDocument.Parse(CalendarVRChatRequests.Update(e).ToJson());
        var rule = body.RootElement.GetProperty("recurrence");

        Assert.Equal(
            Thursday.AddDays(28).UtcDateTime,
            DateTime.Parse(body.RootElement.GetProperty("startsAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
        Assert.Equal(4, rule.GetProperty("end").GetProperty("count").GetInt32());
    }

    [Fact]
    public void ALastDateIsStillSentAsADate()
    {
        var e = Event();
        e.RepeatUntil = new DateOnly(2026, 12, 31);

        var end = Recurrence(CalendarVRChatRequests.Create(e).ToJson()).GetProperty("end");

        Assert.Equal("afterDate", end.GetProperty("type").GetString());
        Assert.Equal("2026-12-31T23:59:59", end.GetProperty("date").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FeaturedIsSentAsTheFormHasIt(bool featured)
    {
        var e = Event(featured: featured);
        var change = new CalendarDateChange
        {
            Id = Guid.CreateVersion7(),
            EventId = e.Id,
            PlannedStartsAt = Thursday.AddDays(7),
            Title = "Week two",
        };

        foreach (var json in new[]
                 {
                     CalendarVRChatRequests.Create(e).ToJson(),
                     CalendarVRChatRequests.Update(e).ToJson(),
                     CalendarVRChatRequests.UpdateDate(e, change).ToJson(),
                 })
        {
            using var body = JsonDocument.Parse(json);
            Assert.Equal(featured, body.RootElement.GetProperty("featured").GetBoolean());
        }
    }

    [Fact]
    public void AnEventThatUsesNoneOfThemKeepsTheFingerprintItWasPublishedWith()
    {
        // Written out the way it was before 2026-10-02, so a deploy does not send every event again.
        var e = Event();

        var before = CalendarFingerprint.Of(
            e.Title, e.Description, e.StartsAt, e.EndsAt, e.TimeZone, e.Repeat, e.RepeatDays, e.RepeatUntil?.ToString("O", CultureInfo.InvariantCulture),
            e.Category, e.Languages, e.Platforms, e.Tags, e.Visibility, e.VRChatImageId, e.NotifyMembers);

        Assert.Equal(before, CalendarVRChatRequests.Fingerprint(e));
    }

    [Fact]
    public void EachNewSettingChangesTheFingerprint()
    {
        var plain = CalendarVRChatRequests.Fingerprint(Event());

        Assert.NotEqual(plain, CalendarVRChatRequests.Fingerprint(Event(every: 2)));
        Assert.NotEqual(plain, CalendarVRChatRequests.Fingerprint(Event(times: 4)));
        Assert.NotEqual(plain, CalendarVRChatRequests.Fingerprint(Event(featured: true)));
        Assert.NotEqual(
            CalendarVRChatRequests.Fingerprint(Event(times: 4)),
            CalendarVRChatRequests.Fingerprint(Event(times: 5)));
    }
}
