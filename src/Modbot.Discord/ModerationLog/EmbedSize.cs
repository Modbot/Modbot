using Modbot.Discord.Gateway;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// How much text a message's cards add up to, and how to keep it under Discord's limit.
/// </summary>
/// <remarks>
/// Discord refuses a message whose cards hold more than six thousand characters between them,
/// however few cards there are. Each card is cut to its own limits already, but ten cards that
/// each use most of theirs still add up to far more, and a refused message is retried on every
/// pass for as long as the cards are waiting: the cards behind it never go out.
/// </remarks>
public static class EmbedSize
{
    /// <summary>Discord's limit for the text of all the cards in one message together.</summary>
    public const int MessageLimit = 6000;

    /// <summary>The characters Discord counts in one card: its title, words, field names and values, footer and author name.</summary>
    public static int Of(DiscordEmbedContent card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var total = card.Title.Length
            + (card.Description?.Length ?? 0)
            + (card.Footer?.Length ?? 0)
            + (card.AuthorName?.Length ?? 0);

        foreach (var field in card.Fields)
            total += field.Name.Length + field.Value.Length;

        return total;
    }

    /// <summary>
    /// How many of the cards, from the first, fit in one message: never fewer than one, because a
    /// card that is too big alone is cut down by <see cref="Shorten"/> rather than left out.
    /// </summary>
    public static int HowManyFit(IReadOnlyList<DiscordEmbedContent> cards, int limit = MessageLimit)
    {
        ArgumentNullException.ThrowIfNull(cards);

        var total = 0;

        for (var i = 0; i < cards.Count; i++)
        {
            total += Of(cards[i]);

            if (total > limit)
                return Math.Max(1, i);
        }

        return cards.Count;
    }

    /// <summary>
    /// The card, cut down until it is within the limit on its own: fields from the last one back,
    /// then the words under the title. The title, author name and footer have limits of their own
    /// that add up to well under the message's, so they stay.
    /// </summary>
    public static DiscordEmbedContent Shorten(DiscordEmbedContent card, int limit = MessageLimit)
    {
        ArgumentNullException.ThrowIfNull(card);

        var size = Of(card);

        if (size <= limit)
            return card;

        var fields = card.Fields.ToList();

        while (size > limit && fields.Count > 0)
        {
            size -= fields[^1].Name.Length + fields[^1].Value.Length;
            fields.RemoveAt(fields.Count - 1);
        }

        var description = card.Description;

        if (size > limit && description is not null)
        {
            var keep = Math.Max(0, description.Length - (size - limit) - 1);
            description = keep == 0 ? null : string.Concat(description.AsSpan(0, keep), "…");
        }

        return card with { Fields = fields, Description = description };
    }
}
