using Modbot.Discord.Cards;

namespace Modbot.Discord;

/// <summary>
/// The link to a person in the Modbot web app, built from the public address setting and
/// nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The rule and the address now live in <see cref="CardLink"/>, with worlds, instances and Discord
/// accounts, because the popup a link opens took three more kinds and a helper that knew only
/// about people had become the reason a world on a card was an id nobody could click.
/// </para>
/// <para>
/// Kept as the name callers outside the cards already use.
/// </para>
/// </remarks>
public static class PersonLink
{
    public static string? For(string? publicAddress, string vrchatUserId)
        => CardLink.UrlFor(CardSubject.Person, vrchatUserId, publicAddress);
}
