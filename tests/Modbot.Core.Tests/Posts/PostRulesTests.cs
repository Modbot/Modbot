using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;

namespace Modbot.Core.Tests.Posts;

/// <summary>
/// The shared sending rules (posts design §3.4): what is due, what holds a site, how late is too
/// late (decision 5), the hourly cap, and which lists a post is in (§2.3).
/// </summary>
public class PostRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);

    private static (Post Post, PostDestination Destination) Scheduled(DateTimeOffset sendAt, DateTimeOffset? waitingSince = null)
    {
        var destination = new PostDestination
        {
            Id = Guid.NewGuid(),
            Network = PostNetworks.Discord,
            Target = "222",
            State = PostDestinationStates.Waiting,
            UpdatedAt = waitingSince ?? sendAt - TimeSpan.FromDays(1),
        };

        var post = new Post
        {
            Id = Guid.NewGuid(),
            Text = "Movie night",
            Status = PostStatuses.Scheduled,
            SendAt = sendAt,
            Destinations = [destination],
        };

        destination.PostId = post.Id;
        return (post, destination);
    }

    [Fact]
    public void APostIsDueOnceItsTimeHasCome()
    {
        var (post, destination) = Scheduled(Now + TimeSpan.FromMinutes(5));
        Assert.False(PostRules.IsDue(post, destination, Now));

        Assert.True(PostRules.IsDue(post, destination, Now + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ADraftOrACancelledPostIsNeverDue()
    {
        var (post, destination) = Scheduled(Now - TimeSpan.FromMinutes(1));

        post.Status = PostStatuses.Draft;
        Assert.False(PostRules.IsDue(post, destination, Now));

        post.Status = PostStatuses.Cancelled;
        Assert.False(PostRules.IsDue(post, destination, Now));
    }

    [Fact]
    public void OnlyAWaitingDestinationIsDue()
    {
        var (post, destination) = Scheduled(Now - TimeSpan.FromMinutes(1));

        foreach (var state in new[] { PostDestinationStates.Sending, PostDestinationStates.Checking, PostDestinationStates.Posted, PostDestinationStates.Failed })
        {
            destination.State = state;
            Assert.False(PostRules.IsDue(post, destination, Now));
        }
    }

    [Fact]
    public void UpToAnHourLateIsStillSent_LaterThanThatIsNot()
    {
        var (post, destination) = Scheduled(Now - TimeSpan.FromMinutes(59));
        Assert.False(PostRules.IsLate(post, destination, Now));

        post.SendAt = Now - TimeSpan.FromMinutes(61);
        Assert.True(PostRules.IsLate(post, destination, Now));
    }

    [Fact]
    public void TryAgainOrPostNowStartsTheHourAgain()
    {
        // Due two hours ago, but a person sent it on its way a minute ago.
        var (post, destination) = Scheduled(Now - TimeSpan.FromHours(2), waitingSince: Now - TimeSpan.FromMinutes(1));

        Assert.False(PostRules.IsLate(post, destination, Now));
    }

    [Fact]
    public void PauseHoldsEverySite()
    {
        var sites = new PostSites(Paused: true, DiscordOn: true, DiscordSetUp: true);

        Assert.Equal(PostHolds.Paused, sites.HoldFor(PostNetworks.Discord));
        Assert.Equal(PostHolds.Paused, sites.HoldFor(PostNetworks.VRChat));
    }

    [Fact]
    public void DiscordOffOrNotSetUpHoldsDiscord()
    {
        Assert.Equal(PostHolds.Off, new PostSites(false, DiscordOn: false, DiscordSetUp: true).HoldFor(PostNetworks.Discord));
        Assert.Equal(PostHolds.NotSetUp, new PostSites(false, DiscordOn: true, DiscordSetUp: false).HoldFor(PostNetworks.Discord));
        Assert.Null(new PostSites(false, DiscordOn: true, DiscordSetUp: true).HoldFor(PostNetworks.Discord));
    }

    [Fact]
    public void ASiteNotBuiltYetIsNeverSetUp()
    {
        var sites = new PostSites(false, true, true);

        Assert.Equal(PostHolds.NotSetUp, sites.HoldFor(PostNetworks.VRChat));
        Assert.Equal(PostHolds.NotSetUp, sites.HoldFor(PostNetworks.Bluesky));
    }

    [Fact]
    public void TheHourlyCapStopsTheEleventh()
    {
        Assert.True(PostRules.UnderHourlyCap(PostRules.PerSitePerHour - 1));
        Assert.False(PostRules.UnderHourlyCap(PostRules.PerSitePerHour));
    }

    [Fact]
    public void AWaitingDestinationShowsWhyItWaits()
    {
        var (_, destination) = Scheduled(Now);

        Assert.Equal(PostHolds.Paused, PostRules.Shown(destination, new PostSites(true, true, true)));
        Assert.Equal(PostHolds.Off, PostRules.Shown(destination, new PostSites(false, false, true)));
        Assert.Equal(PostDestinationStates.Waiting, PostRules.Shown(destination, new PostSites(false, true, true)));

        destination.State = PostDestinationStates.Checking;
        Assert.Equal(PostDestinationStates.Sending, PostRules.Shown(destination, new PostSites(true, true, true)));
    }

    [Fact]
    public void TheListsFollowTheDestinations()
    {
        var (post, destination) = Scheduled(Now);
        Assert.Equal([PostLists.Scheduled], PostRules.ListsOf(post));

        destination.State = PostDestinationStates.Posted;
        Assert.Equal([PostLists.Sent], PostRules.ListsOf(post));

        destination.State = PostDestinationStates.Failed;
        Assert.Equal([PostLists.Failed], PostRules.ListsOf(post));

        post.Status = PostStatuses.Draft;
        Assert.Equal([PostLists.Drafts], PostRules.ListsOf(post));

        post.Status = PostStatuses.Cancelled;
        Assert.Equal([PostLists.Cancelled], PostRules.ListsOf(post));
    }

    [Fact]
    public void APostFailedOnOneSiteAndWaitingOnAnotherIsInBothLists()
    {
        var (post, destination) = Scheduled(Now);
        destination.State = PostDestinationStates.Failed;
        post.Destinations.Add(new PostDestination { Network = PostNetworks.VRChat, State = PostDestinationStates.Waiting });

        Assert.Equal([PostLists.Scheduled, PostLists.Failed], PostRules.ListsOf(post));
    }

    [Fact]
    public void AWholePostCannotBeChangedWhileItIsBeingSent()
    {
        var (post, destination) = Scheduled(Now);
        Assert.Null(PostRules.CannotEdit(post));

        destination.State = PostDestinationStates.Sending;
        Assert.Equal(PostRules.BeingSent, PostRules.CannotEdit(post));

        destination.State = PostDestinationStates.Checking;
        Assert.Equal(PostRules.BeingSent, PostRules.CannotEdit(post));

        destination.State = PostDestinationStates.Posted;
        Assert.NotNull(PostRules.CannotEdit(post));

        destination.State = PostDestinationStates.Failed;
        destination.MayBeSent = true;
        Assert.NotNull(PostRules.CannotEdit(post));

        destination.MayBeSent = false;
        Assert.Null(PostRules.CannotEdit(post));
    }

    [Fact]
    public void TryAgainLooksFirstWhenTheSiteMayHaveThePost()
    {
        var (post, destination) = Scheduled(Now - TimeSpan.FromHours(3));
        destination.State = PostDestinationStates.Failed;
        destination.MayBeSent = true;
        var version = post.Version;

        PostChanges.TryAgain(post, destination, Now);

        Assert.Equal(PostDestinationStates.Checking, destination.State);
        Assert.True(destination.SendIfMissing);
        Assert.Equal(Now, destination.CheckAt);
        Assert.Equal(version + 1, post.Version);
    }

    [Fact]
    public void TryAgainAfterAClearRefusalWaitsToBeSent_AndIsNotLate()
    {
        var (post, destination) = Scheduled(Now - TimeSpan.FromHours(3));
        destination.State = PostDestinationStates.Failed;
        destination.Error = "Missing Permissions";

        PostChanges.TryAgain(post, destination, Now);

        Assert.Equal(PostDestinationStates.Waiting, destination.State);
        Assert.Null(destination.Error);
        Assert.False(PostRules.IsLate(post, destination, Now));
    }

    [Fact]
    public void CancelSkipsWhatHasNotGone_AndKeepsWhatMayBeOnTheSite()
    {
        var (post, destination) = Scheduled(Now + TimeSpan.FromHours(1));
        var maybe = new PostDestination { Network = PostNetworks.VRChat, State = PostDestinationStates.Failed, MayBeSent = true };
        post.Destinations.Add(maybe);

        PostChanges.Cancel(post, Now);

        Assert.Equal(PostStatuses.Cancelled, post.Status);
        Assert.Equal(PostDestinationStates.Skipped, destination.State);
        Assert.Equal(PostDestinationStates.Failed, maybe.State);
    }

    [Fact]
    public void PostNowSendsAtOnce_AndPutsALateOneBackToWaiting()
    {
        var (post, destination) = Scheduled(Now - TimeSpan.FromHours(3));
        destination.State = PostDestinationStates.Failed;
        destination.Error = PostRules.NotSentOnTime;

        PostChanges.SendNow(post, Now);

        Assert.Equal(Now, post.SendAt);
        Assert.Equal(PostDestinationStates.Waiting, destination.State);
        Assert.True(PostRules.IsDue(post, destination, Now));
        Assert.False(PostRules.IsLate(post, destination, Now));
    }

    [Fact]
    public void ADiscordIdSaysWhenItWasMade()
    {
        var at = new DateTimeOffset(2026, 10, 3, 18, 0, 0, 123, TimeSpan.Zero);

        Assert.Equal(at, PostRules.DiscordTimeOf(PostRules.DiscordIdAt(at)));
        Assert.Null(PostRules.DiscordTimeOf("not an id"));
        Assert.Null(PostRules.DiscordTimeOf(null));
    }

    [Fact]
    public void TheLookReadsFromADiscordIdMadeFromTheTime()
    {
        // Discord's own example: 175928847299117063 was made at 2016-04-30 11:18:25.796 UTC.
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1462015105796);
        var id = ulong.Parse(PostRules.DiscordIdAt(at), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(175928847299117063UL >> 22, id >> 22);
        Assert.Equal(0UL, id & ((1UL << 22) - 1));
    }
}
