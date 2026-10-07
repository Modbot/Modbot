using System.Collections.Concurrent;
using System.Security.Cryptography;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Interactions;

/// <summary>A "Report to mods" form that has been shown and not sent yet.</summary>
/// <param name="Token">What the form's id carries.</param>
/// <param name="ReporterDiscordId">Who opened it; nobody else may send it.</param>
/// <param name="Message">The message the menu was used on, which the form's id has no room for.</param>
/// <param name="StartedAt">When the form was shown, on Modbot's clock.</param>
public sealed record PendingReport(string Token, string ReporterDiscordId, DiscordTargetMessage Message, DateTimeOffset StartedAt);

/// <summary>
/// The report forms shown and not sent yet, held in memory for <see cref="Lifetime"/> (Discord
/// commands design §3.4).
/// </summary>
/// <remarks>
/// In memory, per process, like <see cref="PendingStaffActions"/>: a form id holds 100 characters and
/// a message's words, attachment names and link do not fit in it. A restart forgets them, and the
/// form then says it has run out; the member uses the menu again, which costs one more click and
/// never a second report, because nothing was kept.
/// </remarks>
public sealed class PendingReports
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public const int MaxHeld = 1000;

    private readonly ConcurrentDictionary<string, PendingReport> _held = new(StringComparer.Ordinal);

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>Holds a form. False when too many are held already.</summary>
    public bool TryAdd(PendingReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (_held.Count >= MaxHeld)
        {
            foreach (var (token, held) in _held)
            {
                if (now - held.StartedAt >= Lifetime)
                    _held.TryRemove(token, out _);
            }

            if (_held.Count >= MaxHeld)
                return false;
        }

        return _held.TryAdd(report.Token, report);
    }

    /// <summary>The form held under this token, or null when there is none or it has run out.</summary>
    public PendingReport? Find(string token, DateTimeOffset now)
        => _held.TryGetValue(token, out var held) && now - held.StartedAt < Lifetime ? held : null;
}
