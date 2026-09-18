using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Modbot.Core.Giveaways;

/// <summary>One entrant as the draw sees them: a place in the list and a weight.</summary>
/// <param name="Position">Where they sit in the frozen list. The list is walked in this order.</param>
/// <param name="Key">Who they are, as the snapshot names them. Opaque text.</param>
/// <param name="Weight">A whole number, at least one. Zero means they cannot win.</param>
public readonly record struct GiveawayTicket(int Position, string Key, long Weight);

/// <summary>Who won, and in what order.</summary>
/// <param name="Rank">1 for the first name out, 2 for the second, and so on.</param>
public readonly record struct GiveawayWinner(int Rank, int Position, string Key);

/// <summary>
/// The seed, its promise, and the pick itself.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole of the fairness claim, and it is deliberately small.</strong> Modbot
/// publishes a hash of the seed before the draw and the seed itself afterwards, and stores the
/// entrant list exactly as it drew from it (giveaways design §5). Anyone who kept the hash can
/// check the seed matches, and anyone with the list and the seed can work the winners out again
/// — with a hash function, integer addition and a remainder, and nothing else.
/// </para>
/// <para>
/// No floating point anywhere. A draw that could come out differently depending on how somebody's
/// language rounds a double is not reproducible, and a fairness mechanism that only works on one
/// machine is not a fairness mechanism.
/// </para>
/// </remarks>
public static class GiveawayDraws
{
    /// <summary>How many bytes of randomness a seed carries.</summary>
    public const int SeedBytes = 32;

    /// <summary>A fresh seed, as lower-case hex.</summary>
    public static string NewSeed() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SeedBytes));

    /// <summary>
    /// The promise Modbot publishes before drawing: SHA-256 of the seed's text, as lower-case hex.
    /// </summary>
    public static string Promise(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    }

    /// <summary>Whether a revealed seed is the one that was promised.</summary>
    public static bool Keeps(string promise, string seed)
        => string.Equals(promise, Promise(seed), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The number for one round of the draw, from the seed alone.
    /// </summary>
    /// <remarks>
    /// HMAC-SHA256 of the text <c>round:0</c>, <c>round:1</c> and so on, keyed by the seed's bytes;
    /// the first eight bytes of the answer read as a big-endian whole number. Every language has
    /// this, which is the point.
    /// </remarks>
    public static ulong RoundNumber(string seed, int round)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentOutOfRangeException.ThrowIfNegative(round);

        var mixed = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(seed),
            Encoding.UTF8.GetBytes("round:" + round.ToString(CultureInfo.InvariantCulture)));

        return BinaryPrimitives.ReadUInt64BigEndian(mixed);
    }

    /// <summary>
    /// Draws <paramref name="winners"/> names from the list, in order, without drawing anybody twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each round: add up the weights still in the hat, take the round's number modulo that total,
    /// then walk the list in position order adding weights until the running total passes it. That
    /// person wins and comes out of the hat.
    /// </para>
    /// <para>
    /// The modulo is very slightly biased towards the front of the list — by about one part in
    /// 2^64 divided by the total weight, which for any group that has ever existed is far below
    /// the point at which anything else about the draw is exact. Rejection sampling would remove
    /// it and would make the recipe something people cannot follow by hand, which costs more than
    /// the bias does.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GiveawayWinner> Draw(IReadOnlyList<GiveawayTicket> tickets, int winners, string seed)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(seed);

        var hat = tickets.Where(t => t.Weight > 0).OrderBy(t => t.Position).ToList();
        var drawn = new List<GiveawayWinner>();

        for (var round = 0; round < winners && hat.Count > 0; round++)
        {
            var total = hat.Sum(t => t.Weight);
            if (total <= 0)
                break;

            var target = RoundNumber(seed, round) % (ulong)total;

            long running = 0;
            var index = hat.Count - 1;

            for (var i = 0; i < hat.Count; i++)
            {
                running += hat[i].Weight;

                if ((ulong)running > target)
                {
                    index = i;
                    break;
                }
            }

            drawn.Add(new GiveawayWinner(round + 1, hat[index].Position, hat[index].Key));
            hat.RemoveAt(index);
        }

        return drawn;
    }
}
