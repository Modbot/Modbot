using System.Collections.Concurrent;
using System.Security.Cryptography;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Discord.Interactions;

/// <summary>Where an action acts: the VRChat group, the Discord server, or both.</summary>
[Flags]
public enum StaffActionWhere
{
    VRChat = 1,
    Discord = 2,
    Both = VRChat | Discord,
}

/// <summary>An action somebody started from Discord and has not confirmed yet.</summary>
/// <param name="Token">What the form and the confirmation's buttons carry, and the action's key.</param>
/// <param name="DiscordUserId">Who started it; nobody else may confirm it.</param>
/// <param name="Action">The web app's word: ban, kick, approve, reject.</param>
/// <param name="SubjectId">
/// The VRChat person it is about; for an action on the Discord server alone, their Discord id.
/// </param>
/// <param name="SubjectName">
/// Their name as the caller may see it: Modbot's name for them, a Discord member's name, or the id
/// when the caller may not see a VRChat name (<c>/ban</c> and <c>/kick</c> never name a VRChat person
/// to somebody without See profiles).
/// </param>
/// <param name="CardChannelId">The card it was started from, to mark once it is done; null from a private reply.</param>
/// <param name="CardMessageId">Beside <paramref name="CardChannelId"/>.</param>
/// <param name="StartedAt">When the button was pressed, on Modbot's clock.</param>
/// <param name="ReasonIds">The reasons picked in the form. Empty until then.</param>
/// <param name="ReasonLabels">The same reasons as words, for the confirmation.</param>
/// <param name="Note">The note written in the form.</param>
/// <param name="Ready">The form has been sent and checked, so the confirmation may act.</param>
/// <param name="Where">
/// Where it acts. A button under a card and <c>/ban</c> or <c>/kick</c> on a VRChat person are the
/// group; <c>/ban</c> and <c>/kick</c> can act on the Discord server alone, or on both.
/// </param>
/// <param name="TargetDiscordId">The Discord account acted on, when <paramref name="Where"/> includes the server.</param>
/// <param name="DeleteMessageDays">For a Discord ban: how many days of their messages Discord deletes too, 0 to 7.</param>
/// <param name="AlsoBansDiscord">
/// A ban of a VRChat person whose linked Discord account is banned with them (Modbot's rule for any
/// ban); the confirmation says so.
/// </param>
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
    bool Ready,
    StaffActionWhere Where = StaffActionWhere.VRChat,
    string? TargetDiscordId = null,
    int DeleteMessageDays = 0,
    bool AlsoBansDiscord = false)
{
    /// <summary>The key the moderation service claims, so one confirmation acts once (M4 §4.3).</summary>
    public string Key => "discord:" + Token;

    public bool OnVRChat => Where.HasFlag(StaffActionWhere.VRChat);

    public bool OnDiscord => Where.HasFlag(StaffActionWhere.Discord);

    /// <summary>
    /// Every permission the action needs, as the web app asks for it: the group's (Kick, Ban) for the
    /// group, and the Discord server's own (Remove from Discord, Ban on Discord) for the server. Both
    /// at once need both.
    /// </summary>
    public ModbotPermissions Needs
        => (OnVRChat ? StaffActionWords.Requires(Action) : ModbotPermissions.None)
            | (OnDiscord ? StaffActionWords.RequiresOnDiscord(Action) : ModbotPermissions.None);
}

/// <summary>What each place an action acted on answered. Null for a place it did not act on.</summary>
public sealed record StaffLegAnswers(StaffActionAnswer? VRChat, StaffActionAnswer? Discord);

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
    private readonly ConcurrentDictionary<string, Lazy<Task<StaffLegAnswers>>> _ran = new(StringComparer.Ordinal);

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

    public void Forget(string token)
    {
        _actions.TryRemove(token, out _);
        _ran.TryRemove(token, out _);
    }

    /// <summary>
    /// Runs <paramref name="run"/> for this confirmation the first time it is asked, and gives every
    /// later ask the first answer without running it again: one confirmation acts once.
    /// </summary>
    /// <remarks>
    /// For an action on the Discord server, which has no key of its own to claim (Discord answers
    /// "already banned" to a second request, but a second request is still one more call and one
    /// more audit entry). An action on the group has the moderation service's <c>discord:&lt;token&gt;</c>
    /// key instead, and is not run through here when it acts on the group alone. Even a throw is
    /// kept, so a second press cannot send what the first may already have sent.
    /// </remarks>
    /// <returns>The answers, and whether this call was the one that ran.</returns>
    public async Task<(StaffLegAnswers Answers, bool First)> RunOnceAsync(string token, Func<Task<StaffLegAnswers>> run)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(run);

        var mine = new Lazy<Task<StaffLegAnswers>>(run, LazyThreadSafetyMode.ExecutionAndPublication);
        var held = _ran.GetOrAdd(token, mine);

        return (await held.Value.ConfigureAwait(false), ReferenceEquals(held, mine));
    }

    private bool Room(DateTimeOffset now)
    {
        if (_actions.Count < MaxHeld)
            return true;

        foreach (var (token, held) in _actions)
        {
            if (now - held.StartedAt >= Lifetime)
            {
                _actions.TryRemove(token, out _);
                _ran.TryRemove(token, out _);
            }
        }

        return _actions.Count < MaxHeld;
    }
}
