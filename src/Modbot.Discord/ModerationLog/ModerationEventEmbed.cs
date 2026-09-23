using System.Globalization;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// The card a kind of event without a card of its own still gets: who it happened to, what
/// happened, by whom, when, and a link to the person in Modbot.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This was the only shape there was.</strong> Every kind of event was given it, which is
/// why a role change and a ban differed by hue and neither said which role or why (Discord event
/// cards design §1). <see cref="EventCard"/> is the converter now: it picks a card per kind of
/// event and falls back to this one for every kind without a builder. That fallback is the whole
/// safety of the change, so this stays exactly the shape it was.
/// </para>
/// <para>
/// Pure: a fact in, an embed out, no I/O. That is what makes it testable without a gateway and
/// what keeps the poster's loop small. The person's picture arrives already fetched, as an
/// address the poster resolved, for the same reason.
/// </para>
/// <para>
/// <strong>The person heads the card.</strong> Their name is the author line with their icon
/// beside it, and the title says what happened to them -- so a channel of these reads as a list of
/// faces and verbs rather than a list of verbs you have to open to understand. This replaced a
/// <c>Who</c> field reading <c>**Name** (`usr_1234…`)</c>: see <see cref="CardLink"/> for why the
/// id went.
/// </para>
/// <para>
/// Names are user-controlled text. The author line and the title are slots Discord prints
/// literally, so control characters are stripped and nothing is escaped; the fields are markdown,
/// so what goes in them is escaped and mentions are additionally disabled at send time. Times use
/// Discord's own timestamp markup, so every reader sees them in their own time zone.
/// </para>
/// </remarks>
public static class ModerationEventEmbed
{
    public static DiscordEmbedContent For(ModerationEventView e, string? publicAddress)
        => For(e, new CardStyle(publicAddress), CardPicture.None);

    /// <param name="picture">
    /// <see cref="CardPicture.AuthorIcon"/> is the person's own picture, beside their name. There
    /// is deliberately no banner here: the log posts up to ten cards in one message, and ten
    /// banners is a wall rather than a record. The banner belongs on the one card that is about a
    /// person rather than about something that happened to them -- the <c>/lookup</c> reply.
    /// </param>
    public static DiscordEmbedContent For(ModerationEventView e, CardStyle style, CardPicture picture)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(style);

        return EventCard.Plain(e, style, picture);
    }

    /// <summary>
    /// Plain words for each event type: the audit log's own labels, except where a card read on its
    /// own needs to say more than a row in a list does.
    /// </summary>
    public static string LabelFor(string type) => type switch
    {
        FactType.MemberKicked => "Kicked from the group",
        _ => FactLabels.For(type),
    };

    public static uint ColorFor(string type) => type switch
    {
        FactType.MemberBanned => CardColour.Red,
        FactType.MemberUnbanned => CardColour.Green,
        FactType.MemberKicked or FactType.GroupInstanceKick => CardColour.Orange,
        FactType.GroupInstanceWarn => CardColour.Yellow,
        FactType.JoinRequestRejected or FactType.JoinRequestBlocked => CardColour.Grey,
        _ => CardColour.Violet,
    };

    /// <summary>A person as a card shows them: the name, linked to them in Modbot.</summary>
    /// <remarks>Kept as the name every caller already knows; the rule lives in <see cref="CardLink"/>.</remarks>
    public static string Person(string? name, string id, string? publicAddress = null)
        => CardLink.Person(name, id, publicAddress);

    /// <summary>Backslash-escapes Discord markdown so user text renders as typed.</summary>
    public static string Escape(string text) => CardText.EscapeText(text);

    /// <summary>Cuts to Discord's field limits with an ellipsis, never mid-escape-sequence.</summary>
    public static string Fit(string text, int max) => CardText.Fit(text, max);
}

/// <summary>Discord's timestamp markup: the reader's own time zone, formatted by their client.</summary>
public static class DiscordTime
{
    public static string Absolute(DateTimeOffset at) => $"<t:{Unix(at)}:f>";

    public static string Relative(DateTimeOffset at) => $"<t:{Unix(at)}:R>";

    public static string Day(DateTimeOffset at) => $"<t:{Unix(at)}:d>";

    private static string Unix(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
