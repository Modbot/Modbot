using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Supporters;

/// <summary>
/// The short code a supporter pastes into Ko-fi's message box when they pay, so Modbot can match the
/// payment to their Discord account when Ko-fi gave no Discord link.
/// </summary>
/// <remarks>
/// <para>
/// A code is <see cref="Length"/> capitals and digits with no look-alikes (no 0, O, 1, I or L), so it
/// is read and typed without mistakes. A person keeps one code for good: asking again gives the same one.
/// </para>
/// <para>
/// <strong>Finding a code in a message</strong> is a match on whole words: the message is cut at
/// everything that is not a letter or a digit, and each piece of exactly <see cref="Length"/>
/// characters is a candidate, in any case. A code stuck to other letters is not found; that is the price of
/// not matching a piece of an ordinary word.
/// </para>
/// </remarks>
public sealed class SupporterCodes(ModbotContext db)
{
    public const int Length = 6;

    /// <summary>The most pieces of a message looked at, so a very long message costs no more than a long one.</summary>
    private const int MostCandidates = 50;

    private const int MostTries = 5;

    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>A new random code. Not yet checked against the codes already given.</summary>
    public static string Make() => new(RandomNumberGenerator.GetItems<char>(Alphabet, Length));

    /// <summary>
    /// The pieces of a payment's message that could be a code, in capitals and without repeats. A
    /// missing or empty message has none.
    /// </summary>
    public static IReadOnlyList<string> CandidatesIn(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return [];

        var found = new List<string>();
        var start = -1;

        for (var at = 0; at <= message.Length; at++)
        {
            var inWord = at < message.Length && char.IsAsciiLetterOrDigit(message[at]);

            if (inWord && start < 0)
            {
                start = at;
            }
            else if (!inWord && start >= 0)
            {
                if (at - start == Length)
                {
                    var piece = message.Substring(start, Length).ToUpperInvariant();

                    if (!found.Contains(piece))
                        found.Add(piece);
                }

                start = -1;

                if (found.Count >= MostCandidates)
                    break;
            }
        }

        return found;
    }

    /// <summary>The Discord account whose code is in the message, or null when none is.</summary>
    public async Task<string?> DiscordUserIdInAsync(string? message, CancellationToken ct = default)
    {
        var candidates = CandidatesIn(message).ToArray();

        if (candidates.Length == 0)
            return null;

        return await db.SupporterCodes.AsNoTracking()
            .Where(c => candidates.Contains(c.Code))
            .OrderBy(c => c.CreatedAt)
            .Select(c => c.DiscordUserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>The person's code: the one they already have, or a new one made now.</summary>
    public async Task<string> ForAsync(string discordUserId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);

        for (var attempt = 0; attempt < MostTries; attempt++)
        {
            var own = await db.SupporterCodes.AsNoTracking()
                .Where(c => c.DiscordUserId == discordUserId)
                .Select(c => c.Code)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (own is not null)
                return own;

            var row = new SupporterCode { DiscordUserId = discordUserId, Code = Make(), CreatedAt = now };
            db.SupporterCodes.Add(row);

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return row.Code;
            }
            catch (DbUpdateException)
            {
                // Somebody else's code is the same, or the same person asked twice at once: look again.
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not make a supporter code.");
    }
}
