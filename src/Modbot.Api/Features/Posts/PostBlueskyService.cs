using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Api.Features.Calendar;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Api.Features.Posts;

/// <summary>
/// Runs <see cref="PostBlueskySender"/> every thirty seconds (Bluesky design §3.8, posts design §4.2c).
/// </summary>
/// <remarks>
/// Its own loop, apart from Discord's and VRChat's, so Bluesky posts go whether or not the bot is
/// connected or VRChat is signed in, and a limit from Bluesky stops nothing else. A pass with Bluesky
/// not set up, or Posting off, reads the settings row and the waiting rows and nothing more. Each pass
/// first lets <see cref="CalendarBlueskyPlanner"/> make and take down the posts of calendar events, and
/// <see cref="CalendarBlueskyFirstJoin"/> make the "We're live" replies, <see cref="CalendarBlueskyLineupPass"/>
/// makes the weekly line-up; <see cref="CalendarBlueskyPin"/>
/// then keeps the next date's post pinned on the profile. <see cref="PostBlueskyCounts"/> reads the
/// numbers under sent posts from Bluesky's public API, with its own stop on a rate limit.
/// </remarks>
public sealed class PostBlueskyService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public PostBlueskyService(
        IServiceScopeFactory scopes,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();

                // The events' posts are made and taken down first, so the sender sends what they made.
                await scope.ServiceProvider.GetRequiredService<CalendarBlueskyPlanner>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);

                // So are the replies for a first join, which the sender then sends.
                await scope.ServiceProvider.GetRequiredService<CalendarBlueskyFirstJoin>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);

                // And the week's line-up, once a week at the time set in Settings.
                await scope.ServiceProvider.GetRequiredService<CalendarBlueskyLineupPass>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);

                await scope.ServiceProvider.GetRequiredService<PostBlueskySender>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);

                // The numbers under the posts already sent: one call at most, a limit on it stops only this.
                await scope.ServiceProvider.GetRequiredService<PostBlueskyCounts>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);

                // The pin comes last, so a date post the sender just sent can be pinned at once.
                await scope.ServiceProvider.GetRequiredService<CalendarBlueskyPin>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The Bluesky posts pass failed; it will run again");
            }

            try
            {
                await _delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
