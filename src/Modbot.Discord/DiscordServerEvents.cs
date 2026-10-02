using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Bot;

namespace Modbot.Discord;

/// <summary>
/// <see cref="IDiscordServerEvents"/> through the live session, remembered for <see cref="KeepFor"/>
/// so a server is asked at most twelve times an hour (calendar design §16).
/// </summary>
/// <remarks>
/// The same rules as <see cref="DiscordOnlineCount"/>: one request at a time, so the Calendar page
/// and Health opened together make one; a failed read is remembered too, so a Discord that is
/// refusing is not asked on every page open; with the bot not connected nothing is asked and nothing
/// is remembered.
/// </remarks>
public sealed class DiscordServerEvents : IDiscordServerEvents
{
    /// <summary>How long one answer is kept.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);

    private readonly DiscordBotService _bot;
    private readonly IModbotClock _clock;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, DiscordServerEventList? List)> _kept = [];

    public DiscordServerEvents(DiscordBotService bot, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(clock);

        _bot = bot;
        _clock = clock;
    }

    public async Task<DiscordServerEventList?> ReadAsync(string guildId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(guildId))
            return null;

        guildId = guildId.Trim();

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_kept.TryGetValue(guildId, out var kept) && _clock.UtcNow - kept.At < KeepFor)
                return kept.List;

            if (_bot.ReadyGateway is not { } gateway)
                return null;

            var now = _clock.UtcNow;
            var events = await gateway.ReadServerEventsAsync(guildId, ct).ConfigureAwait(false);
            var list = events is null ? null : new DiscordServerEventList(events, now);

            _kept[guildId] = (now, list);
            return list;
        }
        finally
        {
            _one.Release();
        }
    }
}
