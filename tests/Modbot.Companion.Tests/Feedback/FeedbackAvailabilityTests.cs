using Modbot.Companion.Feedback;
using Modbot.Companion.Ingest;

namespace Modbot.Companion.Tests.Feedback;

/// <summary>When the Feedback button is offered: as the server says this person may send feedback.</summary>
public class FeedbackAvailabilityTests
{
    private static readonly ServerPairing Pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    [Fact]
    public void TheButtonIsNotOfferedUntilTheServerHasSaidYes()
    {
        var availability = new FeedbackAvailability(new FakeFeedbackClient(), () => Pairing);

        Assert.False(availability.Available);
    }

    [Fact]
    public async Task TheButtonFollowsSendFeedbackAndRaisesChangedOnlyWhenTheAnswerChanges()
    {
        var client = new FakeFeedbackClient { Available = true };
        var availability = new FeedbackAvailability(client, () => Pairing);
        var changes = 0;
        availability.Changed += () => changes++;

        await availability.RefreshAsync(TestContext.Current.CancellationToken);
        await availability.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(availability.Available);
        Assert.Equal(1, changes);

        client.Available = false;
        await availability.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(availability.Available);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task AServerThatCannotBeReachedLeavesTheLastAnswer()
    {
        var client = new FakeFeedbackClient { Available = true };
        var availability = new FeedbackAvailability(client, () => Pairing);
        await availability.RefreshAsync(TestContext.Current.CancellationToken);

        client.Available = null;
        await availability.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(availability.Available);
    }

    [Fact]
    public async Task NoPairingTakesTheButtonAway()
    {
        var pairing = (ServerPairing?)Pairing;
        var availability = new FeedbackAvailability(new FakeFeedbackClient { Available = true }, () => pairing);
        await availability.RefreshAsync(TestContext.Current.CancellationToken);

        pairing = null;
        await availability.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(availability.Available);
    }
}
