using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Logging;
using Modbot.Core.Supporters;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Serilog;

namespace Modbot.Discord.Supporters;

/// <summary>
/// Runs <see cref="SupporterRoles"/> once a minute while the bot has a ready session, and at once
/// when a payment comes in or staff link a supporter.
/// </summary>
/// <remarks>
/// Its own loop, like the linked member roles, so a server where the bot lacks Manage Roles does not
/// hold up anything else the bot does.
/// </remarks>
public sealed class SupporterRoleService : BackgroundService
{
    public static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly DiscordBotStatus _status;
    private readonly SupporterSignal _signal;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public SupporterRoleService(
        IServiceScopeFactory scopes,
        DiscordBotService bot,
        DiscordBotStatus status,
        SupporterSignal signal,
        IModbotClock clock,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _bot = bot;
        _status = status;
        _signal = signal;
        _clock = clock;
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
                    var roles = scope.ServiceProvider.GetRequiredService<SupporterRoles>();
                    var pass = await roles.RunAsync(gateway, stoppingToken).ConfigureAwait(false);

                    if (pass.Given + pass.Taken + pass.Thanked > 0)
                    {
                        _log.Information(
                            "Supporter perks: gave {Given} and took away {Taken} roles, sent {Thanked} thank-you messages",
                            pass.Given, pass.Taken, pass.Thanked);
                    }

                    if (pass.Problem is { } problem)
                        _status.Problem($"Could not change a supporter's role: {problem}", _clock.UtcNow);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _log.Warning(e, "The supporter roles pass failed; trying again shortly");
                }
            }

            try
            {
                await _signal.WaitAsync(PassInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
