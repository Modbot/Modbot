using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Api.Features.Posts;

/// <summary>
/// Runs <see cref="PostListUpdater"/> every two minutes (posts design §8, Keep updated).
/// </summary>
/// <remarks>
/// Its own loop, apart from the senders', so an edit that is turned away holds no post up and a
/// pass that throws stops nothing else. A pass with no post using Keep updated reads one row.
/// </remarks>
public sealed class PostListService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public PostListService(
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
                await scope.ServiceProvider.GetRequiredService<PostListUpdater>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The posts' event list pass failed; it will run again");
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
