using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Fakes;
using VRChat.API.Model;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>
/// Calendar design §3.1: the copy a create with no answer made on VRChat's calendar, found again
/// although VRChat changed its title.
/// </summary>
public class CalendarVRChatMatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SentAt = Start.AddDays(-2);
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    private static CalendarEvent Event(string title = "Movie night – Alien.") => new()
    {
        Title = title,
        StartsAt = Start,
        EndsAt = Start + TwoHours,
        TimeZone = "UTC",
    };

    /// <summary>A row on VRChat's calendar, made <paramref name="madeAt"/> (after the create, unless a test says).</summary>
    private static global::VRChat.API.Model.CalendarEvent Row(
        string title = "Movie night  Alien․", DateTimeOffset? startsAt = null, DateTimeOffset? madeAt = null) =>
        FakeCalendar.Made("cal_copy", title, startsAt ?? Start, TwoHours, madeAt ?? SentAt.AddSeconds(1));

    [Theory]
    // VRChat on 2026-10-01: the en dash dropped, and "." turned into a look-alike dot (U+2024).
    [InlineData("Movie night – Alien.", "Movie night  Alien․")]
    [InlineData("Movie night – Alien.", "Movie night Alien")]
    [InlineData("Movie Night", "movie night")]
    [InlineData("  Quiz night ", "Quiz night")]
    public void TitlesVRChatChangedStillMatch(string sent, string saved) =>
        Assert.True(CalendarVRChatMatch.SameTitle(sent, saved));

    [Theory]
    [InlineData("Movie night", "Movie night 2")]
    [InlineData("Movie night", "Movie day")]
    public void DifferentTitlesDoNotMatch(string a, string b) =>
        Assert.False(CalendarVRChatMatch.SameTitle(a, b));

    [Fact]
    public void ATitleWithNoLettersOrDigitsIsComparedAsItIs()
    {
        Assert.Equal("🎉", CalendarVRChatMatch.PlainTitle(" 🎉 "));
        Assert.False(CalendarVRChatMatch.SameTitle("🎉", "🎃"));
    }

    [Fact]
    public void ARowWithTheSameTimesAndTitleMadeAfterTheCreateIsItsCopy() =>
        Assert.True(CalendarVRChatMatch.IsCopyOf(Row(), Event(), SentAt));

    [Fact]
    public void ARowMadeBeforeTheCreateWasSentIsNot()
    {
        var row = Row(madeAt: SentAt - CalendarVRChatMatch.ClockSlack - TimeSpan.FromMinutes(1));
        Assert.False(CalendarVRChatMatch.IsCopyOf(row, Event(), SentAt));

        // VRChat's clock a little behind Modbot's is allowed for.
        Assert.True(CalendarVRChatMatch.IsCopyOf(Row(madeAt: SentAt - TimeSpan.FromSeconds(30)), Event(), SentAt));
    }

    [Fact]
    public void ARowAtAnotherTimeOrWithAnotherTitleIsNot()
    {
        Assert.False(CalendarVRChatMatch.IsCopyOf(Row(startsAt: Start.AddHours(1)), Event(), SentAt));
        Assert.False(CalendarVRChatMatch.IsCopyOf(Row(title: "Quiz night"), Event(), SentAt));
    }

    [Fact]
    public void ADraftOrDeletedRowIsNot()
    {
        var draft = Row();
        draft.IsDraft = true;
        Assert.False(CalendarVRChatMatch.IsCopyOf(draft, Event(), SentAt));

        var deleted = Row();
        deleted.DeletedAt = SentAt.AddMinutes(1).UtcDateTime;
        Assert.False(CalendarVRChatMatch.IsCopyOf(deleted, Event(), SentAt));
    }

    [Fact]
    public void ADateOfAWeeklySeriesIsItsCopyWhenItIsOneOfTheEventsTimes()
    {
        var weekly = Event();
        weekly.Repeat = CalendarRepeats.Weekly;
        weekly.RepeatDays = ["SU"];

        var date = FakeCalendar.Made(
            "cal_date", "Movie night  Alien․", Start.AddDays(7), TwoHours, SentAt.AddSeconds(1),
            CalendarEventOccurrenceKind.Occurrence, seriesId: "cal_series");

        Assert.True(CalendarVRChatMatch.IsCopyOf(date, weekly, SentAt));
        Assert.Equal("cal_series", CalendarVRChatReader.KeyOf(date));

        var offDay = FakeCalendar.Made(
            "cal_off", "Movie night  Alien․", Start.AddDays(8), TwoHours, SentAt.AddSeconds(1),
            CalendarEventOccurrenceKind.Occurrence, seriesId: "cal_series");

        Assert.False(CalendarVRChatMatch.IsCopyOf(offDay, weekly, SentAt));
    }

    [Fact]
    public void TheCopyIsMatchedAgainstWhatTheCreateSent_NotTheEventAsEditedSince()
    {
        var e = Event();
        var place = new CalendarEventPlace { EventId = e.Id, Place = CalendarPlaces.VRChat, ErrorAt = SentAt };
        place.CreateSent = CalendarVRChatMatch.Remember(e);

        e.Title = "Quiz night";
        e.StartsAt = Start.AddHours(1);
        e.EndsAt = Start.AddHours(3);

        Assert.False(CalendarVRChatMatch.IsCopyOf(Row(), e, SentAt));

        var sent = CalendarVRChatMatch.AsSent(place, e);
        Assert.Equal("Movie night – Alien.", sent.Title);
        Assert.Equal(Start, sent.StartsAt);
        Assert.True(CalendarVRChatMatch.IsCopyOf(Row(), sent, SentAt));

        // Nothing kept (a place from before 2026-10-01): the event as it is now.
        Assert.Same(e, CalendarVRChatMatch.AsSent(new CalendarEventPlace(), e));
    }

    [Fact]
    public void AdoptingKeepsWhatWasSentAsSent_UnlessTheEventChangedSince()
    {
        var e = Event();
        e.UpdatedAt = SentAt.AddSeconds(-30);

        var place = new CalendarEventPlace
        {
            EventId = e.Id,
            Place = CalendarPlaces.VRChat,
            State = CalendarPlaceStates.Waiting,
            ErrorAt = SentAt,
        };

        CalendarVRChatMatch.Adopt(place, e, "cal_copy", SentAt.AddSeconds(1), SentAt.AddMinutes(2));

        Assert.Equal("cal_copy", place.ExternalId);
        Assert.Equal(CalendarPlaceStates.Published, place.State);
        Assert.Equal(CalendarVRChatRequests.Fingerprint(e), place.SentFingerprint);
        Assert.Null(place.ErrorAt);
        Assert.Null(place.CreateSent);

        // Edited after the create was sent: what VRChat has is older, so an update goes out.
        var edited = new CalendarEventPlace { EventId = e.Id, Place = CalendarPlaces.VRChat, ErrorAt = SentAt };
        e.UpdatedAt = SentAt.AddMinutes(1);
        CalendarVRChatMatch.Adopt(edited, e, "cal_copy", null, SentAt.AddMinutes(2));

        Assert.Null(edited.SentFingerprint);
    }
}
