using System.Globalization;
using System.Text;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// One moderation event as a Discord card: what happened, to whom, by whom, when, and a link to
/// the person in Modbot.
/// </summary>
/// <remarks>
/// <para>
/// Pure: a fact in, an embed out, no I/O. That is what makes it testable without a gateway and
/// what keeps the poster's loop small.
/// </para>
/// <para>
/// Names are user-controlled text and are escaped so a display name of <c>**everyone**</c>
/// renders as typed rather than as bold; mentions are additionally disabled at send time. Times
/// use Discord's own timestamp markup, so every reader sees them in their own time zone.
/// </para>
/// </remarks>
public static class ModerationEventEmbed
{
    private const uint Red = 0xC0392B;
    private const uint Green = 0x27AE60;
    private const uint Orange = 0xE67E22;
    private const uint Yellow = 0xF1C40F;
    private const uint Grey = 0x7F8C8D;
    private const uint Blue = 0x2980B9;

    public static DiscordEmbedContent For(ModerationEventView e, string? publicAddress)
    {
        ArgumentNullException.ThrowIfNull(e);

        var fields = new List<DiscordEmbedField>
        {
            new("Who", Person(e.SubjectName, e.SubjectId), Inline: true),
        };

        if (e.ActorId is not null)
            fields.Add(new DiscordEmbedField("By", Person(e.ActorName, e.ActorId), Inline: true));

        fields.Add(new DiscordEmbedField("When", $"{DiscordTime.Absolute(e.OccurredAt)} ({DiscordTime.Relative(e.OccurredAt)})"));

        var description = string.IsNullOrWhiteSpace(e.Description)
            ? null
            : "> " + Fit(Escape(e.Description.Trim()), 300);

        return new DiscordEmbedContent(
            Fit(LabelFor(e.Type), 256),
            description,
            ColorFor(e.Type),
            fields,
            e.OccurredAt,
            PersonLink.For(publicAddress, e.SubjectId),
            "Modbot",
            FooterIconUrl: BrandIcon.For(publicAddress));
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
        FactType.MemberBanned => Red,
        FactType.MemberUnbanned => Green,
        FactType.MemberKicked or FactType.GroupInstanceKick => Orange,
        FactType.GroupInstanceWarn => Yellow,
        FactType.JoinRequestRejected or FactType.JoinRequestBlocked => Grey,
        _ => Blue,
    };

    /// <summary>A person as "**Name** (`id`)", or just the id when the name is not known.</summary>
    public static string Person(string? name, string id)
    {
        var safeId = "`" + id.Replace("`", string.Empty, StringComparison.Ordinal) + "`";

        return string.IsNullOrWhiteSpace(name)
            ? safeId
            : $"**{Fit(Escape(name.Trim()), 100)}** ({safeId})";
    }

    /// <summary>Backslash-escapes Discord markdown so user text renders as typed.</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            if (c is '*' or '_' or '~' or '`' or '|' or '>' or '#' or '@' or '\\')
                sb.Append('\\');

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Cuts to Discord's field limits with an ellipsis, never mid-escape-sequence.</summary>
    public static string Fit(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var cut = max - 1;
        if (cut > 0 && text[cut - 1] == '\\')
            cut--;

        return string.Concat(text.AsSpan(0, cut), "…");
    }
}

/// <summary>Discord's timestamp markup: the reader's own time zone, formatted by their client.</summary>
public static class DiscordTime
{
    public static string Absolute(DateTimeOffset at) => $"<t:{Unix(at)}:f>";

    public static string Relative(DateTimeOffset at) => $"<t:{Unix(at)}:R>";

    public static string Day(DateTimeOffset at) => $"<t:{Unix(at)}:d>";

    private static string Unix(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
