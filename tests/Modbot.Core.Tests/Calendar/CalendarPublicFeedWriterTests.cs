using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// Calendar design §6.1: the public feed holds only events visible to everyone, and of those only
/// what the secret feed holds less what is for members.
/// </summary>
public class CalendarPublicFeedWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private const string Address = "https://modbot.example";

    private static CalendarEvent Event(
        string title, string visibility = "public", string access = "public", string state = CalendarEventStates.Scheduled) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = "Come along",
            StartsAt = Now.AddDays(2),
            EndsAt = Now.AddDays(2).AddHours(2),
            TimeZone = "UTC",
            WorldId = "wrld_1",
            Visibility = visibility,
            AccessType = access,
            State = state,
            Version = 1,
            UpdatedAt = Now,
        };

    private static string Write(params CalendarEvent[] events) =>
        CalendarFeedWriter.WritePublic(
            "Night Owls", events, new Dictionary<string, string> { ["wrld_1"] = "The Black Cat" }, Now, Address);

    [Fact]
    public void APublicEventIsInIt_WithItsTitleTimeAndWorld()
    {
        var open = Event("Movie night");

        var feed = Write(open);

        Assert.Contains($"UID:{open.Id:D}@modbot", feed, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Movie night", feed, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:Come along", feed, StringComparison.Ordinal);
        Assert.Contains("LOCATION:The Black Cat", feed, StringComparison.Ordinal);
        Assert.Contains("X-WR-CALNAME:Night Owls", feed, StringComparison.Ordinal);
    }

    [Fact]
    public void AMembersOnlyEventIsLeftOut_EvenWhenTheCallerPassesIt()
    {
        var members = Event("Members' meeting", visibility: "group");
        var open = Event("Movie night");

        var feed = Write(members, open);

        Assert.DoesNotContain(members.Id.ToString("D"), feed, StringComparison.Ordinal);
        Assert.DoesNotContain("Members' meeting", feed, StringComparison.Ordinal);
        Assert.Contains($"UID:{open.Id:D}@modbot", feed, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventVRChatShowsOnlyToSomeRolesIsLeftOut()
    {
        var roles = Event("Staff social");
        roles.VRChatRoleIds = ["grol_staff"];

        Assert.False(CalendarFeedWriter.IsPublic(roles));
        Assert.DoesNotContain("Staff social", Write(roles), StringComparison.Ordinal);
    }

    [Fact]
    public void DraftsAndDeletedEventsDoNotBelong()
    {
        var draft = Event("Draft", state: CalendarEventStates.Draft);
        var deleted = Event("Deleted");
        deleted.DeletedAt = Now;

        Assert.False(CalendarFeedWriter.BelongsInPublic(draft, Now));
        Assert.False(CalendarFeedWriter.BelongsInPublic(deleted, Now));
        Assert.True(CalendarFeedWriter.BelongsInPublic(Event("Scheduled"), Now));
    }

    [Theory]
    [InlineData("public", true)]
    [InlineData("plus", false)]
    [InlineData("members", false)]
    public void TheJoinLinkIsOnlyForAnEventAnyoneCanJoin(string access, bool linked)
    {
        var e = Event("Movie night", access: access);

        var feed = Write(e);
        var join = $"URL:{Address}/api/calendar/join/{e.Id:D}";

        Assert.Equal(linked, feed.Contains(join, StringComparison.Ordinal));
    }

    [Fact]
    public void TheLinkToModbotsCalendarPageIsLeftOut()
    {
        var e = Event("Movie night", access: "members");

        var feed = Write(e);

        Assert.DoesNotContain("/calendar?event=", feed, StringComparison.Ordinal);
        Assert.DoesNotContain("URL:", feed, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAPublicAddressThereIsNoJoinLink()
    {
        var e = Event("Movie night");

        Assert.Null(CalendarFeedWriter.JoinLink(null, e));
        Assert.Null(CalendarFeedWriter.JoinLink("  ", e));
        Assert.Equal($"{Address}/api/calendar/join/{e.Id:D}", CalendarFeedWriter.JoinLink(Address + "/", e));
    }

    [Fact]
    public void ACancelledPublicEventStaysAsCancelled()
    {
        var cancelled = Event("Cancelled", state: CalendarEventStates.Cancelled);
        cancelled.CancelledAt = Now.AddDays(-1);

        Assert.True(CalendarFeedWriter.BelongsInPublic(cancelled, Now));
        Assert.Contains("STATUS:CANCELLED", Write(cancelled), StringComparison.Ordinal);
    }
}
