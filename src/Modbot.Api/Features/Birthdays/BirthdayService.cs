using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Serilog;

namespace Modbot.Api.Features.Birthdays;

/// <summary>
/// Runs <see cref="BirthdayPass"/> every five minutes. The post is at the top of an hour, so it
/// goes out within five minutes of it.
/// </summary>
/// <remarks>
/// Its own loop, apart from the posting senders'. A pass with birthdays off and no role to take back
/// reads the settings row and one small table, and nothing more.
/// </remarks>
public sealed class BirthdayService : BackgroundService
{
    /// <summary>How long between two passes. Also how long a refused role change waits before it is tried again.</summary>
    public static readonly TimeSpan PassEvery = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public BirthdayService(
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
                await scope.ServiceProvider.GetRequiredService<BirthdayPass>()
                    .RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "The birthday pass failed; it will run again");
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
