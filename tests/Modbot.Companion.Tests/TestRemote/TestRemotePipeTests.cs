using System.Text.Json.Nodes;
using Modbot.Companion.TestRemote;

namespace Modbot.Companion.Tests.TestRemote;

/// <summary>One line in over a real pipe, one line of JSON back, with ok true or false.</summary>
public class TestRemotePipeTests
{
    private static string PipeName() => "modbot-remote-tests-" + Guid.NewGuid().ToString("n");

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static async Task<(Task Listening, List<RemoteCommand> Heard)> ListenAsync(string pipe, CancellationToken ct)
    {
        var heard = new List<RemoteCommand>();
        var server = new TestRemoteServer(pipe);

        var listening = server.ListenAsync(
            line => RemoteAnswers.AnswerAsync(
                line,
                command =>
                {
                    lock (heard)
                        heard.Add(command);

                    return Task.FromResult(new JsonObject { ["heard"] = command.GetType().Name });
                },
                ct),
            ct);

        await Task.Delay(50, ct);
        return (listening, heard);
    }

    [Fact]
    public async Task ACommandIsCarriedOutAndAnsweredInOneLine()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pipe = PipeName();
        var (listening, heard) = await ListenAsync(pipe, stop.Token);

        var answer = await TestRemoteClient.AskAsync(pipe, "press \"Overlay on\"", Second, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotNull(answer);
        Assert.DoesNotContain('\n', answer);
        var json = JsonNode.Parse(answer)!.AsObject();
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal("Press", json["heard"]!.GetValue<string>());
        Assert.Equal([new RemoteCommand.Press("Overlay on")], heard);

        // And again: the pipe is ready for the next caller.
        var again = await TestRemoteClient.AskAsync(pipe, "state", Second, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("\"ok\":true", again, StringComparison.Ordinal);

        await stop.CancelAsync();
        await listening;
    }

    [Fact]
    public async Task ABadLineIsAnsweredWithWhyAndNothingIsCarriedOut()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pipe = PipeName();
        var (listening, heard) = await ListenAsync(pipe, stop.Token);

        var answer = await TestRemoteClient.AskAsync(pipe, "event joined usr_123", Second, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var json = JsonNode.Parse(answer!)!.AsObject();
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("Test people only", json["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(heard);

        await stop.CancelAsync();
        await listening;
    }

    [Fact]
    public async Task WaitIsAnsweredWithoutReachingTheCompanion()
    {
        var heard = 0;
        var answer = await RemoteAnswers.AnswerAsync("wait 10", _ =>
        {
            heard++;
            return Task.FromResult(new JsonObject());
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, heard);
        Assert.Equal("{\"ok\":true,\"waited\":10}", answer);
    }

    [Fact]
    public async Task ARefusalBecomesTheAnswersError()
    {
        var answer = await RemoteAnswers.AnswerAsync("put-back", _ => throw new RemoteRefusal("The main panel is off."), TestContext.Current.CancellationToken);

        Assert.Equal("{\"ok\":false,\"error\":\"The main panel is off.\"}", answer);
    }

    [Fact]
    public async Task NothingListeningIsNoAnswer()
    {
        var answer = await TestRemoteClient.AskAsync(PipeName(), "state", TimeSpan.FromMilliseconds(200), Second, TestContext.Current.CancellationToken);

        Assert.Null(answer);
    }
}
