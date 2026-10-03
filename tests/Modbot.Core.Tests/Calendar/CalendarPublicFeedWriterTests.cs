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
            PublishToVRChat = true,
            Visibility = visibility,
            AccessType = access,
            State = state,
            Version = 1,
            UpdatedAt = Now,
        };

    /// <summary>Lines longer than 75 bytes are folded onto the next (RFC 5545 §3.1); this puts them back.</summary>
    private static string Unfold(string feed) => feed.Replace("\r\n ", string.Empty, StringComparison.Ordinal);

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
    public void AnEventNotGoingToVRChatIsLeftOut_WhateverItsHiddenVisibilitySays()
    {
        // Set to Everyone with the VRChat chip on, then made Discord-only: the form hides "Visible
        // to" and leaves the word as it was.
        var discordOnly = Event("Discord-only night");
        discordOnly.PublishToVRChat = false;

        Assert.False(CalendarFeedWriter.IsPublic(discordOnly));
        Assert.False(CalendarFeedWriter.BelongsInPublic(discordOnly, Now));
        Assert.DoesNotContain("Discord-only night", Write(discordOnly), StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedDateOfAPublicSeriesCarriesNothingTheSecretFeedLeavesOut()
    {
        var series = WeeklyWithAChangedDate(access: "members");

        var publicFeed = Write(series);

        // With no address the secret feed has no link either: everything else is the same, line for
        // line, the changed date's own VEVENT included.
        var secret = CalendarFeedWriter.Write(
            "Night Owls", [series], new Dictionary<string, string> { ["wrld_1"] = "The Black Cat" }, Now);

        Assert.Equal(secret, publicFeed);
        Assert.Contains("RECURRENCE-ID:", publicFeed, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Movie night: the finale", publicFeed, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedDateOfASeriesAnyoneCanJoinCarriesOnlyTheJoinLinkMore()
    {
        var series = WeeklyWithAChangedDate(access: "public");

        var lines = Unfold(Write(series)).Split("\r\n");
        var secret = Unfold(CalendarFeedWriter.Write(
            "Night Owls", [series], new Dictionary<string, string> { ["wrld_1"] = "The Black Cat" }, Now)).Split("\r\n");

        var extra = lines.Where(l => l.StartsWith("URL:", StringComparison.Ordinal)).ToList();

        // One link on the series and one on its changed date, both the join link.
        Assert.Equal(2, extra.Count);
        Assert.All(extra, l => Assert.Equal($"URL:{Address}/api/calendar/join/{series.Id:D}", l));
        Assert.Equal(secret, lines.Where(l => !l.StartsWith("URL:", StringComparison.Ordinal)));
    }

    private static CalendarEvent WeeklyWithAChangedDate(string access)
    {
        var series = Event("Movie night", access: access);
        series.Repeat = CalendarRepeats.Weekly;
        series.RepeatTimes = 4;

        var planned = series.StartsAt.AddDays(7);
        series.DateChanges.Add(new CalendarDateChange
        {
            Id = Guid.NewGuid(),
            EventId = series.Id,
            PlannedStartsAt = planned,
            StartsAt = planned.AddHours(1),
            EndsAt = planned.AddHours(3),
            Title = "Movie night: the finale",
            Description = "The last one",
            CreatedAt = Now,
            UpdatedAt = Now,
        });

        return series;
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

        var feed = Unfold(Write(e));
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
