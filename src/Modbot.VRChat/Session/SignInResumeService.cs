using Microsoft.Extensions.Hosting;
using Modbot.VRChat.RateLimiting;
using Serilog;

namespace Modbot.VRChat.Session;

/// <summary>
/// Makes the one sign-in attempt that follows a wait, when the wait ends (spec 4.1.2).
/// </summary>
/// <remarks>
/// <para>
/// Without it, the attempt would wait for whichever producer next asked the gate for something --
/// which after a rate limit is up to fifteen minutes away, and during setup, before a group is
/// chosen, is never. The banner would sit at "retrying now" with nothing retrying.
/// </para>
/// <para>
/// Asking every few seconds costs nothing: the gate answers from memory, and sends a request only
/// when a wait has actually ended.
/// </para>
/// </remarks>
public sealed class SignInResumeService(IVRChatGate gate, IDelayScheduler delays) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failing = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await delays.DelayAsync(Interval, stoppingToken).ConfigureAwait(false);
                await gate.ResumeAfterWaitAsync(stoppingToken).ConfigureAwait(false);
                failing = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The database being briefly unreachable must not end the loop: the next attempt
                // after a wait depends on it. Logged once per run of failures, not every five
                // seconds.
                if (!failing)
                    Log.Warning(ex, "Could not check whether the wait to sign in to VRChat is over");

                failing = true;
            }
        }
    }
}
