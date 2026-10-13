using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Api.Features.LivePosts;

/// <summary>
/// Runs <see cref="LivePostPass"/> every 30 seconds, a little slower than the group instance poll it reads.
/// </summary>
/// <remarks>
/// Its own loop, apart from the posting senders'. A pass with the setting off reads the settings
/// row and nothing more.
/// </remarks>
public sealed class LivePostService : BackgroundService
{
    /// <summary>How long between two passes.</summary>
    public static readonly TimeSpan PassEvery = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public LivePostService(
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
                await scope.ServiceProvider.GetRequiredService<LivePostPass>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The live post pass failed; it will run again");
            }

            try
            {
                await _delay(PassEvery, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
