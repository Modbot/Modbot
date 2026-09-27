using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Bot;

namespace Modbot.Discord;

/// <summary>
/// <see cref="IDiscordOnlineCount"/> through the live session, remembered for
/// <see cref="KeepFor"/> so a server is asked at most twelve times an hour.
/// </summary>
/// <remarks>
/// One request at a time: two pages opened together make one request, and the second reads what
/// the first brought back. A request that failed is remembered too, so a Discord that is refusing
/// is not asked again on every page open; the page shows no count until the time is up. With the
/// bot not connected nothing is asked and nothing is remembered.
/// </remarks>
public sealed class DiscordOnlineCount : IDiscordOnlineCount
{
    /// <summary>How long one answer is kept: five minutes, the rate the owner chose.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);

    private readonly DiscordBotService _bot;
    private readonly IModbotClock _clock;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, int? Count)> _kept = [];

    public DiscordOnlineCount(DiscordBotService bot, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(clock);

        _bot = bot;
        _clock = clock;
    }

    public async Task<int?> ReadAsync(string guildId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(guildId))
            return null;

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_kept.TryGetValue(guildId, out var kept) && _clock.UtcNow - kept.At < KeepFor)
                return kept.Count;

            if (_bot.ReadyGateway is not { } gateway)
                return null;

            var count = await gateway.ReadOnlineCountAsync(guildId, ct).ConfigureAwait(false);
            _kept[guildId] = (_clock.UtcNow, count);
            return count;
        }
        finally
        {
            _one.Release();
        }
    }
}
