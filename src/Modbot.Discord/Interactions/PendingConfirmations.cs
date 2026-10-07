using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Modbot.Discord.Interactions;

/// <summary>Something started from Discord that waits for a press before it acts.</summary>
public interface IPendingConfirmation
{
    /// <summary>What the form and the buttons carry, and the key that makes one confirmation act once.</summary>
    string Token { get; }

    /// <summary>Who started it; nobody else may confirm it.</summary>
    string DiscordUserId { get; }

    /// <summary>When it was started, on Modbot's clock.</summary>
    DateTimeOffset StartedAt { get; }
}

/// <summary>
/// The things started from Discord and not finished yet, of one kind, held in memory for
/// <see cref="Lifetime"/> (the same rule as <see cref="PendingStaffActions"/>, for the commands that
/// are not about a person: <c>/event cancel-date</c> and <c>/post new</c>).
/// </summary>
/// <remarks>
/// <para>
/// In memory, per process, on purpose. Discord lets a reply be changed for fifteen minutes, so a
/// confirmation older than that could not be answered properly anyway. A restart forgets them all,
/// and the confirmation then says it has run out: the person starts the command again, which costs
/// one command and never a second post or cancel, because nothing was sent.
/// </para>
/// <para>
/// A confirmation is not removed when it is pressed. It stays until it runs out, so a second press
/// finds it and <see cref="RunOnceAsync"/> answers with the first result instead of acting again.
/// <see cref="MaxHeld"/> keeps one person pressing a command over and over from filling memory.
/// </para>
/// </remarks>
/// <typeparam name="TItem">What is waiting.</typeparam>
/// <typeparam name="TAnswer">What acting on it answers.</typeparam>
public sealed class PendingConfirmations<TItem, TAnswer>
    where TItem : class, IPendingConfirmation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public const int MaxHeld = 1000;

    private readonly ConcurrentDictionary<string, TItem> _held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<TAnswer>>> _ran = new(StringComparer.Ordinal);

    /// <summary>A new token: 24 random hex characters, short enough to sit in a button id.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>Holds one. False when too many are held already.</summary>
    public bool TryAdd(TItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);

        return Room(now) && _held.TryAdd(item.Token, item);
    }

    /// <summary>The one held under this token, or null when there is none or it has run out.</summary>
    public TItem? Get(string token, DateTimeOffset now)
        => _held.TryGetValue(token, out var held) && now - held.StartedAt < Lifetime ? held : null;

    /// <summary>Replaces what is held under the token.</summary>
    public void Update(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _held[item.Token] = item;
    }

    public void Forget(string token)
    {
        _held.TryRemove(token, out _);
        _ran.TryRemove(token, out _);
    }

    /// <summary>
    /// Runs <paramref name="run"/> for this confirmation the first time it is asked, and gives every
    /// later ask the first answer without running it again: one confirmation acts once. Even a throw
    /// is kept, so a second press cannot send what the first may already have sent.
    /// </summary>
    /// <returns>The answer, and whether this call was the one that ran.</returns>
    public async Task<(TAnswer Answer, bool First)> RunOnceAsync(string token, Func<Task<TAnswer>> run)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(run);

        var mine = new Lazy<Task<TAnswer>>(run, LazyThreadSafetyMode.ExecutionAndPublication);
        var held = _ran.GetOrAdd(token, mine);

        return (await held.Value.ConfigureAwait(false), ReferenceEquals(held, mine));
    }

    private bool Room(DateTimeOffset now)
    {
        if (_held.Count < MaxHeld)
            return true;

        foreach (var (token, item) in _held)
        {
            if (now - item.StartedAt >= Lifetime)
            {
                _held.TryRemove(token, out _);
                _ran.TryRemove(token, out _);
            }
        }

        return _held.Count < MaxHeld;
    }
}
