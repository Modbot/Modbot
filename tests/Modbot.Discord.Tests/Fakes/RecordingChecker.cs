using System.Collections.Concurrent;
using Modbot.Core.Moderation;

namespace Modbot.Discord.Tests.Fakes;

/// <summary>An AI moderation checker that remembers every Discord message it was handed.</summary>
public sealed class RecordingChecker : IModerationChecker
{
    private readonly ConcurrentQueue<DiscordMessageToCheck> _checked = new();

    public IReadOnlyList<DiscordMessageToCheck> Checked => _checked.ToArray();

    public Task<ModerationOutcome> CheckDiscordMessageAsync(DiscordMessageToCheck message, CancellationToken ct = default)
    {
        _checked.Enqueue(message);
        return Task.FromResult(ModerationOutcome.Nothing);
    }

    public Task<ModerationOutcome> CheckProfileAsync(ProfileToCheck profile, CancellationToken ct = default)
        => Task.FromResult(ModerationOutcome.Nothing);
}
