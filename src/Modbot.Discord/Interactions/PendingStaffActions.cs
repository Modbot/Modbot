using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Modbot.Discord.Interactions;

/// <summary>An action somebody started from Discord and has not confirmed yet.</summary>
/// <param name="Token">What the form and the confirmation's buttons carry, and the action's key.</param>
/// <param name="DiscordUserId">Who started it; nobody else may confirm it.</param>
/// <param name="Action">The web app's word: ban, kick, approve, reject.</param>
/// <param name="SubjectId">The VRChat person it is about.</param>
/// <param name="SubjectName">Their name as Modbot knows it, or the id when it does not.</param>
/// <param name="CardChannelId">The card it was started from, to mark once it is done; null from a private reply.</param>
/// <param name="CardMessageId">Beside <paramref name="CardChannelId"/>.</param>
/// <param name="StartedAt">When the button was pressed, on Modbot's clock.</param>
/// <param name="ReasonIds">The reasons picked in the form. Empty until then.</param>
/// <param name="ReasonLabels">The same reasons as words, for the confirmation.</param>
/// <param name="Note">The note written in the form.</param>
/// <param name="Ready">The form has been sent and checked, so the confirmation may act.</param>
public sealed record PendingStaffAction(
    string Token,
    string DiscordUserId,
    string Action,
    string SubjectId,
    string SubjectName,
    string? CardChannelId,
    string? CardMessageId,
    DateTimeOffset StartedAt,
    IReadOnlyList<Guid> ReasonIds,
    IReadOnlyList<string> ReasonLabels,
    string Note,
    bool Ready)
{
    /// <summary>The key the moderation service claims, so one confirmation acts once (M4 §4.3).</summary>
    public string Key => "discord:" + Token;
}

/// <summary>
/// The actions started from Discord and not finished yet, held in memory for
/// <see cref="Lifetime"/> (acting from Discord design §4).
/// </summary>
/// <remarks>
/// <para>
/// In memory, per process, on purpose. Discord lets a reply be changed for fifteen minutes, so a
/// confirmation older than that could not be answered properly anyway. A restart forgets them all,
/// and the confirmation then says it has run out: the moderator presses the card's button again,
/// which costs one press and never a second ban, because nothing was sent.
/// </para>
/// <para>
/// A confirmation is not removed when it is pressed. It stays until it runs out, so a second press
/// of the same confirmation finds it and runs it with the same key, and the moderation service
/// answers with the first result rather than acting again.
/// </para>
/// <para>
/// <see cref="MaxHeld"/> keeps one person pressing every button in a channel from filling memory.
/// When it is reached, what has run out is dropped; when nothing has, new ones are refused.
/// </para>
/// </remarks>
public sealed class PendingStaffActions
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public const int MaxHeld = 1000;

    private readonly ConcurrentDictionary<string, PendingStaffAction> _actions = new(StringComparer.Ordinal);

    /// <summary>A new token: 24 random hex characters, short enough to sit in a button id.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>Holds an action. False when too many are held already.</summary>
    public bool TryAdd(PendingStaffAction action, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!Room(now))
            return false;

        return _actions.TryAdd(action.Token, action);
    }

    /// <summary>The action held under this token, or null when there is none or it has run out.</summary>
    public PendingStaffAction? Action(string token, DateTimeOffset now)
        => _actions.TryGetValue(token, out var held) && now - held.StartedAt < Lifetime ? held : null;

    /// <summary>Replaces what is held under the action's token.</summary>
    public void Update(PendingStaffAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions[action.Token] = action;
    }

    public void Forget(string token) => _actions.TryRemove(token, out _);

    private bool Room(DateTimeOffset now)
    {
        if (_actions.Count < MaxHeld)
            return true;

        foreach (var (token, held) in _actions)
        {
            if (now - held.StartedAt >= Lifetime)
                _actions.TryRemove(token, out _);
        }

        return _actions.Count < MaxHeld;
    }
}
