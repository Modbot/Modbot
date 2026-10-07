using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Twitch;

namespace Modbot.Core.Tests.Twitch;

/// <summary>
/// The Twitch feature's pure rules (Twitch design): which text is a channel, how the post template
/// is filled, when a "live" post is made (decision 2), which event a stream is linked to (step 3),
/// and how late a "live" post may go (decision 3).
/// </summary>
public class TwitchRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 19, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("ourgroup", "ourgroup")]
    [InlineData("OurGroup", "ourgroup")]
    [InlineData("@ourgroup", "ourgroup")]
    [InlineData("  ourgroup  ", "ourgroup")]
    [InlineData("twitch.tv/ourgroup", "ourgroup")]
    [InlineData("https://www.twitch.tv/OurGroup", "ourgroup")]
    [InlineData("https://twitch.tv/ourgroup/schedule", "ourgroup")]
    [InlineData("our_group_1", "our_group_1")]
    public void ALoginIsReadFromWhatWasTyped(string typed, string login)
    {
        Assert.Equal(login, TwitchRules.LoginOf(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("our group")]
    [InlineData("our-group")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    [InlineData("https://example.com/ourgroup")]
    [InlineData("https://nottwitch.tv.evil.test/ourgroup")]
    [InlineData("ftp://twitch.tv/ourgroup")]
    [InlineData("../users")]
    public void SomethingThatIsNotAChannelIsRefused(string? typed)
    {
        Assert.Null(TwitchRules.LoginOf(typed));
    }

    [Fact]
    public void TheChannelLinkIsThePageOnTwitch()
    {
        Assert.Equal("https://www.twitch.tv/ourgroup", TwitchRules.ChannelLink("ourgroup"));
    }

    [Fact]
    public void TheTemplateIsFilledWithTheStreamsTitleCategoryAndLink()
    {
        var text = TwitchRules.Fill(
            "{title} ({category})\n{link} {other}", "Movie night", "VRChat", "https://www.twitch.tv/ourgroup", 8000);

        Assert.Equal("Movie night (VRChat)\nhttps://www.twitch.tv/ourgroup {other}", text);
    }

    [Fact]
    public void AMissingTitleOrCategoryFillsAsNothing()
    {
        Assert.Equal("[][]", TwitchRules.Fill("[{title}][{category}]", null, null, "x", 100));
    }

    [Fact]
    public void FilledTextIsCutToTheLimit_NotInTheMiddleOfAPair()
    {
        // A pair of UTF-16 units is one character; cutting between them leaves half a character.
        var cut = TwitchRules.Cut("ab\U0001F600cd", 3);

        Assert.Equal("ab", cut);
        Assert.Equal("abc", TwitchRules.Cut("abc", 3));
    }

    [Fact]
    public void ThePostIsMadeOnceTheStreamHasBeenLiveLongEnough()
    {
        Assert.Equal(TwitchPostDecision.Wait, Decide(now: Start.AddMinutes(2)));
        Assert.Equal(TwitchPostDecision.Make, Decide(now: Start.AddMinutes(3)));
        Assert.Equal(TwitchPostDecision.Make, Decide(now: Start.AddMinutes(4)));
    }

    [Theory]
    [InlineData("rerun")]
    [InlineData("playlist")]
    [InlineData("watch_party")]
    [InlineData("premiere")]
    [InlineData("")]
    public void OnlyALiveStreamPosts(string type)
    {
        Assert.Equal(TwitchPostDecision.NotLive, Decide(type: type, now: Start.AddMinutes(10)));
    }

    [Fact]
    public void ASecondPostDoesNotComeInsideTheCoolDown()
    {
        Assert.Equal(
            TwitchPostDecision.WithinCoolDown,
            Decide(now: Start.AddMinutes(5), lastPostAt: Start.AddMinutes(5) - TimeSpan.FromHours(5.9)));

        Assert.Equal(
            TwitchPostDecision.Make,
            Decide(now: Start.AddMinutes(5), lastPostAt: Start.AddMinutes(5) - TimeSpan.FromHours(6)));
    }

    [Fact]
    public void ThePostNeedsASiteTicked()
    {
        Assert.Equal(TwitchPostDecision.NoSite, Decide(now: Start.AddMinutes(5), anySiteTicked: false));
    }

    [Fact]
    public void AStreamFoundLongAfterItStartedDoesNotPost()
    {
        // The poll first saw it 40 minutes in: "we're live" would be old news.
        Assert.Equal(
            TwitchPostDecision.SeenTooLate,
            Decide(now: Start.AddMinutes(40), firstSeenAt: Start.AddMinutes(40)));

        // Seen within the 15 minutes a live post may be late: still posts.
        Assert.Equal(
            TwitchPostDecision.Make,
            Decide(now: Start.AddMinutes(14), firstSeenAt: Start.AddMinutes(14)));
    }

    [Fact]
    public void TheMinimumAndTheCoolDownAreHeldToTheirRanges()
    {
        Assert.Equal(1, TwitchRules.ClampAfterMinutes(0));
        Assert.Equal(60, TwitchRules.ClampAfterMinutes(600));
        Assert.Equal(1, TwitchRules.ClampEveryHours(-3));
        Assert.Equal(168, TwitchRules.ClampEveryHours(10_000));
    }

    private static TwitchPostDecision Decide(
        string type = "live",
        DateTimeOffset? now = null,
        DateTimeOffset? firstSeenAt = null,
        DateTimeOffset? lastPostAt = null,
        bool anySiteTicked = true) =>
        TwitchRules.Decide(type, Start, firstSeenAt ?? Start.AddMinutes(1), now ?? Start, 3, 6, lastPostAt, anySiteTicked);

    // ── Linking a stream to an event (step 3) ─────────────────────────────────────────────

    private static CalendarEvent EventAt(
        DateTimeOffset starts,
        TimeSpan length,
        string state = CalendarEventStates.Scheduled,
        string repeat = CalendarRepeats.None) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Movie night",
        StartsAt = starts,
        EndsAt = starts + length,
        TimeZone = "UTC",
        Repeat = repeat,
        State = state,
    };

    [Fact]
    public void AStreamIsLinkedToTheEventThatWasOn()
    {
        var running = EventAt(Start.AddMinutes(-30), TimeSpan.FromHours(2));
        var other = EventAt(Start.AddDays(2), TimeSpan.FromHours(2));

        Assert.Equal(running.Id, TwitchRules.ChooseEvent([running, other], Start));
    }

    [Fact]
    public void AnEventStartingWithinTheHourAfterIsTheOneToo()
    {
        var soon = EventAt(Start.AddMinutes(45), TimeSpan.FromHours(2));
        var later = EventAt(Start.AddMinutes(90), TimeSpan.FromHours(2));

        Assert.Equal(soon.Id, TwitchRules.ChooseEvent([soon, later], Start));
    }

    [Fact]
    public void NothingIsLinkedWhenNoEventWasOn()
    {
        var over = EventAt(Start.AddHours(-5), TimeSpan.FromHours(2));
        var tomorrow = EventAt(Start.AddDays(1), TimeSpan.FromHours(2));

        Assert.Null(TwitchRules.ChooseEvent([over, tomorrow], Start));
        Assert.Null(TwitchRules.ChooseEvent([], Start));
    }

    [Fact]
    public void OverlappingEventsLeaveTheStreamUnlinked()
    {
        var first = EventAt(Start.AddMinutes(-30), TimeSpan.FromHours(2));
        var second = EventAt(Start, TimeSpan.FromHours(1));

        Assert.Null(TwitchRules.ChooseEvent([first, second], Start));
    }

    [Theory]
    [InlineData(CalendarEventStates.Draft)]
    [InlineData(CalendarEventStates.Cancelled)]
    public void AnEventThatNeverRanIsNotLinked(string state)
    {
        var never = EventAt(Start.AddMinutes(-30), TimeSpan.FromHours(2), state);

        Assert.Null(TwitchRules.ChooseEvent([never], Start));
    }

    [Fact]
    public void ADeletedEventIsNotLinked()
    {
        var gone = EventAt(Start.AddMinutes(-30), TimeSpan.FromHours(2));
        gone.DeletedAt = Start.AddDays(-1);

        Assert.Null(TwitchRules.ChooseEvent([gone], Start));
    }

    [Fact]
    public void ADateOfARepeatingEventIsFoundByItsRule()
    {
        // The first date was weeks ago; the date on this week's Wednesday is the one on now.
        var weekly = EventAt(Start.AddDays(-21).AddMinutes(-30), TimeSpan.FromHours(2), repeat: CalendarRepeats.Weekly);

        Assert.Equal(weekly.Id, TwitchRules.ChooseEvent([weekly], Start));
    }

    [Fact]
    public void ACancelledDateOfARepeatingEventIsNotLinked()
    {
        var planned = Start.AddDays(-21).AddMinutes(-30);
        var weekly = EventAt(planned, TimeSpan.FromHours(2), repeat: CalendarRepeats.Weekly);
        weekly.DateChanges.Add(new CalendarDateChange
        {
            Id = Guid.NewGuid(),
            EventId = weekly.Id,
            PlannedStartsAt = planned.AddDays(21),
            Cancelled = true,
        });

        Assert.Null(TwitchRules.ChooseEvent([weekly], Start));
    }

    // ── A "live" post is stale sooner (decision 3) ────────────────────────────────────────

    [Fact]
    public void ALivePostMoreThanFifteenMinutesLateIsLate_AnyOtherPostWaitsAnHour()
    {
        var live = Scheduled(PostKinds.TwitchLive);
        var other = Scheduled(null);

        var inTwentyMinutes = Start.AddMinutes(20);

        Assert.True(PostRules.IsLate(live.Post, live.Destination, inTwentyMinutes));
        Assert.False(PostRules.IsLate(other.Post, other.Destination, inTwentyMinutes));
        Assert.False(PostRules.IsLate(live.Post, live.Destination, Start.AddMinutes(15)));
        Assert.Equal(TimeSpan.FromMinutes(15), PostRules.LateLimitFor(live.Post));
        Assert.Equal(PostRules.LateLimit, PostRules.LateLimitFor(other.Post));
    }

    private static (Post Post, PostDestination Destination) Scheduled(string? kind)
    {
        var destination = new PostDestination
        {
            Id = Guid.NewGuid(),
            Network = PostNetworks.Discord,
            Target = "222",
            State = PostDestinationStates.Waiting,
            UpdatedAt = Start,
        };

        var post = new Post
        {
            Id = Guid.NewGuid(),
            Text = "We're live",
            Status = PostStatuses.Scheduled,
            SendAt = Start,
            Kind = kind,
            Destinations = [destination],
        };

        destination.PostId = post.Id;
        return (post, destination);
    }

    // ── Where it goes ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void NothingIsTickedToStart()
    {
        Assert.False(new TwitchPostPlaces().AnyTicked);
        Assert.False(TwitchPostPlaces.Parse(null).AnyTicked);
        Assert.False(TwitchPostPlaces.Parse("{}").AnyTicked);
        Assert.False(TwitchPostPlaces.Parse("not json").AnyTicked);
    }

    [Fact]
    public void TheSitesTickedAreKeptWithTheirOwnChoices()
    {
        var places = new TwitchPostPlaces(
            new TwitchDiscordPlace("222", "333", Publish: true),
            new TwitchVRChatPlace(VRChatPostVisibilities.Everyone, ["grol_1"], Notify: true),
            Bluesky: true);

        var read = TwitchPostPlaces.Parse(places.Write());

        Assert.True(read.AnyTicked);
        Assert.Equal("222", read.Discord!.ChannelId);
        Assert.Equal("333", read.Discord.RoleId);
        Assert.True(read.Discord.Publish);
        Assert.Equal("public", read.VRChat!.Visibility);
        Assert.Equal(["grol_1"], read.VRChat.RoleIds);
        Assert.True(read.VRChat.Notify);
        Assert.True(read.Bluesky);
    }

    [Fact]
    public void OneSiteTickedAloneLeavesTheOthersUnticked()
    {
        var read = TwitchPostPlaces.Parse(new TwitchPostPlaces(Bluesky: true).Write());

        Assert.Null(read.Discord);
        Assert.Null(read.VRChat);
        Assert.True(read.Bluesky);
    }

    // ── Settings ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwitchIsSetUpOnlyOverACheckThatPassed()
    {
        var settings = new Settings
        {
            TwitchClientId = FakeId,
            TwitchClientSecretEncrypted = "x",
            TwitchChannelLogin = "ourgroup",
            TwitchChannelId = "1",
            TwitchCheckedAt = Start,
        };

        Assert.True(TwitchRules.SetUp(settings));

        settings.TwitchProblem = "Twitch did not accept the client id and secret.";
        Assert.False(TwitchRules.SetUp(settings));

        settings.TwitchProblem = null;
        settings.TwitchCheckedAt = null;
        Assert.False(TwitchRules.SetUp(settings));

        Assert.False(TwitchRules.SetUp(null));
    }

    [Fact]
    public void TwitchIsStoppedUntilTheLimitHasPassed()
    {
        var settings = new Settings { TwitchStoppedUntil = Start.AddMinutes(10) };

        Assert.True(TwitchRules.Stopped(settings, Start));
        Assert.True(TwitchRules.Stopped(settings, Start.AddMinutes(9)));
        Assert.False(TwitchRules.Stopped(settings, Start.AddMinutes(10)));
        Assert.False(TwitchRules.Stopped(new Settings(), Start));
    }

    private const string FakeId = "abcdefghij0123456789klmnopqrst";
}
