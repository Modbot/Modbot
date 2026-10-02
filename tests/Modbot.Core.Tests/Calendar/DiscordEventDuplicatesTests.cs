using Modbot.Core.Calendar;
using Modbot.Core.Discord;

namespace Modbot.Core.Tests.Calendar;

/// <summary>
/// Calendar design §16 (2026-10-02): Discord server events that look like copies -- the same or
/// nearly the same title, starting close together -- whoever made them.
/// </summary>
public class DiscordEventDuplicatesTests
{
    private static readonly DateTimeOffset Friday = new(2026, 10, 2, 19, 0, 0, TimeSpan.Zero);

    private static DiscordServerEvent Made(
        string id, string name, DateTimeOffset startsAt, string madeBy = DiscordEventMakers.Bot, string? botName = "Some bot") =>
        new(id, name, startsAt, startsAt.AddHours(2), false, madeBy, botName);

    [Theory]
    [InlineData("Sleepy Hollow Watch Party", "sleepyhollowwatchparty")]
    [InlineData("  SLEEPY hollow -- watch party!!! ", "sleepyhollowwatchparty")]
    [InlineData("🎃 Sleepy Hollow Watch Party 🎃", "sleepyhollowwatchparty")]
    [InlineData("[VRChat, Group Public] Sleepy Hollow Watch Party", "sleepyhollowwatchparty")]
    [InlineData("[Event] [VRChat] Sleepy Hollow Watch Party", "sleepyhollowwatchparty")]
    [InlineData("Sleepy Hollow Watch Party (18+)", "sleepyhollowwatchparty18")]
    [InlineData("Game Night (Among Us)", "gamenightamongus")]
    [InlineData("Café night", "cafenight")]
    [InlineData("𝐌𝐨𝐯𝐢𝐞 𝐧𝐢𝐠𝐡𝐭", "movienight")]
    [InlineData("Ｍｏｖｉｅ　ｎｉｇｈｔ", "movienight")]
    public void ATitleIsComparedByItsLettersAndDigitsOnly(string title, string plain)
    {
        Assert.Equal(plain, DiscordEventDuplicates.Plain(title));
    }

    [Fact]
    public void ATitleThatIsAllBrackets_KeepsWhatIsInThem()
    {
        Assert.Equal("vrchatmeetup", DiscordEventDuplicates.Plain("[VRChat Meetup]"));
        Assert.Equal(string.Empty, DiscordEventDuplicates.Plain("🎃🎃"));
        Assert.Equal(string.Empty, DiscordEventDuplicates.Plain(null));
    }

    [Fact]
    public void NearlyTheSameTitle_IsTheSame_ButAShortOneInsideALongOneIsNot()
    {
        Assert.True(DiscordEventDuplicates.SameTitle("Ichabod and Mr. Toad", "ICHABOD AND MR TOAD 🐸"));

        // One holds the other whole, and the shorter is long enough to mean something.
        Assert.True(DiscordEventDuplicates.SameTitle("Movie night", "Movie night at the cinema"));

        // "Art" is in "Party": too short to count.
        Assert.False(DiscordEventDuplicates.SameTitle("Art", "Watch party"));

        Assert.False(DiscordEventDuplicates.SameTitle("Movie night", "Karaoke"));

        // A bracket after the words says which event it is: two different games on one night.
        Assert.False(DiscordEventDuplicates.SameTitle("Game Night (Among Us)", "Game Night (Minecraft)"));

        // A tag in front is only where the title was posted from.
        Assert.True(DiscordEventDuplicates.SameTitle("[Event] Movie Night", "Movie Night"));

        // A title of nothing but emoji matches nothing, not every other one.
        Assert.False(DiscordEventDuplicates.SameTitle("🎃", "🎃"));
    }

    [Fact]
    public void CopiesStartWithinTheWindow_AndNotOneMinuteOutside_It()
    {
        var first = Made("1", "Movie night", Friday);

        Assert.True(DiscordEventDuplicates.AreCopies(first, Made("2", "Movie night", Friday + DiscordEventDuplicates.StartsWithin)));
        Assert.True(DiscordEventDuplicates.AreCopies(first, Made("3", "Movie night", Friday - DiscordEventDuplicates.StartsWithin)));
        Assert.False(DiscordEventDuplicates.AreCopies(
            first, Made("4", "Movie night", Friday + DiscordEventDuplicates.StartsWithin + TimeSpan.FromMinutes(1))));

        // The same event a week later is the next time, not a copy.
        Assert.False(DiscordEventDuplicates.AreCopies(first, Made("5", "Movie night", Friday.AddDays(7))));
    }

    [Fact]
    public void CopiesAreFound_WhoeverMadeThem()
    {
        var events = new[]
        {
            Made("10", "[VRChat, Group Public] Sleepy Hollow Watch Party", Friday, DiscordEventMakers.Bot, "ChronicleBot"),
            Made("11", "Sleepy Hollow Watch Party", Friday.AddMinutes(5), DiscordEventMakers.Bot, "VRC-Watchdog"),
            Made("12", "sleepy hollow watch party 🎃", Friday, DiscordEventMakers.Person, null),
            Made("13", "Sleepy Hollow Watch Party", Friday, DiscordEventMakers.Modbot, "Modbot"),
            Made("20", "Karaoke", Friday, DiscordEventMakers.Bot, "VRC-Watchdog"),
        };

        var found = Assert.Single(DiscordEventDuplicates.Find(events));

        Assert.Equal(["10", "12", "13", "11"], found.Select(e => e.Id));
        Assert.Contains(found, e => e.MadeBy == DiscordEventMakers.Modbot);
        Assert.Contains(found, e => e.MadeBy == DiscordEventMakers.Person);
    }

    [Fact]
    public void EachSetOfCopiesIsItsOwnGroup_SoonestFirst_AndAnEventWithNoCopyIsLeftOut()
    {
        var events = new[]
        {
            Made("1", "Ichabod and Mr. Toad", Friday.AddDays(1)),
            Made("2", "Sleepy Hollow Watch Party", Friday),
            Made("3", "Ichabod and Mr Toad", Friday.AddDays(1).AddMinutes(10)),
            Made("4", "Sleepy Hollow Watch Party", Friday),
            Made("5", "Friday Night Fun", Friday.AddDays(2)),
        };

        var found = DiscordEventDuplicates.Find(events);

        Assert.Equal(2, found.Count);
        Assert.Equal(["2", "4"], found[0].Select(e => e.Id));
        Assert.Equal(["1", "3"], found[1].Select(e => e.Id));
    }

    [Fact]
    public void TheSameEventListedTwice_IsNotItsOwnCopy()
    {
        var once = Made("1", "Movie night", Friday);

        Assert.Empty(DiscordEventDuplicates.Find([once, once]));
    }
}
