using Modbot.Core.Users;

namespace Modbot.Companion.Overlay;

/// <summary>
/// What a paired server has said about one person, beyond their name: their VRChat trust rank and
/// whether they carry Modbot's 18+ mark.
/// </summary>
/// <remarks>
/// Both come off the same stored profile, so they arrive together. A join pop-up waits for this,
/// and a card that went up without it is filled in once it comes.
/// </remarks>
/// <param name="Rank">Null when the server has not read their tags.</param>
/// <param name="EighteenPlus">Null when the server has not read their profile, or is too old to send it.</param>
public sealed record PersonInfo(TrustRank? Rank, bool? EighteenPlus)
{
    /// <summary>The info, or null when the server has said neither.</summary>
    public static PersonInfo? Of(TrustRank? rank, bool? eighteenPlus)
        => rank is null && eighteenPlus is null ? null : new PersonInfo(rank, eighteenPlus);
}
