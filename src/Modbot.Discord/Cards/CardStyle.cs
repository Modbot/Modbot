namespace Modbot.Discord.Cards;

/// <summary>
/// The colours every Discord card draws from.
/// </summary>
/// <remarks>
/// One palette, because the cards each used to carry their own constants and had ended up with
/// three reds, two greens and two greys -- a banned card and a cancelled card were different
/// shades of the same idea for no reason anybody could have named. The meanings, not the pictures,
/// are what a moderator reads off a channel at a glance, so each colour here is named for what it
/// means and used nowhere it does not mean that. The violet is Modbot's own (brand design
/// 2026-09-16); the rest are Discord's, so they sit properly in both its themes.
/// </remarks>
public static class CardColour
{
    /// <summary>Modbot's own violet: news that is neither good nor bad.</summary>
    public const uint Violet = 0x5B4BD6;

    /// <summary>Something was taken away: a ban, a cancelled event.</summary>
    public const uint Red = 0xED4245;

    /// <summary>Something is open, or was given back: an instance with people in it, an unban.</summary>
    public const uint Green = 0x3BA55D;

    /// <summary>Somebody was removed but not banned: a kick.</summary>
    public const uint Orange = 0xE67E22;

    /// <summary>Somebody was told, not removed: a warning.</summary>
    public const uint Yellow = 0xF1C40F;

    /// <summary>Worth a look, nothing has gone wrong: unusual activity.</summary>
    public const uint Amber = 0xF0A020;

    /// <summary>Nothing is happening: an open instance with nobody in it, a request turned down.</summary>
    public const uint Grey = 0x747F8D;

    /// <summary>It is over: a closed instance, a finished event.</summary>
    public const uint Dark = 0x4F545C;
}

/// <summary>
/// What every card posted in one pass shares: where this Modbot is, whose group it moderates, and
/// the mark that goes beside a footer.
/// </summary>
/// <remarks>
/// <para>
/// One value passed down rather than three parameters threaded through every builder, so a card
/// added later -- a giveaway, say -- picks up the links, the group's name and the mark by taking
/// this and nothing else.
/// </para>
/// <para>
/// Every field may be null, and each is null in an ordinary deployment: Modbot works with no
/// public address, before its group has been read, and with no mark to show. A card without them
/// is the same card with fewer things on it, never a card that failed to post.
/// </para>
/// </remarks>
/// <param name="PublicAddress">
/// The address a human typed into settings, which every link out of Discord is built from and
/// nothing else (accounts and access design §4.2). Null means the cards carry no links.
/// </param>
/// <param name="GroupName">The group this Modbot moderates, for the footer of a card about it.</param>
/// <param name="FooterIconUrl">
/// The small mark beside the footer: the group's icon when it could be sent, else Modbot's own
/// mark from the public address, else nothing.
/// </param>
public sealed record CardStyle(
    string? PublicAddress = null,
    string? GroupName = null,
    string? FooterIconUrl = null)
{
    /// <summary>A deployment that has not been set up: no links, no group, no mark.</summary>
    public static CardStyle None { get; } = new();

    /// <summary>The footer of a card about the group's own moderation.</summary>
    public string GroupFooter => string.IsNullOrWhiteSpace(GroupName)
        ? "Modbot"
        : CardText.Plain(GroupName, 100);
}
