using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Users;

/// <summary>
/// The codes staff run <c>/verify</c> with to prove which Discord account is theirs, without
/// Discord sign-in (Discord account linking design §14): handing one out, and finding the one a
/// command names.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a command proves anything.</strong> When somebody runs a slash command, Discord
/// itself says which account ran it; nobody can run one as somebody else. The code says which
/// Modbot account asked, because only that account, signed in, was shown it. Together they are the
/// same proof Discord sign-in gives, with no client secret to set up.
/// </para>
/// <para>
/// <strong>Six characters, two halves.</strong> Shown as <c>K7P-42Q</c>, from the alphabet the
/// companion's pairing codes use (no <c>0/O</c>, <c>1/I/L</c> or <c>U</c>). The first half finds
/// the code, the second half is checked. A wrong code whose first half matches a live code counts
/// against that code, and after <see cref="MaxFailedTries"/> it stops working, so many Discord
/// accounts guessing at once cannot wear one down; a wrong code that matches nothing costs nobody
/// anything, so a member running <c>/verify</c> with rubbish cannot use up somebody's code. On top
/// of that each Discord account gets <c>MemberCommandLimits.PerMinute</c> tries a minute.
/// </para>
/// <para>
/// <strong>Short-lived and used once.</strong> <see cref="Lifetime"/>, from <c>IModbotClock</c>; the
/// row goes the moment the code is used. Stored as it is, like <see cref="DiscordLinkCode"/>, so
/// the account page can show it again after a reload: it is worth nothing after a quarter of an
/// hour, and whoever can read the table can write the account row anyway.
/// </para>
/// </remarks>
public static class StaffDiscordCodes
{
    /// <summary>The same alphabet as the companion's pairing codes: nothing that reads as something else.</summary>
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Characters in a code, not counting the dash.</summary>
    public const int Length = 6;

    /// <summary>The first half, which finds the code.</summary>
    public const int FindLength = 3;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Wrong codes that began like a live one before that one stops working.</summary>
    public const int MaxFailedTries = 5;

    /// <summary>The command, as the account page shows it and Discord registers it.</summary>
    public const string Command = "verify";

    /// <summary>A code as people read it: <c>K7P-42Q</c>.</summary>
    public static string Show(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.Length == Length ? code[..FindLength] + "-" + code[FindLength..] : code;
    }

    /// <summary>
    /// What somebody typed, as a stored code: upper case, with spaces and dashes taken out, and the
    /// command in front of it too when the whole <c>/verify K7P-42Q</c> the account page shows was
    /// pasted into the code. Null when it cannot be one.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null)
            return null;

        var text = typed.Trim();
        foreach (var prefix in (string[])["/" + Command, "code:"])
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                text = text[prefix.Length..].TrimStart();
        }

        var sb = new StringBuilder(Length);
        foreach (var c in text)
        {
            if (c is '-' or ' ' or '‐' or '‑' or '‒' or '–' or '—' || char.IsWhiteSpace(c))
                continue;

            var upper = char.ToUpperInvariant(c);
            if (!Alphabet.Contains(upper, StringComparison.Ordinal) || sb.Length == Length)
                return null;

            sb.Append(upper);
        }

        return sb.Length == Length ? sb.ToString() : null;
    }

    /// <summary>
    /// A new code for <paramref name="userId"/>, replacing any it had. Its first half is one no
    /// other live code has, so somebody's typing mistakes never count against somebody else's code.
    /// Saves.
    /// </summary>
    public static async Task<StaffDiscordCode> IssueAsync(ModbotContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Codes nobody can use any more go whenever a new one is made, so the table stays as small
        // as the number of people connecting right now.
        await db.StaffDiscordCodes
            .Where(c => c.UserId != userId && c.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        var taken = await db.StaffDiscordCodes.AsNoTracking()
            .Where(c => c.UserId != userId)
            .Select(c => c.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var takenFirstHalves = taken
            .Where(c => c.Length >= FindLength)
            .Select(c => c[..FindLength])
            .ToHashSet(StringComparer.Ordinal);

        var code = NewCode();
        for (var i = 0; i < 20 && takenFirstHalves.Contains(code[..FindLength]); i++)
            code = NewCode();

        var row = await db.StaffDiscordCodes.FirstOrDefaultAsync(c => c.UserId == userId, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new StaffDiscordCode { UserId = userId };
            db.StaffDiscordCodes.Add(row);
        }

        row.Code = code;
        row.ExpiresAt = now + Lifetime;
        row.FailedTries = 0;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>The live code <paramref name="userId"/> holds, or null.</summary>
    public static Task<StaffDiscordCode?> LiveAsync(ModbotContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.StaffDiscordCodes.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.ExpiresAt > now && c.FailedTries < MaxFailedTries, ct);
    }

    /// <summary>
    /// The live code <paramref name="typed"/> names, tracked so the caller can remove it once it is
    /// used; or null. A wrong code whose first half matches a live one counts against that one, and
    /// that is saved here.
    /// </summary>
    public static async Task<StaffDiscordCode?> FindAsync(ModbotContext db, string? typed, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (Normalize(typed) is not { } code)
            return null;

        var firstHalf = code[..FindLength];

        var candidates = await db.StaffDiscordCodes
            .Where(c => c.Code.StartsWith(firstHalf) && c.ExpiresAt > now && c.FailedTries < MaxFailedTries)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var match = candidates.FirstOrDefault(c => Same(c.Code, code));
        if (match is not null)
            return match;

        if (candidates.Count > 0)
        {
            foreach (var candidate in candidates)
                candidate.FailedTries++;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return null;
    }

    private static string NewCode()
    {
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(chars);
    }

    /// <summary>Compared in fixed time, so how long a wrong answer takes says nothing about how close it was.</summary>
    private static bool Same(string stored, string presented)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(presented));
}
