using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Discord.Bot;
using Serilog;

namespace Modbot.Discord.StaffReminders;

/// <summary>
/// Runs <see cref="StaffReminderPoster"/> every quarter of an hour, only while the bot has a ready session.
/// </summary>
/// <remarks>
/// Its own loop, like the alerts: a deleted reminder channel must not hold up anything else. A reminder
/// is for a day, so a quarter of an hour is soon enough.
/// </remarks>
public sealed class StaffReminderService : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public StaffReminderService(
        IServiceScopeFactory scopes,
        DiscordBotService bot,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);

        _scopes = scopes;
        _bot = bot;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_bot.ReadyGateway is { } gateway)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<StaffReminderPoster>()
                        .RunOnceAsync(gateway, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Posting staff reminders to Discord failed; it will try again");
                }
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
