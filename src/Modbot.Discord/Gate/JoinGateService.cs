using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
using Serilog;

namespace Modbot.Discord.Gate;

/// <summary>
/// Runs <see cref="JoinGate"/> once a minute while the bot has a ready session, and at once when
/// somebody joins or the hold is lifted (join gate design §6).
/// </summary>
/// <remarks>
/// Its own loop, like the linked roles: a server where the bot cannot give the member role must not
/// hold up the moderation log. With no ready session there is no pass at all, which is what keeps
/// time from counting against anybody while the bot is away.
/// </remarks>
public sealed class JoinGateService : BackgroundService
{
    public static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly DiscordBotStatus _status;
    private readonly JoinGateState _state;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public JoinGateService(
        IServiceScopeFactory scopes,
        DiscordBotService bot,
        DiscordBotStatus status,
        JoinGateState state,
        IModbotClock clock,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _bot = bot;
        _status = status;
        _state = state;
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
                    var pass = await scope.ServiceProvider.GetRequiredService<JoinGate>()
                        .RunAsync(gateway, stoppingToken)
                        .ConfigureAwait(false);

                    if (pass.Passed + pass.LetInInDiscord + pass.Removed > 0)
                    {
                        _log.Information(
                            "Join gate: {Passed} got in, {InDiscord} were let in in Discord, {Removed} were removed",
                            pass.Passed, pass.LetInInDiscord, pass.Removed);
                    }

                    if (pass.Problem is { } problem)
                        _status.Problem(problem, _clock.UtcNow);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _log.Warning(e, "The join gate pass failed; trying again shortly");
                }
            }

            try
            {
                await _state.WaitAsync(PassInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>
/// What a moderator does at the join gate from Modbot, through the bot's live session (join gate
/// design §7). Let in, Remove and Pause invites need the bot; holding and lifting do not, but answer
/// the same way so the API has one place to ask.
/// </summary>
public sealed class JoinGateActions : IJoinGateActions
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordBotService _bot;
    private readonly JoinGateState _state;

    public JoinGateActions(IServiceScopeFactory scopes, DiscordBotService bot, JoinGateState state)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(state);

        _scopes = scopes;
        _bot = bot;
        _state = state;
    }

    public async Task<T> RunAloneAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        T result;
        using (await _state.LockAsync(ct).ConfigureAwait(false))
            result = await work().ConfigureAwait(false);

        _state.Wake();
        return result;
    }

    public Task<JoinGateOutcome> LetInAsync(string discordUserId, Guid by, CancellationToken ct = default)
        => WithBotAsync((gate, gateway) => gate.LetInAsync(gateway, discordUserId, by, ct));

    public Task<JoinGateOutcome> RemoveAsync(string discordUserId, Guid by, string byName, CancellationToken ct = default)
        => WithBotAsync((gate, gateway) => gate.RemoveAsync(gateway, discordUserId, by, byName, ct));

    public Task<JoinGateOutcome> PauseInvitesAsync(Guid by, CancellationToken ct = default)
        => WithBotAsync((gate, gateway) => gate.PauseInvitesAsync(gateway, by, ct));

    public async Task<JoinGateOutcome> HoldAsync(Guid by, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JoinGate>().HoldAsync(by, ct).ConfigureAwait(false);
    }

    public async Task<JoinGateOutcome> LiftHoldAsync(Guid by, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JoinGate>().LiftHoldAsync(by, ct).ConfigureAwait(false);
    }

    private async Task<JoinGateOutcome> WithBotAsync(Func<JoinGate, Gateway.IDiscordGateway, Task<JoinGateOutcome>> act)
    {
        if (_bot.ReadyGateway is not { } gateway)
            return JoinGateOutcome.Offline;

        using var scope = _scopes.CreateScope();
        return await act(scope.ServiceProvider.GetRequiredService<JoinGate>(), gateway).ConfigureAwait(false);
    }
}
