using Modbot.Core.Discord;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Tests.Gateway;

/// <summary>
/// The channel rule for <c>/events</c> (Discord commands design §3.2): after a public answer in a
/// channel, the next ones there for sixty seconds are private. It is decided before the command is
/// acknowledged, because Discord fixes who sees a reply when the interaction is acknowledged.
/// </summary>
public class PublicReplyWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    private static IReadOnlyList<DiscordCommandDefinition> Registered => DiscordCommands.For(DiscordCommandSwitches.With(null, "events", true));

    private static IReadOnlyDictionary<string, string> Typed(params (string Name, string Value)[] options)
        => options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal);

    private static bool InPublic(PublicReplyWindow window, ulong? channel, DateTimeOffset at, params (string Name, string Value)[] options)
        => DiscordNetGateway.RepliesInPublic(Registered, "events", Typed(options), channel, window, at);

    // ── The window itself ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFirstPublicAnswerInAChannel_TakesTheWindow_AndASecondInsideItIsRefused()
    {
        var window = new PublicReplyWindow();

        Assert.True(window.TryTake("events", 1, Start, Minute));
        Assert.False(window.TryTake("events", 1, Start.AddSeconds(1), Minute));
        Assert.False(window.TryTake("events", 1, Start.AddSeconds(59), Minute));
    }

    [Fact]
    public void ARefusedTry_DoesNotExtendTheWindow_SoSixtySecondsAfterTheFirstIsEnough()
    {
        var window = new PublicReplyWindow();

        Assert.True(window.TryTake("events", 1, Start, Minute));
        Assert.False(window.TryTake("events", 1, Start.AddSeconds(30), Minute));
        Assert.False(window.TryTake("events", 1, Start.AddSeconds(59), Minute));
        Assert.True(window.TryTake("events", 1, Start.AddSeconds(60), Minute));

        // And the new answer starts the next window.
        Assert.False(window.TryTake("events", 1, Start.AddSeconds(90), Minute));
    }

    [Fact]
    public void AnotherChannel_AndAnotherCommand_AreCountedApart()
    {
        var window = new PublicReplyWindow();

        Assert.True(window.TryTake("events", 1, Start, Minute));
        Assert.True(window.TryTake("events", 2, Start, Minute));
        Assert.True(window.TryTake("other", 1, Start, Minute));
        Assert.False(window.TryTake("events", 1, Start, Minute));
    }

    // ── What the gateway decides before acknowledging ──────────────────────────────────────

    [Fact]
    public void ThePublicEventsAnswer_IsPublicOnce_ThenPrivateInTheSameChannelForSixtySeconds()
    {
        var window = new PublicReplyWindow();

        Assert.True(InPublic(window, 10, Start));
        Assert.False(InPublic(window, 10, Start.AddSeconds(20)));
        Assert.False(InPublic(window, 10, Start.AddSeconds(59)));

        // Another channel is not held up, and the channel is open again after the minute.
        Assert.True(InPublic(window, 11, Start.AddSeconds(20)));
        Assert.True(InPublic(window, 10, Start.AddSeconds(60)));
    }

    [Fact]
    public void APrivateAsk_IsPrivate_AndDoesNotTakeTheWindow()
    {
        var window = new PublicReplyWindow();

        Assert.False(InPublic(window, 10, Start, ("private", "true")));
        Assert.True(InPublic(window, 10, Start.AddSeconds(1)));
        Assert.False(InPublic(window, 10, Start.AddSeconds(2)));
    }

    [Fact]
    public void WithNoChannelToCountAgainst_ThePublicAnswerIsNotLimited()
    {
        var window = new PublicReplyWindow();

        Assert.True(InPublic(window, null, Start));
        Assert.True(InPublic(window, null, Start));
    }

    [Fact]
    public void ACommandWithNoWindow_AnswersInPublicEveryTime_AndAnUnknownOneInPrivate()
    {
        var window = new PublicReplyWindow();
        IReadOnlyList<DiscordCommandDefinition> registered =
        [
            new("loud", "A public one", [], Reply: DiscordReplyKind.Public),
        ];

        Assert.True(DiscordNetGateway.RepliesInPublic(registered, "loud", Typed(), 10, window, Start));
        Assert.True(DiscordNetGateway.RepliesInPublic(registered, "loud", Typed(), 10, window, Start));
        Assert.False(DiscordNetGateway.RepliesInPublic(registered, "unknown", Typed(), 10, window, Start));
        Assert.False(DiscordNetGateway.RepliesInPublic([], "events", Typed(), 10, window, Start));
    }

    [Fact]
    public async Task ThePrivateAnswer_IsAcknowledgedAsPrivate_AndThePublicOneAsPublic()
    {
        var window = new PublicReplyWindow();

        var first = new RecordingSender();
        var second = new RecordingSender();

        var firstAnswer = new DiscordInteractionAnswer(first, Log.Logger, InPublic(window, 10, Start), TimeSpan.FromMilliseconds(50));
        var secondAnswer = new DiscordInteractionAnswer(second, Log.Logger, InPublic(window, 10, Start.AddSeconds(5)), TimeSpan.FromMilliseconds(50));

        await firstAnswer.AcknowledgeNowAsync();
        await secondAnswer.AcknowledgeNowAsync();
        await firstAnswer.ReplyAsync(DiscordReply.Say("events"));
        await secondAnswer.ReplyAsync(DiscordReply.Say("events"));

        Assert.Equal(["defer:public", "followup:public"], first.Calls);
        Assert.Equal(["defer:private", "followup:private"], second.Calls);
    }

    private sealed class RecordingSender : IInteractionSender
    {
        public List<string> Calls { get; } = [];

        public bool CanRewrite => false;

        public Task DeferAsync(bool privately)
        {
            Calls.Add(privately ? "defer:private" : "defer:public");
            return Task.CompletedTask;
        }

        public Task ShowFormAsync(DiscordForm form) => Task.CompletedTask;

        public Task RespondAsync(DiscordReply reply, bool privately)
        {
            Calls.Add(privately ? "respond:private" : "respond:public");
            return Task.CompletedTask;
        }

        public Task FollowupAsync(DiscordReply reply, bool privately)
        {
            Calls.Add(privately ? "followup:private" : "followup:public");
            return Task.CompletedTask;
        }

        public Task RewriteAsync(DiscordReply reply) => Task.CompletedTask;

        public Task RewriteAgainAsync(DiscordReply reply) => Task.CompletedTask;
    }
}
