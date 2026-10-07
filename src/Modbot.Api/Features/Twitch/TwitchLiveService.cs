using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Core.Twitch;
using Serilog;

namespace Modbot.Api.Features.Twitch;

/// <summary>
/// Runs <see cref="TwitchLivePass"/> once a minute (Twitch design, step 1).
/// </summary>
/// <remarks>
/// Its own loop, apart from the posting senders', so a limit from Twitch stops nothing else and a
/// slow Twitch holds no post up. A pass with Twitch not set up, or the poll off, reads the settings
/// row and nothing more.
/// </remarks>
public sealed class TwitchLiveService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public TwitchLiveService(
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
                await scope.ServiceProvider.GetRequiredService<TwitchLivePass>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The Twitch poll failed; it will run again");
            }

            try
            {
                await _delay(TwitchRules.PollEvery, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
