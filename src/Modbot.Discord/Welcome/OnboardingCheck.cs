using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Welcome;

/// <summary>
/// Whether a Discord server has onboarding, read from Discord at most once an hour per server
/// (smart welcome design §3.2). Every new member asks; Discord is asked once.
/// </summary>
/// <remarks>
/// A read that failed (a refusal, Discord down) is kept for five minutes, so a rate limit is not
/// answered by asking again with every join.
/// </remarks>
public sealed class OnboardingCheck
{
    /// <summary>How long an answer from Discord is kept.</summary>
    public static readonly TimeSpan KeepAnswer = TimeSpan.FromHours(1);

    /// <summary>How long a failed read is kept.</summary>
    public static readonly TimeSpan KeepFailure = TimeSpan.FromMinutes(5);

    private readonly IModbotClock _clock;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<string, (bool? On, DateTimeOffset ReadAt)> _known = new(StringComparer.Ordinal);

    public OnboardingCheck(IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>True when the server has onboarding, false when it has none, null when Discord could not say.</summary>
    public async Task<bool?> IsOnAsync(IDiscordGateway gateway, string guildId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentException.ThrowIfNullOrEmpty(guildId);

        await _one.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var now = _clock.UtcNow;

            if (_known.TryGetValue(guildId, out var known)
                && now - known.ReadAt < (known.On is null ? KeepFailure : KeepAnswer))
                return known.On;

            var on = await gateway.ReadOnboardingOnAsync(guildId, ct).ConfigureAwait(false);
            _known[guildId] = (on, now);
            return on;
        }
        finally
        {
            _one.Release();
        }
    }
}
