using Modbot.Core.Data.Entities;
using Modbot.Discord.Votes;

namespace Modbot.Discord.Tests.Votes;

/// <summary>
/// What a vote looks like in Discord: the ids its buttons carry, buttons up to 25 and lists beyond,
/// the counts only on a vote that shows them, and the result once it is closed. Nothing here ever
/// names a voter.
/// </summary>
public class VoteMessageTests
{
    private static Vote MakeVote(int options, bool showCounts = false, string? role = null)
    {
        var vote = new Vote { Question = "Which world?", ChannelId = "900", MessageId = "1", RoleId = role, ShowCounts = showCounts };
        vote.Options = [.. Enumerable.Range(0, options).Select(i => new VoteOption { VoteId = vote.Id, Label = $"World {i + 1}", Position = i })];
        return vote;
    }

    [Fact]
    public void A_button_id_names_the_vote_and_the_option_and_reads_back()
    {
        var vote = MakeVote(2);
        var option = vote.Options[1];

        var id = VoteMessage.ButtonId(vote.Id, option.Id);

        Assert.True(VoteMessage.IsPress(id));
        Assert.True(VoteMessage.TryRead(id, out var voteId, out var optionId));
        Assert.Equal(vote.Id, voteId);
        Assert.Equal(option.Id, optionId);
    }

    [Fact]
    public void A_list_id_names_the_vote_only()
    {
        var vote = MakeVote(30);

        Assert.True(VoteMessage.TryRead(VoteMessage.ListId(vote.Id, 1), out var voteId, out var optionId));
        Assert.Equal(vote.Id, voteId);
        Assert.Null(optionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("modbot:rolemenu:abc")]
    [InlineData("modbot:vote:notaguid:alsonot")]
    [InlineData("modbot:vote:")]
    public void An_id_that_is_not_a_vote_press_is_refused(string? id)
    {
        Assert.False(VoteMessage.TryRead(id, out _, out _));
    }

    [Fact]
    public void Twenty_five_options_are_buttons_and_no_lists()
    {
        var vote = MakeVote(25);

        Assert.Equal(25, VoteMessage.Buttons(vote).Count);
        Assert.Empty(VoteMessage.Lists(vote));
    }

    [Fact]
    public void More_than_twenty_five_options_are_lists_of_twenty_five_and_no_buttons()
    {
        var vote = MakeVote(60);

        Assert.Empty(VoteMessage.Buttons(vote));

        var lists = VoteMessage.Lists(vote);
        Assert.Equal([25, 25, 10], lists.Select(l => l.Choices.Count));
        Assert.All(lists, l => Assert.True(VoteMessage.IsPress(l.Id)));
    }

    [Fact]
    public void The_most_options_fill_Discords_five_lists()
    {
        var vote = MakeVote(VoteLimits.MostOptions);

        Assert.Equal(5, VoteMessage.Lists(vote).Count);
    }

    [Fact]
    public void A_vote_that_hides_counts_shows_no_numbers_while_open()
    {
        var vote = MakeVote(2, showCounts: false);
        var counts = new Dictionary<Guid, int> { [vote.Options[0].Id] = 7 };

        var open = VoteMessage.Open(vote, counts);

        Assert.Equal("Which world?", open.Title);
        Assert.Null(open.Description);
    }

    [Fact]
    public void A_vote_that_shows_counts_lists_each_option_in_button_order_with_zeros()
    {
        var vote = MakeVote(3, showCounts: true);
        var counts = new Dictionary<Guid, int> { [vote.Options[1].Id] = 4 };

        var description = VoteMessage.Open(vote, counts).Description!;

        var lines = description.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("World 1", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(": 0", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(": 4", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(": 0", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_limit_is_shown_and_not_pinged_and_comes_before_the_counts()
    {
        var vote = MakeVote(2, showCounts: true, role: "555");

        var description = VoteMessage.Open(vote).Description!;

        Assert.StartsWith("For <@&555> only", description, StringComparison.Ordinal);
        Assert.Contains("World 1", description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_closed_vote_shows_the_result_most_votes_first_with_shares_and_the_total()
    {
        var vote = MakeVote(3);
        var counts = new Dictionary<Guid, int> { [vote.Options[0].Id] = 1, [vote.Options[2].Id] = 3 };

        var closed = VoteMessage.Closed(vote, counts);

        var lines = closed.Description!.Split('\n');
        Assert.Contains("World 3", lines[0], StringComparison.Ordinal);
        Assert.Contains("3 (75%)", lines[0], StringComparison.Ordinal);
        Assert.Contains("World 1", lines[1], StringComparison.Ordinal);
        Assert.Contains("0 (0%)", lines[2], StringComparison.Ordinal);
        Assert.Equal("Closed. 4 votes", closed.Footer);
    }

    [Fact]
    public void A_closed_vote_with_no_votes_does_not_divide_by_zero()
    {
        var closed = VoteMessage.Closed(MakeVote(2), new Dictionary<Guid, int>());

        Assert.Contains("0 (0%)", closed.Description, StringComparison.Ordinal);
        Assert.Equal("Closed. 0 votes", closed.Footer);
    }
}
