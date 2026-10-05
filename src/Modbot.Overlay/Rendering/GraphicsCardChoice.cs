namespace Modbot.Overlay.Rendering;

/// <summary>One graphics card as Windows lists it, with only what choosing between them needs.</summary>
/// <param name="Index">Its place in Windows' list, which is how it is asked for again.</param>
/// <param name="Name">The card's own description, as the driver gives it.</param>
/// <param name="DedicatedMemory">Memory on the card itself, in bytes. Zero for a card that borrows system memory.</param>
/// <param name="Id">Windows' id for the card (its LUID), which is what SteamVR names its card by.</param>
/// <param name="Software">Windows says it is drawn by the processor, not a graphics card.</param>
/// <param name="Remote">Windows says it is a remote session's card.</param>
public sealed record GraphicsCard(
    uint Index,
    string Name,
    ulong DedicatedMemory,
    long Id,
    bool Software = false,
    bool Remote = false);

/// <summary>
/// Which graphics card the overlay's device is made on, in the order to try them.
/// </summary>
/// <remarks>
/// <para><strong>Why not let Windows choose.</strong> Asking for no card in particular gives
/// whatever Windows lists first, which on a PC with a headset card, a processor's built-in one and a
/// screen-sharing program's pretend card is not reliably the card SteamVR draws on. The built-in
/// one has a small slice of memory set aside, and a device made there was refused with
/// "out of memory" at some starts and not others.</para>
/// <para><strong>The order.</strong> When SteamVR is attached and names a card Windows lists, that
/// card and no other: tried, and tried once more after a short wait. Otherwise (no SteamVR, as in
/// debug mode's preview with no headset, or a card SteamVR names that Windows does not list) every
/// real card, the one with the most memory of its own first, and last Windows' own choice, so a PC
/// where none of that worked is no worse off than before.</para>
/// <para><strong>What is skipped.</strong> Cards Windows marks as drawn in software or as a
/// remote session's, and cards whose name says they are pretend ones: Microsoft's basic and remote
/// display drivers, Parsec's, and anything calling itself virtual. None of them can carry a headset
/// panel, and Parsec's is the one that sits first in the list on some PCs.</para>
/// <para>Pure, so the order can be checked without a graphics card.</para>
/// </remarks>
public static class GraphicsCardChoice
{
    /// <summary>
    /// Words in a card's name that mark it as not a real graphics card. Matched anywhere in the
    /// name, ignoring case.
    /// </summary>
    private static readonly string[] PretendCardWords =
    [
        "Microsoft",
        "Basic Render",
        "Parsec",
        "Remote",
        "Virtual",
    ];

    /// <summary>
    /// The cards to try, best first. A null entry, last, means "let Windows choose"; it is not
    /// there when SteamVR named the card.
    /// </summary>
    /// <param name="cards">Every card Windows listed. Empty when the list could not be read.</param>
    /// <param name="steamVrCard">The id of the card SteamVR draws on, or null when it is not known.</param>
    public static IReadOnlyList<GraphicsCard?> Order(IReadOnlyList<GraphicsCard> cards, long? steamVrCard)
    {
        ArgumentNullException.ThrowIfNull(cards);

        // SteamVR's word is final: it is the card the headset is plugged into, and a texture on any
        // other card is one SteamVR cannot show. So that card alone, tried twice (the second after
        // a short wait), and nothing after it. A failure then says so plainly instead of being
        // hidden behind a texture on a card that draws nothing in the headset.
        if (steamVrCard is { } wanted && cards.FirstOrDefault(card => card.Id == wanted) is { } headset)
            return [headset, headset];

        var order = new List<GraphicsCard?>();

        foreach (var card in cards
            .Where(IsReal)
            .OrderByDescending(card => card.DedicatedMemory)
            .ThenBy(card => card.Index))
        {
            if (!order.Contains(card))
                order.Add(card);
        }

        order.Add(null);
        return order;
    }

    /// <summary>Whether a card is a real graphics card that can carry a headset panel.</summary>
    public static bool IsReal(GraphicsCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (card.Software || card.Remote)
            return false;

        return !PretendCardWords.Any(word => card.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether to wait a moment before the next try (the same card again, when SteamVR named it).
    /// Only once, and only after the card
    /// said it was out of memory: that answer has come and gone between starts on the same PC, so a
    /// short pause gives it a fair second try, and any other answer would only be the same again.
    /// </summary>
    /// <param name="outOfMemory">The try that just failed was refused as out of memory.</param>
    /// <param name="waitedAlready">A wait has already been spent on this device.</param>
    public static bool WaitBeforeNext(bool outOfMemory, bool waitedAlready) => outOfMemory && !waitedAlready;
}
