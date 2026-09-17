using System.IO.Pipes;
using System.Text;
using Modbot.Companion.Pairing;

namespace Modbot.Companion.Tests.Pairing;

/// <summary>
/// The hand-off between the copy of the client Windows starts to deliver a link and the copy that
/// is already running. If this drops the link, "Open in Modbot" does nothing and the moderator has
/// no idea why.
/// </summary>
public class PairingLinkInboxTests
{
    private static string PipeName() => "modbot-companion-tests-" + Guid.NewGuid().ToString("n");

    private static async Task<(PairingLinkInbox Inbox, List<string> Received, Task Listening)> ListeningAsync(
        string pipe,
        CancellationToken ct)
    {
        var received = new List<string>();
        var inbox = new PairingLinkInbox(pipe);

        var listening = inbox.ListenAsync(
            message =>
            {
                lock (received)
                    received.Add(message);

                return Task.CompletedTask;
            },
            ct);

        // The listener creates its pipe synchronously before its first await, but give it a
        // moment on a loaded machine anyway.
        await Task.Delay(50, ct);

        return (inbox, received, listening);
    }

    private static async Task<List<string>> WaitForAsync(List<string> received, int count, CancellationToken ct)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(5), ct);
        while (!deadline.IsCompleted)
        {
            lock (received)
            {
                if (received.Count >= count)
                    return [.. received];
            }

            await Task.Delay(20, ct);
        }

        lock (received)
            return [.. received];
    }

    [Fact]
    public async Task ALinkSentByASecondCopyReachesTheRunningOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pipe = PipeName();
        var (_, received, listening) = await ListeningAsync(pipe, stop.Token);

        var link = new PairingToken(new Uri("https://modbot.example/"), "AB12-CD34").ToLink();
        Assert.True(await PairingLinkInbox.TrySendAsync(link, pipe, TimeSpan.FromSeconds(3), ct));

        Assert.Equal([link], await WaitForAsync(received, 1, ct));

        await stop.CancelAsync();
        await listening;
    }

    [Fact]
    public async Task TheInboxKeepsListeningAfterTheFirstMessage()
    {
        // A moderator may pair with two groups in one sitting. The second link must not find the
        // inbox closed because the first was delivered.
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pipe = PipeName();
        var (_, received, listening) = await ListeningAsync(pipe, stop.Token);

        Assert.True(await PairingLinkInbox.TrySendAsync("first", pipe, TimeSpan.FromSeconds(3), ct));
        Assert.True(await PairingLinkInbox.TrySendAsync("second", pipe, TimeSpan.FromSeconds(3), ct));

        Assert.Equal(["first", "second"], await WaitForAsync(received, 2, ct));

        await stop.CancelAsync();
        await listening;
    }

    [Fact]
    public async Task SendingWithNobodyListeningSaysSoRatherThanHanging()
    {
        // The second copy uses this answer to decide it should become the running copy itself,
        // so it has to come back, and come back quickly.
        var ct = TestContext.Current.CancellationToken;

        var delivered = await PairingLinkInbox.TrySendAsync(
            "hello", PipeName(), TimeSpan.FromMilliseconds(300), ct);

        Assert.False(delivered);
    }

    [Fact]
    public async Task AnOversizedMessageIsDroppedAndTheNextOneStillArrives()
    {
        // A pairing link is a few hundred bytes. Anything that does not fit the bound is not a
        // link, and it must neither be handed on nor jam the inbox.
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pipe = PipeName();
        var (_, received, listening) = await ListeningAsync(pipe, stop.Token);

        // Written raw, because TrySendAsync refuses to send it at all.
        var flood = Encoding.UTF8.GetBytes(new string('x', PairingLinkInbox.MaxMessageBytes + 1));
        await using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly))
        {
            await raw.ConnectAsync(3000, ct);
            await raw.WriteAsync(flood, ct);
            await raw.FlushAsync(ct);
        }

        Assert.True(await PairingLinkInbox.TrySendAsync("after", pipe, TimeSpan.FromSeconds(3), ct));

        Assert.Equal(["after"], await WaitForAsync(received, 1, ct));

        await stop.CancelAsync();
        await listening;
    }

    [Fact]
    public async Task TheSenderRefusesToSendSomethingThatCouldNotBeALink()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await PairingLinkInbox.TrySendAsync(
            new string('x', PairingLinkInbox.MaxMessageBytes + 1), PipeName(), TimeSpan.FromMilliseconds(100), ct));
    }

    [Fact]
    public async Task StoppingTheInboxEndsTheListenQuietly()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var (_, _, listening) = await ListeningAsync(PipeName(), stop.Token);

        await stop.CancelAsync();

        await listening.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(listening.IsCompletedSuccessfully);
    }
}
