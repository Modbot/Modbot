using Modbot.AI.Moderation;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Tests.Moderation;

/// <summary>
/// What the flag reviewer sends and what it believes of the answer (AutoMod design §6.1). The
/// call itself is the runner's; nothing here reaches a provider.
/// </summary>
public class FlagReviewerTests
{
    private const string Marker = "MODBOT-CONTENT-ABCDEF0123456789ABCDEF01";

    private static ModerationFlag Flag(FactPlatform platform = FactPlatform.Discord) => new()
    {
        Id = Guid.NewGuid(),
        RuleKind = ModerationRuleKind.TermList,
        RuleName = "Scams",
        RuleVersion = 2,
        Term = "free nitro",
        Target = "discordMessage",
        SubjectPlatform = platform,
        SubjectId = "u1",
        Matched = "FREE nitro",
        Reason = "Offers of free things.",
    };

    [Theory]
    [InlineData("""{"verdict":"keep","why":"It is a scam link."}""", FlagOpinions.Keep)]
    [InlineData("""{"verdict":"dismiss","why":"They are warning others about the scam."}""", FlagOpinions.Dismiss)]
    public void AVerdictWithAReasonIsRead(string reply, string verdict)
    {
        var opinion = FlagReviewer.Read(reply, proposeAction: false, Marker);

        Assert.NotNull(opinion);
        Assert.Equal(verdict, opinion.Verdict);
        Assert.Null(opinion.ProposedAction);
        Assert.False(string.IsNullOrWhiteSpace(opinion.Why));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"verdict":"maybe","why":"Not sure."}""")]
    [InlineData("""{"verdict":"keep","why":""}""")]
    [InlineData("""{"verdict":"keep"}""")]
    public void AnAnswerOfTheWrongShapeIsThrownAwayWhole(string reply)
        => Assert.Null(FlagReviewer.Read(reply, proposeAction: false, Marker));

    [Fact]
    public void AnActionNobodyAskedForIsAnAnswerToADifferentQuestion()
        => Assert.Null(FlagReviewer.Read("""{"verdict":"keep","why":"Scam.","action":"group_ban"}""", proposeAction: false, Marker));

    [Fact]
    public void WhenAnActionWasAskedFor_ItMustBeOneOfTheKnownOnes()
    {
        Assert.Null(FlagReviewer.Read("""{"verdict":"keep","why":"Scam."}""", proposeAction: true, Marker));
        Assert.Null(FlagReviewer.Read("""{"verdict":"keep","why":"Scam.","action":"nuke"}""", proposeAction: true, Marker));

        var opinion = FlagReviewer.Read("""{"verdict":"keep","why":"Scam.","action":"delete_message"}""", proposeAction: true, Marker);
        Assert.NotNull(opinion);
        Assert.Equal(FlagOpinions.DeleteMessage, opinion.ProposedAction);
    }

    [Fact]
    public void AReasonThatQuotesTheMarkerIsTheModelQuotingModbot()
        => Assert.Null(FlagReviewer.Read($$"""{"verdict":"keep","why":"The text after {{Marker}} says so."}""", proposeAction: false, Marker));

    [Fact]
    public void TheInstructionsNameTheRuleAndTheMatch_AndNeverTheMembersText()
    {
        var text = FlagReviewer.Instructions(Flag(), "free nitro, discord gift", Marker, proposeAction: false);

        Assert.Contains("Scams", text, StringComparison.Ordinal);
        Assert.Contains("free nitro, discord gift", text, StringComparison.Ordinal);
        Assert.Contains("\"FREE nitro\"", text, StringComparison.Ordinal);
        Assert.Contains(Marker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("action", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheActionListDependsOnThePlatform()
    {
        var discord = FlagReviewer.Instructions(Flag(FactPlatform.Discord), null, Marker, proposeAction: true);
        var vrchat = FlagReviewer.Instructions(Flag(FactPlatform.VRChat), null, Marker, proposeAction: true);

        Assert.Contains("delete_message and timeout apply", discord, StringComparison.Ordinal);
        Assert.Contains("group_ban and group_remove apply", vrchat, StringComparison.Ordinal);
    }
}
