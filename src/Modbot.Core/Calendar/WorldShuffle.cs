using Modbot.Core.Data.Entities;

namespace Modbot.Core.Calendar;

/// <summary>
/// The shuffle a world list is picked in (world lists design §4): the whole list in a random order,
/// each world played once before any comes round again.
/// </summary>
/// <remarks>
/// Pure, so the rules can be tested without a database: <see cref="WorldPicker"/> loads the shuffle,
/// runs these, and saves it.
/// </remarks>
public static class WorldShuffle
{
    /// <summary>
    /// Brings a shuffle in line with the list's worlds now: a world taken out leaves the order, a
    /// world added joins this round at a random place, and a round with nothing left to play is
    /// followed by a new one.
    /// </summary>
    public static void Bring(WorldListShuffle shuffle, IReadOnlyList<string> worlds, Random random)
    {
        ArgumentNullException.ThrowIfNull(shuffle);
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(random);

        var inList = new HashSet<string>(worlds, StringComparer.Ordinal);

        var order = shuffle.Order.Where(inList.Contains).Distinct(StringComparer.Ordinal).ToList();
        var inOrder = new HashSet<string>(order, StringComparer.Ordinal);
        var played = shuffle.Played.Where(inOrder.Contains).Distinct(StringComparer.Ordinal).ToList();

        // Inserting each new world at a random place builds a fair random order from nothing, and
        // puts a world added part-way through a round somewhere in what is left of it.
        foreach (var world in worlds.Distinct(StringComparer.Ordinal))
        {
            if (inOrder.Add(world))
                order.Insert(random.Next(order.Count + 1), world);
        }

        shuffle.Order = order;
        shuffle.Played = played;

        if (order.Count == 0)
            return;

        if (shuffle.Round == 0)
            shuffle.Round = 1;

        if (played.Count >= order.Count)
            NewRound(shuffle, random);
    }

    /// <summary>
    /// The first world in the order not played this round that <paramref name="fits"/>. With
    /// <paramref name="after"/>, the search starts just after that world and goes round to the
    /// start, never offering <paramref name="after"/> itself. Null when none fits.
    /// </summary>
    public static string? Next(WorldListShuffle shuffle, Func<string, bool> fits, string? after = null)
    {
        ArgumentNullException.ThrowIfNull(shuffle);
        ArgumentNullException.ThrowIfNull(fits);

        var order = shuffle.Order;
        if (order.Count == 0)
            return null;

        var played = new HashSet<string>(shuffle.Played, StringComparer.Ordinal);
        var start = after is null ? 0 : order.IndexOf(after) + 1;

        for (var step = 0; step < order.Count; step++)
        {
            var world = order[(start + step) % order.Count];

            if (string.Equals(world, after, StringComparison.Ordinal) || played.Contains(world))
                continue;

            if (fits(world))
                return world;
        }

        return null;
    }

    /// <summary>Marks a world played this round.</summary>
    public static void Play(WorldListShuffle shuffle, string world)
    {
        ArgumentNullException.ThrowIfNull(shuffle);

        if (!shuffle.Played.Contains(world, StringComparer.Ordinal))
            shuffle.Played = [.. shuffle.Played, world];

        shuffle.LastPlayed = world;
    }

    /// <summary>Puts a world back: not played this round after all.</summary>
    public static void PutBack(WorldListShuffle shuffle, string world)
    {
        ArgumentNullException.ThrowIfNull(shuffle);

        shuffle.Played = [.. shuffle.Played.Where(w => !string.Equals(w, world, StringComparison.Ordinal))];

        if (string.Equals(shuffle.LastPlayed, world, StringComparison.Ordinal))
            shuffle.LastPlayed = shuffle.Played.Count > 0 ? shuffle.Played[^1] : null;
    }

    /// <summary>Whether a world's player range takes <paramref name="people"/>. An unknown count fits every world.</summary>
    public static bool Fits(int? minPlayers, int? maxPlayers, int? people) =>
        people is not { } count
        || ((minPlayers is null || count >= minPlayers) && (maxPlayers is null || count <= maxPlayers));

    private static void NewRound(WorldListShuffle shuffle, Random random)
    {
        var order = shuffle.Order.ToList();

        // Fisher-Yates.
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        // The round just ended must not end and start on the same world.
        if (order.Count > 1 && string.Equals(order[0], shuffle.LastPlayed, StringComparison.Ordinal))
        {
            var swap = 1 + random.Next(order.Count - 1);
            (order[0], order[swap]) = (order[swap], order[0]);
        }

        shuffle.Order = order;
        shuffle.Played = [];
        shuffle.Round++;
    }
}
