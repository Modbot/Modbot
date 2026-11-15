using Modbot.Companion.Talk;

namespace Modbot.Companion.Tests.Talk;

/// <summary>
/// A start that fails: the panel gets a short plain line, the log gets the exception once, and the
/// start is tried again by itself.
/// </summary>
public class VRChatOscStartTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(20);

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    [Fact]
    public async Task AFailedStartSaysSoPlainlyAndLogsTheExceptionOnce()
    {
        var tries = 0;
        var logged = new List<Exception>();
        var failure = new NullReferenceException();

        using var osc = new VRChatOsc(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            Short,
            logged.Add,
            _ =>
            {
                Interlocked.Increment(ref tries);
                throw failure;
            });

        osc.Start();

        Assert.Equal(VRChatOsc.StartProblem, osc.Problem);
        Assert.DoesNotContain("Object reference", osc.Problem, StringComparison.Ordinal);

        // It keeps trying, and the log still has the one line.
        Assert.True(await WaitFor(() => Volatile.Read(ref tries) >= 3));
        Assert.Same(failure, Assert.Single(logged));
        Assert.Equal(VRChatOsc.StartProblem, osc.Problem);
    }

    [Fact]
    public async Task TheTriesStopWhenItIsDisposed()
    {
        var tries = 0;

        var osc = new VRChatOsc(
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            Short,
            startQuery: _ =>
            {
                Interlocked.Increment(ref tries);
                throw new InvalidOperationException("no");
            });

        osc.Start();
        Assert.True(await WaitFor(() => Volatile.Read(ref tries) >= 2));

        osc.Dispose();
        var after = Volatile.Read(ref tries);
        await Task.Delay(Short * 5, TestContext.Current.CancellationToken);

        Assert.Equal(after, Volatile.Read(ref tries));
    }
}
