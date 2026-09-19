using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.VRChat.RateLimiting;
using Serilog;

namespace Modbot.VRChat.Invites;

/// <summary>
/// Runs the auto-invite pass, one invite at a time (auto-invites design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Thirty seconds between passes, and at most one invite per pass.</strong> That is the
/// user's limit written where anybody can read it. <see cref="GroupInvites"/> refuses to go faster
/// regardless, and the <c>groups.invites</c> bucket is capped at the same rate, so all three would
/// have to be wrong at once for a second invite to slip out.
/// </para>
/// <para>
/// A pass that throws does not take the host down and does not stop the loop: the next one gets a
/// fresh scope and a fresh context, as the calendar's loop does. Nothing is kept between passes,
/// so there is nothing for a failed one to corrupt.
/// </para>
/// </remarks>
public sealed class GroupAutoInviteService : BackgroundService
{
    public static readonly TimeSpan Interval = GroupInvites.NoFasterThan;

    private readonly IServiceScopeFactory _scopes;
    private readonly IDelayScheduler _delays;
    private readonly ILogger _log;

    public GroupAutoInviteService(IServiceScopeFactory scopes, IDelayScheduler? delays = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
        _delays = delays ?? new RealDelayScheduler();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _delays.DelayAsync(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<GroupAutoInvites>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The auto-invite pass failed; it will run again");
            }
        }
    }
}
