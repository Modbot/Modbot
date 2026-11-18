using Modbot.AI.Chat;

namespace Modbot.AI.Tests.Chat;

/// <summary>
/// The rules about filing feedback are in Chat's system prompt only when the tool is offered
/// (feedback design §7).
/// </summary>
public class ChatPromptFeedbackTests
{
    [Fact]
    public void WhenTheToolIsNotOffered_ThePromptDoesNotMentionFeedbackOrTheTool()
    {
        var prompt = ChatPrompt.Build("A Group", null);

        Assert.DoesNotContain("feedback", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ChatPrompt.FileFeedbackToolName, prompt, StringComparison.Ordinal);
        Assert.Contains("You can only look things up", prompt, StringComparison.Ordinal);
        Assert.Equal(prompt, ChatPrompt.Build("A Group", null, canFileFeedback: false));
    }

    [Fact]
    public void WhenTheToolIsOffered_ThePromptEncouragesFilingAfterTheDraftIsShownAndConfirmed()
    {
        var prompt = ChatPrompt.Build("A Group", null, canFileFeedback: true);

        Assert.Contains(ChatPrompt.FileFeedbackToolName, prompt, StringComparison.Ordinal);
        Assert.Contains("offer to file it as feedback", prompt, StringComparison.Ordinal);
        Assert.Contains("Show the draft first", prompt, StringComparison.Ordinal);
        Assert.Contains("names of people, worlds and groups taken out", prompt, StringComparison.Ordinal);
        Assert.Contains("only after the person has said yes", prompt, StringComparison.Ordinal);

        // Filing is the one thing it may do besides look things up; the rest of the ban stays.
        Assert.DoesNotContain("You can only look things up", prompt, StringComparison.Ordinal);
        Assert.Contains("cannot kick", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOperatorsInstructionsStillComeLast()
    {
        var prompt = ChatPrompt.Build("A Group", "Answer in Spanish.", canFileFeedback: true);

        Assert.EndsWith("Answer in Spanish.", prompt, StringComparison.Ordinal);
        Assert.True(
            prompt.IndexOf("Feedback:", StringComparison.Ordinal) < prompt.IndexOf("Instructions from this group's operator", StringComparison.Ordinal));
    }
}
