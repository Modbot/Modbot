using Modbot.Core.Data.Entities;
using Modbot.VRChat.Calendar;
using Modbot.VRChat.Tests.Fakes;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;

namespace Modbot.VRChat.Tests.Calendar;

/// <summary>VRChat's own title and description on an event (calendar design, per-site text).</summary>
public class CalendarVRChatSiteTextTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 26, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TwoHours = TimeSpan.FromHours(2);

    private static CalendarEvent Event() => new()
    {
        Title = "Thy Kingdom-No Kids Hangout",
        Description = "Come along",
        StartsAt = Start,
        EndsAt = Start + TwoHours,
        TimeZone = "UTC",
    };

    [Fact]
    public void ACreateAndAnUpdateSendVRChatsOwnWords()
    {
        var e = Event();
        e.VRChatTitle = "18+ Hangout w/ Thy Kingdom";
        e.VRChatDescription = "Plain words for VRChat";

        var create = CalendarVRChatRequests.Create(e);
        var update = CalendarVRChatRequests.Update(e);

        Assert.Equal("18+ Hangout w/ Thy Kingdom", create.Title);
        Assert.Equal("Plain words for VRChat", create.Description);
        Assert.Equal("18+ Hangout w/ Thy Kingdom", update.Title);
        Assert.Equal("Plain words for VRChat", update.Description);
    }

    [Fact]
    public void WithNoOwnWordsTheSharedOnesAreSent()
    {
        var e = Event();

        Assert.Equal("Thy Kingdom-No Kids Hangout", CalendarVRChatRequests.Create(e).Title);
        Assert.Equal("Come along", CalendarVRChatRequests.Update(e).Description);
    }

    [Fact]
    public void OnlyVRChatsWordsChangeVRChatsFingerprint()
    {
        var e = Event();
        var before = CalendarVRChatRequests.Fingerprint(e);

        // Discord's and Google's words are not VRChat's: no write to VRChat.
        e.DiscordTitle = "Thy Kingdom-No Kids Hangout [VRChat, Group Public]";
        e.GoogleDescription = "[Group Link](https://vrc.group/KINGDOM.1234)";
        Assert.Equal(before, CalendarVRChatRequests.Fingerprint(e));

        e.VRChatTitle = "18+ Hangout w/ Thy Kingdom";
        Assert.NotEqual(before, CalendarVRChatRequests.Fingerprint(e));
    }

    [Fact]
    public void ChangingTheSharedTitleDoesNotRewriteAnEventWithItsOwnVRChatTitle()
    {
        var e = Event();
        e.VRChatTitle = "18+ Hangout w/ Thy Kingdom";
        var before = CalendarVRChatRequests.Fingerprint(e);

        e.Title = "A new shared title";

        Assert.Equal(before, CalendarVRChatRequests.Fingerprint(e));
    }

    [Fact]
    public void VRChatsOwnDescriptionAnswersTheDescriptionVRChatNeeds()
    {
        var e = Event();
        e.Description = string.Empty;
        Assert.Contains(CalendarVRChatChecks.NoDescription, CalendarVRChatChecks.FieldProblems(e));

        e.VRChatDescription = "Only on VRChat";
        Assert.DoesNotContain(CalendarVRChatChecks.NoDescription, CalendarVRChatChecks.FieldProblems(e));
    }

    [Fact]
    public void ACreateWithNoAnswerIsFoundByVRChatsTitle()
    {
        var e = Event();
        e.VRChatTitle = "18+ Hangout w/ Thy Kingdom";

        var row = FakeCalendar.Made("cal_copy", "18+ Hangout w/ Thy Kingdom", Start, TwoHours, Start.AddHours(-47));

        Assert.True(CalendarVRChatMatch.IsCopyOf(row, e, Start.AddHours(-48)));

        // Remembered with the create, so an edit since is not what the copy is matched against.
        var place = new CalendarEventPlace { CreateSent = CalendarVRChatMatch.Remember(e) };
        e.VRChatTitle = "Changed after the create";
        var sent = CalendarVRChatMatch.AsSent(place, e);

        Assert.Equal("18+ Hangout w/ Thy Kingdom", sent.VRChatTitle);
        Assert.True(CalendarVRChatMatch.IsCopyOf(row, sent, Start.AddHours(-48)));
    }

    [Fact]
    public void AChangeReadFromVRChatGoesToVRChatsOwnWordsWhenItHasThem()
    {
        var e = Event();
        e.MadeOnVRChat = true;
        e.VRChatTitle = "18+ Hangout w/ Thy Kingdom";

        var source = FakeCalendar.Made("cal_copy", "Edited on vrchat.com", Start, TwoHours, Start.AddHours(-47));
        source.Description = "New words";

        CalendarVRChatCopy.Onto(e, source);

        // The shared title Discord and Google say is left alone; the description had no own text.
        Assert.Equal("Edited on vrchat.com", e.VRChatTitle);
        Assert.Equal("Thy Kingdom-No Kids Hangout", e.Title);
        Assert.Equal("New words", e.Description);
        Assert.Null(e.VRChatDescription);
    }
}
