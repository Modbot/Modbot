using System.Security.Cryptography;
using System.Text;

namespace Modbot.Api.Features.Onboarding;

/// <summary>
/// The one-time code the setup wizard asks for until the first staff account exists.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the first person to reach a fresh deployment owned it: every wizard endpoint was
/// open until an account existed, and both Compose files published the port on every address. A
/// public Railway address or a VPS port is found within minutes, and the owner of the first account
/// is who the group's VRChat login is then typed into. The code is the proof that whoever is
/// creating the account can also read this server's console (first-run setup code design §2).
/// </para>
/// <para>
/// It lives in memory only. Nothing is written to a table, so there is nothing to clean up and
/// nothing to leak from a backup. It comes from <c>MODBOT_SETUP_CODE</c> when that is set, and is
/// made at random otherwise, so every boot without an account prints a new one. Once an account
/// exists it is dropped and never asked for again: from then on the wizard needs a session.
/// </para>
/// </remarks>
public sealed class SetupCode
{
    /// <summary>The request header the wizard sends the code in.</summary>
    public const string Header = "X-Setup-Code";

    /// <summary>
    /// Thirty letters and digits, with the ones that are easy to misread left out: no 0 or O, no 1,
    /// I or L, no 5 beside S. Twelve of them is about 59 bits.
    /// </summary>
    private const string Letters = "ABCDEFGHJKMNPQRSTUVWXYZ2346789";

    private const int GroupLength = 4;
    private const int Groups = 3;

    private string? _code;

    /// <param name="fromEnvironment">
    /// <c>MODBOT_SETUP_CODE</c>, or null for a random one.
    /// </param>
    public SetupCode(string? fromEnvironment)
    {
        // A value that is nothing but dashes and spaces would compare equal to an empty answer.
        _code = fromEnvironment is null || Normalize(fromEnvironment).Length == 0
            ? MakeRandom()
            : fromEnvironment.Trim();
    }

    /// <summary>The code, or null once it has been dropped.</summary>
    public string? Current => Volatile.Read(ref _code);

    /// <summary>
    /// Whether <paramref name="given"/> is the code. Case, spaces and dashes do not matter, since the
    /// code is read off a console and typed by hand.
    /// </summary>
    /// <remarks>
    /// Compared in constant time, so how long a wrong answer takes says nothing about how much of
    /// it was right.
    /// </remarks>
    public bool Matches(string? given)
    {
        var code = Current;
        if (code is null || given is null)
            return false;

        var answer = Normalize(given);
        if (answer.Length == 0)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Normalize(code)),
            Encoding.UTF8.GetBytes(answer));
    }

    /// <summary>Forgets the code. Called once the first account exists.</summary>
    public void Drop() => Volatile.Write(ref _code, null);

    private static string Normalize(string value)
    {
        var kept = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (c == '-' || char.IsWhiteSpace(c))
                continue;

            kept.Append(char.ToUpperInvariant(c));
        }

        return kept.ToString();
    }

    private static string MakeRandom()
    {
        var picked = RandomNumberGenerator.GetItems<char>(Letters, GroupLength * Groups);
        var text = new StringBuilder(GroupLength * Groups + Groups - 1);

        for (var i = 0; i < picked.Length; i++)
        {
            if (i > 0 && i % GroupLength == 0)
                text.Append('-');

            text.Append(picked[i]);
        }

        return text.ToString();
    }
}
