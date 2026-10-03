using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>Google Calendar design §3.3: which events go to Google, and when Google counts as set up.</summary>
public class CalendarGoogleTests
{
    private static readonly DateTimeOffset Sep20 = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);

    private static Settings Ready() => new()
    {
        Id = 1,
        GoogleClientEmail = "modbot@test-project.iam.gserviceaccount.com",
        GoogleKeyId = "0123456789abcdef",
        GooglePrivateKeyEncrypted = "sealed",
        GoogleCalendarId = "c_events@group.calendar.google.com",
        GoogleCheckedAt = Sep20,
        GoogleCanChange = true,
        GoogleSendingOn = true,
    };

    private static CalendarEvent Event()
    {
        var e = CalendarRepeatTests.Event(Sep20, TimeSpan.FromHours(2));
        e.Visibility = "public";
        e.PublishToGoogle = true;
        return e;
    }

    [Fact]
    public void SetUpNeedsAKeyACalendarAndACheckThatFoundModbotMayChangeEvents()
    {
        Assert.True(CalendarGoogle.SetUp(Ready()));

        var notChecked = Ready();
        notChecked.GoogleCheckedAt = null;
        Assert.False(CalendarGoogle.SetUp(notChecked));

        var readOnly = Ready();
        readOnly.GoogleCanChange = false;
        Assert.False(CalendarGoogle.SetUp(readOnly));

        var refused = Ready();
        refused.GoogleProblem = "Google did not accept the key.";
        Assert.False(CalendarGoogle.SetUp(refused));

        var noKey = Ready();
        noKey.GooglePrivateKeyEncrypted = null;
        Assert.False(CalendarGoogle.SetUp(noKey));
    }

    [Fact]
    public void ReadyIsSetUpWithSendingOn_AndNotWhileRemoving()
    {
        var off = Ready();
        off.GoogleSendingOn = false;
        Assert.False(CalendarGoogle.Ready(off));

        var removing = Ready();
        removing.GoogleRemovingEvents = true;
        Assert.False(CalendarGoogle.Ready(removing));

        Assert.True(CalendarGoogle.Ready(Ready()));
    }

    [Fact]
    public void AMembersOnlyEventNeverGoes()
    {
        var group = Event();
        group.Visibility = "group";
        Assert.Equal(CalendarGoogleWants.Nothing, CalendarGoogle.WantsOf(group, Sep20));
        Assert.False(CalendarGoogle.Wants(group, Ready(), Sep20));

        var someRoles = Event();
        someRoles.VRChatRoleIds = ["grol_staff"];
        Assert.Equal(CalendarGoogleWants.Nothing, CalendarGoogle.WantsOf(someRoles, Sep20));
    }

    [Fact]
    public void ANewEventIsTickedOnlyWhenGoogleIsSetUpAndEveryoneMaySeeIt()
    {
        Assert.True(CalendarGoogle.TicksByDefault(Ready(), Event()));

        var group = Event();
        group.Visibility = "group";
        Assert.False(CalendarGoogle.TicksByDefault(Ready(), group));

        var notChecked = Ready();
        notChecked.GoogleCheckedAt = null;
        Assert.False(CalendarGoogle.TicksByDefault(notChecked, Event()));
    }

    [Fact]
    public void LiveEventsGo_FinishedOnesStay_DraftsAndDeletedOnesDoNot()
    {
        var e = Event();
        Assert.Equal(CalendarGoogleWants.There, CalendarGoogle.WantsOf(e, Sep20));

        e.State = CalendarEventStates.Finished;
        Assert.Equal(CalendarGoogleWants.KeptOnly, CalendarGoogle.WantsOf(e, Sep20.AddDays(30)));

        e.State = CalendarEventStates.Draft;
        Assert.Equal(CalendarGoogleWants.Nothing, CalendarGoogle.WantsOf(e, Sep20));

        e.State = CalendarEventStates.Scheduled;
        e.DeletedAt = Sep20;
        Assert.Equal(CalendarGoogleWants.Nothing, CalendarGoogle.WantsOf(e, Sep20));

        e.DeletedAt = null;
        e.PublishToGoogle = false;
        Assert.Equal(CalendarGoogleWants.Nothing, CalendarGoogle.WantsOf(e, Sep20));
    }

    [Fact]
    public void ACancelledEventStaysUntilADayAfterItsDateEnds()
    {
        var e = Event();
        e.State = CalendarEventStates.Cancelled;
        e.CancelledAt = Sep20.AddDays(-1);

        var until = e.EndsAt + CalendarGoogle.KeepCancelledFor;

        Assert.Equal(until, CalendarGoogle.KeptUntil(e));
        Assert.Equal(CalendarGoogleWants.KeptOnly, CalendarGoogle.WantsOf(e, until.AddMinutes(-1)));
        Assert.Equal(CalendarGoogleWants.Nothing, CalendarGoogle.WantsOf(e, until));
    }
}
