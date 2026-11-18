using Modbot.Companion.App.Feedback;
using Modbot.Companion.Feedback;
using Modbot.Overlay.Views;

namespace Modbot.Companion.App.Tests;

/// <summary>What the Feedback button shows: nothing until it is offered, then the stage in words and a microphone.</summary>
public class FeedbackCardTests
{
    [Fact]
    public void WhenNotOfferedTheButtonIsHidden()
    {
        Assert.False(FeedbackCard.Off.Offered);
        Assert.Equal(DictateState.Off, FeedbackCard.Off.Picture);
    }

    [Fact]
    public void AtIdleTheButtonSaysFeedbackAndDoesNotMove()
    {
        var card = new FeedbackCard(FeedbackState.Idle);

        Assert.True(card.Offered);
        Assert.Equal("Feedback", card.Label);
        Assert.Equal(DictateState.Idle, card.Picture);
        Assert.False(card.Moves);
    }

    [Theory]
    [InlineData(FeedbackState.Listening, "Listening", DictateState.Listening, false)]
    [InlineData(FeedbackState.Writing, "Writing", DictateState.Writing, true)]
    [InlineData(FeedbackState.Tidying, "Tidying", DictateState.Writing, true)]
    [InlineData(FeedbackState.Sending, "Sending", DictateState.Sending, true)]
    [InlineData(FeedbackState.Preparing, "Getting ready", DictateState.GettingReady, true)]
    [InlineData(FeedbackState.Draft, "Feedback", DictateState.Idle, false)]
    public void EachStageHasItsWordsAndPicture(FeedbackState state, string label, DictateState picture, bool moves)
    {
        var card = new FeedbackCard(state);

        Assert.Equal(label, card.Label);
        Assert.Equal(picture, card.Picture);
        Assert.Equal(moves, card.Moves);
    }

    [Fact]
    public void ANoticeTakesTheLabelsPlaceAtIdle()
    {
        Assert.Equal("Sent", new FeedbackCard(FeedbackState.Idle, Notice: "Sent").Label);
        Assert.Equal("30%", new FeedbackCard(FeedbackState.Preparing, Percent: 30).PercentText);
    }
}
