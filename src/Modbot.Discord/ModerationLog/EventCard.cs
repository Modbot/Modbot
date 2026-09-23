using System.Text;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// One Modbot event as the Discord card for that kind of event.
/// </summary>
/// <remarks>
/// <para>
/// Pure: a fact in, a card out, no I/O (Discord event cards design §2). The pictures arrive
/// already resolved, so a card can be checked without a gateway and the poster's loop stays small.
/// </para>
/// <para>
/// <strong>There is a card per kind of event, and a fallback for every kind without one.</strong>
/// More than fifty types can be sent, they arrive from VRChat's audit log rather than from Modbot,
/// and a type nobody has written a builder for must still post a card rather than throw or post
/// nothing. The fallback is <see cref="Plain"/>, which is the one shape every card used to have.
/// </para>
/// <para>
/// <strong>The title says what happened including the thing it happened to</strong>, and the fields
/// carry what a moderator would otherwise open Modbot to find. Where the event has a title of its
/// own -- an announcement, a post, a calendar entry -- that title is the card's, because a channel
/// of those should read as the things themselves rather than as a list of the word "Announcement".
/// </para>
/// <para>
/// The safety rules are unchanged (§6). The author line and the title are slots Discord prints
/// literally, so control characters are stripped and nothing is escaped; fields are markdown, so
/// what goes in them is escaped. No id is printed in the body. Everything is cut to Discord's
/// limits, never mid-escape-sequence.
/// </para>
/// </remarks>
public static class EventCard
{
    /// <summary>The most of a description a card shows.</summary>
    /// <remarks>
    /// Ten cards go out in one message and Discord counts six thousand characters across all of
    /// them, so a generous cap here is a message that is refused rather than a card that reads
    /// better.
    /// </remarks>
    private const int DescriptionLength = 300;

    /// <summary>The most of one field a card shows. Discord's own limit is 1024.</summary>
    private const int FieldLength = 900;

    /// <summary>The most of one value inside a field: a name, a word, one side of a change.</summary>
    private const int ValueLength = 80;

    /// <summary>The most changes one card lists before it says how many are left.</summary>
    private const int ChangesListed = 8;

    public static DiscordEmbedContent For(ModerationEventView e, string? publicAddress)
        => For(e, new CardStyle(publicAddress), CardPicture.None);

    /// <param name="picture">
    /// <see cref="CardPicture.AuthorIcon"/> is the person's own picture, beside their name. There
    /// is deliberately no banner on any of these: the log posts up to ten cards in one message,
    /// and ten banners is a wall rather than a record.
    /// </param>
    public static DiscordEmbedContent For(ModerationEventView e, CardStyle style, CardPicture picture)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(style);

        return e.Type switch
        {
            FactType.MemberBanned => Titled(e, style, picture, "Banned from the group"),
            FactType.MemberUnbanned => Titled(e, style, picture, "Unbanned from the group"),
            FactType.MemberKicked => Titled(e, style, picture, "Kicked from the group"),

            FactType.GroupInstanceWarn => InAnInstance(e, style, picture, "Warned in an instance"),
            FactType.GroupInstanceKick => InAnInstance(e, style, picture, "Kicked from an instance"),

            FactType.RoleGranted => Role(e, style, picture, "Given the {0} role"),
            FactType.RoleRevoked => Role(e, style, picture, "Lost the {0} role"),
            FactType.RoleUpdated => RoleChanged(e, style),

            FactType.GroupInstanceAnnouncement => Announcement(e, style),
            FactType.GroupPostCreated => Post(e, style),

            FactType.GroupInstanceCreated => Instance(e, style, "Instance opened"),
            FactType.GroupInstanceClosed => Instance(e, style, "Instance closed"),

            FactType.CalendarEventCreated => CalendarEntry(e, style),

            FactType.UserProfileChanged => ProfileChanged(e, style, picture),

            _ => Plain(e, style, picture),
        };
    }

    /// <summary>
    /// The one shape every card used to have, and the shape every kind without a builder still
    /// has: the person on the author line, the type's label as the title, VRChat's own sentence as
    /// the description, and <c>By</c> and <c>When</c> as the only fields.
    /// </summary>
    public static DiscordEmbedContent Plain(ModerationEventView e, CardStyle style, CardPicture picture)
        => Titled(e, style, picture, ModerationEventEmbed.LabelFor(e.Type));

    // ── The cards ────────────────────────────────────────────────────────────────────────────

    /// <summary>A card about a person that says nothing today's card did not, but says it better.</summary>
    private static DiscordEmbedContent Titled(
        ModerationEventView e, CardStyle style, CardPicture picture, string title)
        => Build(e, style, picture, title, Quoted(e.Description), []);

    /// <summary>
    /// A warn or an instance kick: where it happened, when Modbot knows the world's name.
    /// </summary>
    private static DiscordEmbedContent InAnInstance(
        ModerationEventView e, CardStyle style, CardPicture picture, string title)
    {
        var fields = new List<DiscordEmbedField>();

        if (Where(e, style) is { } where)
            fields.Add(new DiscordEmbedField("Where", where, Inline: true));

        return Build(e, style, picture, title, Quoted(e.Description), fields);
    }

    /// <summary>
    /// A role given or taken away. The role is named in the title, so it is not repeated in a
    /// field: the same thing twice on one card is noise, not detail.
    /// </summary>
    private static DiscordEmbedContent Role(
        ModerationEventView e, CardStyle style, CardPicture picture, string title)
    {
        var role = e.What.RoleName;

        return Titled(
            e, style, picture,
            string.IsNullOrWhiteSpace(role)
                ? ModerationEventEmbed.LabelFor(e.Type)
                : string.Format(System.Globalization.CultureInfo.InvariantCulture, title, role.Trim()));
    }

    /// <summary>
    /// A role's own settings changed. The card is about the role, not about a person, so the group
    /// heads it.
    /// </summary>
    private static DiscordEmbedContent RoleChanged(ModerationEventView e, CardStyle style)
    {
        var role = e.What.RoleName;

        var title = string.IsNullOrWhiteSpace(role)
            ? ModerationEventEmbed.LabelFor(e.Type)
            : $"The {role.Trim()} role changed";

        return AboutTheGroup(e, style, title, Quoted(e.Description), Changes(e));
    }

    /// <summary>An instance announcement: its own title, and its own words as the body.</summary>
    private static DiscordEmbedContent Announcement(ModerationEventView e, CardStyle style)
        => AboutTheGroup(
            e, style,
            e.What.Title ?? ModerationEventEmbed.LabelFor(e.Type),
            FreeText(e.What.Message) ?? Quoted(e.Description),
            []);

    /// <summary>A group post: its own title, its own words, and who may see it.</summary>
    private static DiscordEmbedContent Post(ModerationEventView e, CardStyle style)
    {
        var fields = new List<DiscordEmbedField>();

        if (Word(e.What.Visibility) is { } visibility)
            fields.Add(new DiscordEmbedField("Seen by", visibility, Inline: true));

        return AboutTheGroup(
            e, style,
            e.What.Title ?? ModerationEventEmbed.LabelFor(e.Type),
            FreeText(e.What.Text) ?? Quoted(e.Description),
            fields);
    }

    /// <summary>An instance opened or closed, and who it was open to.</summary>
    private static DiscordEmbedContent Instance(ModerationEventView e, CardStyle style, string title)
    {
        var fields = new List<DiscordEmbedField>();

        if (Openness(e.What.GroupAccessType) is { } openTo)
            fields.Add(new DiscordEmbedField("Open to", openTo, Inline: true));

        return AboutTheGroup(e, style, title, Quoted(e.Description), fields);
    }

    /// <summary>A calendar entry: its own title, what kind it is, and who may come.</summary>
    private static DiscordEmbedContent CalendarEntry(ModerationEventView e, CardStyle style)
    {
        var fields = new List<DiscordEmbedField>();

        if (Word(e.What.Kind) is { } kind)
            fields.Add(new DiscordEmbedField("Kind", kind, Inline: true));

        if (Openness(e.What.AccessType) is { } openTo)
            fields.Add(new DiscordEmbedField("Open to", openTo, Inline: true));

        return AboutTheGroup(
            e, style,
            e.What.Title ?? ModerationEventEmbed.LabelFor(e.Type),
            Quoted(e.Description),
            fields);
    }

    /// <summary>
    /// A profile that changed. Titled by the name when that is what changed, because a name change
    /// is the one a moderator reads a channel for.
    /// </summary>
    private static DiscordEmbedContent ProfileChanged(ModerationEventView e, CardStyle style, CardPicture picture)
    {
        var named = e.What.Changes.Any(c => string.Equals(c.Name, "displayName", StringComparison.Ordinal));

        return Build(
            e, style, picture,
            named ? "Name changed" : ModerationEventEmbed.LabelFor(e.Type),
            Quoted(e.Description),
            Changes(e));
    }

    // ── The two ways a card is put together ──────────────────────────────────────────────────

    /// <summary>
    /// A card about the person on the author line: their name, their picture, their popup.
    /// </summary>
    private static DiscordEmbedContent Build(
        ModerationEventView e,
        CardStyle style,
        CardPicture picture,
        string title,
        string? description,
        IReadOnlyList<DiscordEmbedField> extra)
    {
        var link = CardLink.UrlFor(CardSubject.Person, e.SubjectId, style.PublicAddress);

        return new DiscordEmbedContent(
            CardText.Plain(title, 256),
            description,
            ModerationEventEmbed.ColorFor(e.Type),
            Fields(e, style, extra),
            e.OccurredAt,
            link,
            style.GroupFooter,
            FooterIconUrl: style.FooterIconUrl,
            AuthorName: AuthorName(e),
            AuthorUrl: link,
            AuthorIconUrl: picture.AuthorIcon);
    }

    /// <summary>
    /// A card about something the group did rather than about somebody it was done to: a role, an
    /// instance, a post, a calendar entry.
    /// </summary>
    /// <remarks>
    /// These events name a location, a role or a notification in the subject, never a person, so
    /// the group heads the card the way it heads an instance announcement and a calendar post
    /// (Discord embeds design §4.2, §4.3). Putting a location on the author line and linking it to
    /// a person's popup is what the old one shape did, and it was wrong on every one of them.
    /// </remarks>
    private static DiscordEmbedContent AboutTheGroup(
        ModerationEventView e,
        CardStyle style,
        string title,
        string? description,
        IReadOnlyList<DiscordEmbedField> extra)
    {
        var link = string.IsNullOrWhiteSpace(e.WorldId)
            ? null
            : CardLink.UrlFor(CardSubject.World, e.WorldId, style.PublicAddress);

        return new DiscordEmbedContent(
            CardText.Plain(title, 256),
            description,
            ModerationEventEmbed.ColorFor(e.Type),
            Fields(e, style, extra),
            e.OccurredAt,
            link,
            style.GroupFooter,
            FooterIconUrl: style.FooterIconUrl,
            AuthorName: string.IsNullOrWhiteSpace(style.GroupName) ? null : CardText.Plain(style.GroupName, 256),
            AuthorUrl: link);
    }

    /// <summary>By and When, which every card has, then whatever the kind of event adds.</summary>
    private static List<DiscordEmbedField> Fields(
        ModerationEventView e, CardStyle style, IReadOnlyList<DiscordEmbedField> extra)
    {
        var fields = new List<DiscordEmbedField>(2 + extra.Count);

        if (e.ActorId is not null)
        {
            fields.Add(new DiscordEmbedField(
                "By", CardLink.Person(e.ActorName, e.ActorId, style.PublicAddress), Inline: true));
        }

        fields.Add(new DiscordEmbedField(
            "When",
            $"{DiscordTime.Absolute(e.OccurredAt)} ({DiscordTime.Relative(e.OccurredAt)})",
            Inline: true));

        fields.AddRange(extra);

        return fields;
    }

    /// <summary>
    /// The name on the author line: the person's own, or their id when Modbot has never read a
    /// profile for them. An author line cannot be left out and still leave the card about
    /// somebody, so this is the one place an id is still printed.
    /// </summary>
    private static string AuthorName(ModerationEventView e)
        => string.IsNullOrWhiteSpace(e.SubjectName)
            ? CardText.Plain(e.SubjectId, 256)
            : CardText.Plain(e.SubjectName, 256);

    // ── The pieces a field is made of ────────────────────────────────────────────────────────

    /// <summary>
    /// The instance, named by its world and its number, or nothing when Modbot has never read the
    /// world. A world id in a field is exactly what <see cref="CardLink"/> took out of these cards.
    /// </summary>
    private static string? Where(ModerationEventView e, CardStyle style)
    {
        if (string.IsNullOrWhiteSpace(e.WorldId) || string.IsNullOrWhiteSpace(e.WorldName))
            return null;

        var world = CardLink.World(e.WorldName, e.WorldId, style.PublicAddress);

        return string.IsNullOrWhiteSpace(e.InstanceId)
            ? world
            : world + " #" + CardText.Fit(CardText.EscapeName(e.InstanceId.Trim()), ValueLength);
    }

    /// <summary>What changed, one line each, before and after.</summary>
    private static IReadOnlyList<DiscordEmbedField> Changes(ModerationEventView e)
    {
        var changes = e.What.Changes;
        if (changes.Count == 0)
            return [];

        var lines = new StringBuilder();
        var listed = 0;

        foreach (var change in changes)
        {
            if (listed == ChangesListed)
            {
                lines.Append("\nand ").Append(changes.Count - listed).Append(" more");
                break;
            }

            if (listed > 0)
                lines.Append('\n');

            lines.Append("**").Append(CardText.Fit(CardText.EscapeText(PlainName(change.Name)), ValueLength)).Append("**: ");

            // An id or an address says nothing a moderator wanted, and a VRChat file address is
            // longer than the field it would sit in, so those are named as changed and not printed.
            lines.Append(Hidden(change.Name)
                ? "changed"
                : $"{Side(change.Before)} → {Side(change.After)}");

            listed++;
        }

        return [new DiscordEmbedField("Changed", CardText.Fit(lines.ToString(), FieldLength))];
    }

    private static string Side(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "nothing"
            : CardText.Fit(CardText.EscapeText(value.Trim()), ValueLength);

    /// <summary>Whether a changed field's values are an id or an address rather than words.</summary>
    /// <remarks>
    /// Read off the payload's own name for the field, never off the shape of the value: a VRChat id
    /// has no shape to read (foundation §3.1.1).
    /// </remarks>
    private static bool Hidden(string name)
        => name.EndsWith("Id", StringComparison.Ordinal)
           || name.EndsWith("Ids", StringComparison.Ordinal)
           || name.EndsWith("Url", StringComparison.Ordinal);

    /// <summary>
    /// Plain words for the names a payload gives its fields: VRChat's own where there is a better
    /// one, and the name split into words where there is not.
    /// </summary>
    private static string PlainName(string name) => name switch
    {
        "displayName" => "Name",
        "statusDescription" => "Status",
        "representedGroup" => "Represented group",
        "dateJoined" => "Joined VRChat",
        "ageVerificationStatus" => "18+ status",
        _ => Spaced(name),
    };

    /// <summary>
    /// <c>isSelfAssignable</c> as <c>Is self assignable</c>: a name nobody wrote for a screen, made
    /// readable rather than left as it was stored.
    /// </summary>
    private static string Spaced(string name)
    {
        if (name.Length == 0)
            return name;

        var words = new StringBuilder(name.Length + 8);
        words.Append(char.ToUpperInvariant(name[0]));

        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                words.Append(' ').Append(char.ToLowerInvariant(name[i]));
            else
                words.Append(name[i]);
        }

        return words.ToString();
    }

    /// <summary>
    /// How open an instance or a calendar entry was, in VRChat's own words for it.
    /// </summary>
    private static string? Openness(string? value) => value?.Trim() switch
    {
        null or "" => null,
        "public" => "Anyone",
        "plus" => "Group plus",
        "members" => "Group members",
        var other => Word(other),
    };

    /// <summary>One short word out of a payload, made inert and cut.</summary>
    private static string? Word(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : CardText.Fit(CardText.EscapeText(value.Trim()), ValueLength);

    /// <summary>The event's own words as the card's body.</summary>
    private static string? FreeText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? null
            : CardText.Fit(CardText.EscapeText(text.Trim()), DescriptionLength);

    /// <summary>VRChat's own sentence about the entry, quoted so it reads as theirs.</summary>
    private static string? Quoted(string? description)
        => string.IsNullOrWhiteSpace(description)
            ? null
            : "> " + CardText.Fit(CardText.EscapeText(description.Trim()), DescriptionLength);
}
