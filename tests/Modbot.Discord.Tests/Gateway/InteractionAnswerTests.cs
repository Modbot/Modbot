using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Tests.Gateway;

/// <summary>
/// How an interaction is answered (Discord commands design §3.2, acting from Discord design §11):
/// nothing is acknowledged first, so a slash command can answer with a form; a command that is
/// silent for too long is acknowledged for it, as public when it replies in public and as private
/// otherwise. Run against a sender that records what Discord would have been told.
/// </summary>
public class InteractionAnswerTests
{
    private static readonly TimeSpan Within = TimeSpan.FromMilliseconds(80);

    private static readonly DiscordForm Form = new("Report", "modbot:form:test", []);

    private sealed class FakeSender(bool canRewrite = false) : IInteractionSender
    {
        private readonly TaskCompletionSource<bool> _deferred = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];

        public bool CanRewrite => canRewrite;

        /// <summary>Completes with the audience of the deferral when there is one.</summary>
        public Task<bool> Deferred => _deferred.Task;

        public Task DeferAsync(bool privately)
        {
            Calls.Add(privately ? "defer:private" : "defer:public");
            _deferred.TrySetResult(privately);
            return Task.CompletedTask;
        }

        public Task ShowFormAsync(DiscordForm form)
        {
            Calls.Add("form:" + form.Id);
            return Task.CompletedTask;
        }

        public Task RespondAsync(DiscordReply reply, bool privately)
        {
            Calls.Add($"respond:{(privately ? "private" : "public")}:{reply.Text}");
            return Task.CompletedTask;
        }

        public Task FollowupAsync(DiscordReply reply, bool privately)
        {
            Calls.Add($"followup:{(privately ? "private" : "public")}:{reply.Text}");
            return Task.CompletedTask;
        }

        public Task RewriteAsync(DiscordReply reply)
        {
            Calls.Add("rewrite:" + reply.Text);
            return Task.CompletedTask;
        }

        public Task RewriteAgainAsync(DiscordReply reply)
        {
            Calls.Add("rewrite-again:" + reply.Text);
            return Task.CompletedTask;
        }
    }

    private static DiscordInteractionAnswer Answer(FakeSender sender, bool inPublic = false)
        => new(sender, Log.Logger, inPublic, Within);

    private static async Task<T> Within5Seconds<T>(Task<T> task)
        => await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    // ── Public or private is fixed by the acknowledgement ──────────────────────────────────

    [Fact]
    public async Task ASilentCommandThatRepliesInPublic_IsDeferredAsPublic()
    {
        var sender = new FakeSender();
        var answer = Answer(sender, inPublic: true);

        answer.AcknowledgeIfSilent();
        var privately = await Within5Seconds(sender.Deferred);

        Assert.False(privately);

        // The answer that follows keeps the deferral's audience: public.
        await answer.ReplyAsync(DiscordReply.Say("Next events"));
        Assert.Equal(["defer:public", "followup:public:Next events"], sender.Calls);
    }

    [Fact]
    public async Task EveryOtherSilentCommand_IsDeferredAsPrivate()
    {
        var sender = new FakeSender();
        var answer = Answer(sender);

        answer.AcknowledgeIfSilent();
        var privately = await Within5Seconds(sender.Deferred);

        Assert.True(privately);

        await answer.ReplyAsync(DiscordReply.Say("Note saved."));
        Assert.Equal(["defer:private", "followup:private:Note saved."], sender.Calls);
    }

    [Fact]
    public async Task APublicReplyGivenInTime_IsSentAsPublic_AndNothingIsDeferredAfterIt()
    {
        var sender = new FakeSender();
        var answer = Answer(sender, inPublic: true);

        answer.AcknowledgeIfSilent();
        await answer.ReplyAsync(DiscordReply.Say("Next events"));

        await Task.Delay(Within * 5, TestContext.Current.CancellationToken);

        Assert.Equal(["respond:public:Next events"], sender.Calls);
    }

    [Fact]
    public async Task APrivateReplyGivenInTime_IsSentAsPrivate()
    {
        var sender = new FakeSender();
        var answer = Answer(sender);

        answer.AcknowledgeIfSilent();
        await answer.ReplyAsync(DiscordReply.Say("Note saved."));
        await answer.ReplyAsync(DiscordReply.Say("And another."));

        Assert.Equal(["respond:private:Note saved.", "followup:private:And another."], sender.Calls);
    }

    [Fact]
    public async Task AcknowledgingNow_FollowsTheAudienceToo()
    {
        var publicOne = new FakeSender();
        await Answer(publicOne, inPublic: true).AcknowledgeNowAsync();

        var privateOne = new FakeSender();
        await Answer(privateOne).AcknowledgeNowAsync();

        Assert.Equal(["defer:public"], publicOne.Calls);
        Assert.Equal(["defer:private"], privateOne.Calls);
    }

    // ── A slash command can answer with a form first ───────────────────────────────────────

    [Fact]
    public async Task ASlashCommandThatShowsAForm_IsNotAcknowledgedFirst_NorAfterwards()
    {
        var sender = new FakeSender();
        var answer = Answer(sender, inPublic: true);

        answer.AcknowledgeIfSilent();
        await answer.ShowFormAsync(Form);

        // Past the point where a silent command would have been deferred.
        await Task.Delay(Within * 5, TestContext.Current.CancellationToken);

        Assert.Equal(["form:modbot:form:test"], sender.Calls);
        Assert.True(answer.Answered);
    }

    [Fact]
    public async Task AReplyAfterAForm_IsDropped_BecauseDiscordTakesNothingAfterOne()
    {
        var sender = new FakeSender();
        var answer = Answer(sender);

        await answer.ShowFormAsync(Form);
        await answer.ReplyAsync(DiscordReply.Say("Too late."));

        Assert.Equal(["form:modbot:form:test"], sender.Calls);
    }

    [Fact]
    public async Task AFormAfterTheCommandWasAcknowledged_IsRefused()
    {
        var sender = new FakeSender();
        var answer = Answer(sender);

        await answer.AcknowledgeNowAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => answer.ShowFormAsync(Form));
        Assert.Equal(["defer:private"], sender.Calls);
    }

    // ── Rewriting the message a button sits on ─────────────────────────────────────────────

    [Fact]
    public async Task AButtonsMessage_IsRewrittenOnce_ThenEditedAgain()
    {
        var sender = new FakeSender(canRewrite: true);
        var answer = Answer(sender);

        await answer.UpdateAsync(DiscordReply.Say("Banning..."));
        await answer.UpdateAsync(DiscordReply.Say("Banned."));

        Assert.Equal(["rewrite:Banning...", "rewrite-again:Banned."], sender.Calls);
    }

    [Fact]
    public async Task APressThatWasAcknowledgedAsThinking_AnswersWithANewReplyInstead()
    {
        var sender = new FakeSender(canRewrite: true);
        var answer = Answer(sender);

        await answer.AcknowledgeNowAsync();
        await answer.UpdateAsync(DiscordReply.Say("Banned."));

        Assert.Equal(["defer:private", "followup:private:Banned."], sender.Calls);
    }

    [Fact]
    public async Task AnInteractionWithNoMessageToRewrite_RepliesInstead()
    {
        var sender = new FakeSender(canRewrite: false);
        var answer = Answer(sender);

        await answer.UpdateAsync(DiscordReply.Say("Done."));

        Assert.Equal(["respond:private:Done."], sender.Calls);
    }
}
