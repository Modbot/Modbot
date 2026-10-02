using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.ModerationLog;

/// <summary>
/// The cards for what happens on the Discord server, and for the things Modbot itself keeps: a
/// list, a role, a giveaway.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A Discord account is never a bare id.</strong> These cards used to go through the one
/// shape every card had, which looked the subject up among VRChat profiles, found nothing, and put
/// the Discord id on the author line -- "465050188063440901 · Joined a voice channel", with nothing
/// to say who or where. The name now comes from Modbot's member list or from the fact itself, and
/// when neither has it the card names the person with Discord's own mention, which Discord draws as
/// their name in the server. A mention in a card pings nobody: the bot turns mentions off on every
/// message it sends.
/// </para>
/// <para>
/// Channels and roles are named the same way, with Discord's own mentions, so a card says which
/// voice channel and which role even when Modbot never read their names.
/// </para>
/// </remarks>
public static partial class EventCard
{
    /// <summary>The kinds of event the group page writes about the group itself, rather than about a person.</summary>
    private static readonly HashSet<string> GroupPageThings = new(StringComparer.Ordinal)
    {
        FactType.GroupPostPosted,
        FactType.GroupPostChanged,
        FactType.GroupPostRemoved,
        FactType.GroupRoleMade,
        FactType.GroupRoleEdited,
        FactType.GroupRoleRemoved,
        FactType.GroupGalleryImageRemoved,
    };

    /// <summary>
    /// Whether the event is about a thing rather than a person: one of Modbot's own (a list, a role,
    /// a webhook, the calendar feed), or the group, as the group page's saves write it.
    /// </summary>
    /// <remarks>
    /// A Modbot account is a person too, and Modbot knows its name; only a subject nobody's name was
    /// found for is taken as a thing, so a deleted account's card names what its payload names.
    /// </remarks>
    private static bool AboutAThing(ModerationEventView e)
        => (e.SubjectPlatform == FactPlatform.Modbot && string.IsNullOrWhiteSpace(e.SubjectName))
           || GroupPageThings.Contains(e.Type);

    /// <summary>
    /// A thing Modbot keeps, or the group: what happened on the author line, the thing's own name
    /// as the title. With no name in the payload, the group heads the card as it heads every other
    /// card about the group.
    /// </summary>
    private static DiscordEmbedContent Thing(ModerationEventView e, CardStyle style)
    {
        var own = e.What.Own;
        var name = e.What.Title ?? own.Name ?? own.NameInChange("name") ?? own.NameInChange("title");

        // The group page writes a role's settings as old and new pairs, as VRChat's audit log does.
        var extra = e.Type == FactType.GroupRoleEdited ? Changes(e) : [];

        return string.IsNullOrWhiteSpace(name)
            ? AboutTheGroup(e, style, ModerationEventEmbed.LabelFor(e.Type), null, extra)
            : Named(e, style, name, url: null, description: null, extra);
    }

    /// <summary>
    /// A card titled by the thing it is about, with what happened to it on the author line where the
    /// group's name would otherwise be. The footer still names the group.
    /// </summary>
    private static DiscordEmbedContent Named(
        ModerationEventView e,
        CardStyle style,
        string name,
        string? url,
        string? description,
        IReadOnlyList<DiscordEmbedField> extra,
        IReadOnlyList<DiscordEmbedField>? lead = null)
        => new(
            CardText.Plain(name.Trim(), 256),
            description,
            ModerationEventEmbed.ColorFor(e.Type),
            Fields(e, style, extra, lead),
            e.OccurredAt,
            url,
            style.GroupFooter,
            FooterIconUrl: style.FooterIconUrl,
            AuthorName: CardText.Plain(ModerationEventEmbed.LabelFor(e.Type), 256));

    // ── Discord ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A Discord event, by kind: a person, a channel, a role or the server.</summary>
    private static DiscordEmbedContent DiscordCard(ModerationEventView e, CardStyle style)
    {
        var own = e.What.Own;
        var label = ModerationEventEmbed.LabelFor(e.Type);

        switch (e.Type)
        {
            case FactType.DiscordVoiceJoined:
            case FactType.DiscordVoiceLeft:
                return DiscordPersonCard(e, style, label, Field("Channel", Channel(own.ChannelId, own.ChannelName)));

            case FactType.DiscordVoiceMoved:
            {
                // The recorder writes where they went as the channel, and where they came from as "from".
                var from = Channel(own.FromChannelId, own.FromChannelName);
                var to = Channel(own.ChannelId, own.ChannelName);

                var moved = (from, to) switch
                {
                    ({ } a, { } b) => $"{a} → {b}",
                    (null, { } b) => b,
                    _ => null,
                };

                return DiscordPersonCard(e, style, label, Field("Channel", moved));
            }

            case FactType.DiscordMemberTimedOut:
                return DiscordPersonCard(e, style, label, Field("Until", Time(own.Until)));

            case FactType.DiscordMemberNicknameChanged:
                return DiscordPersonCard(
                    e, style, label,
                    Field("Nickname", $"{Quote(own.Old, ValueLength)} → {Quote(own.New, ValueLength)}"));

            case FactType.DiscordRoleGranted:
            case FactType.DiscordRoleRevoked:
            {
                var granted = e.Type == FactType.DiscordRoleGranted;

                if (!string.IsNullOrWhiteSpace(e.What.RoleName))
                {
                    var role = e.What.RoleName.Trim();
                    return DiscordPersonCard(
                        e, style,
                        granted ? $"Given the {role} role on Discord" : $"Lost the {role} role on Discord",
                        []);
                }

                return DiscordPersonCard(
                    e, style, label,
                    Field("Role", string.IsNullOrWhiteSpace(e.What.RoleId) ? null : CardLink.RoleMention(e.What.RoleId)));
            }

            case FactType.DiscordMessagesRemoved:
                return DiscordPersonCard(
                    e, style, label,
                    [.. Field("Channel", Channel(own.ChannelId, null)), .. Field("Messages", Word(own.Count))]);

            // The audit log names the channel as the subject of a bulk removal, not a person.
            case FactType.DiscordMessagesBulkRemoved:
                return AboutTheGroup(
                    e, style, label, null,
                    [.. Field("Channel", CardLink.ChannelMention(e.SubjectId)), .. Field("Messages", Word(own.Count))]);

            case FactType.DiscordChannelCreated:
            case FactType.DiscordChannelChanged:
            case FactType.DiscordChannelDeleted:
                return DiscordThing(
                    e, style,
                    string.IsNullOrWhiteSpace(own.Name) ? null : "#" + own.Name.Trim().TrimStart('#'),
                    e.Type == FactType.DiscordChannelDeleted ? null : CardLink.ChannelMention(e.SubjectId),
                    "Channel");

            case FactType.DiscordRoleCreated:
            case FactType.DiscordRoleChanged:
            case FactType.DiscordRoleDeleted:
                return DiscordThing(
                    e, style,
                    own.Name,
                    e.Type == FactType.DiscordRoleDeleted ? null : CardLink.RoleMention(e.SubjectId),
                    "Role");

            // The server itself is the subject of the first read of its members.
            case FactType.DiscordMembersSnapshot:
                return AboutTheGroup(e, style, label, null, Field("Members", Word(own.Count)));

            default:
                return DiscordPersonCard(e, style, label, []);
        }
    }

    /// <summary>
    /// A channel or a role made, changed or deleted on Discord: titled by its name, with what
    /// changed. A name the audit log did not give is Discord's own mention instead, which still
    /// says which one; a deleted one has nothing left to mention, so the card says only what
    /// happened.
    /// </summary>
    private static DiscordEmbedContent DiscordThing(
        ModerationEventView e, CardStyle style, string? name, string? mention, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return Named(e, style, name, url: null, description: null, Changes(e));

        return AboutTheGroup(
            e, style, ModerationEventEmbed.LabelFor(e.Type), null,
            [.. Field(fieldName, mention), .. Changes(e)]);
    }

    /// <summary>
    /// A card about a Discord account: their name on the author line, linked to their profile in
    /// Modbot; or, when no name is known, Discord's mention of them as the first field, so the card
    /// still says who.
    /// </summary>
    private static DiscordEmbedContent DiscordPersonCard(
        ModerationEventView e, CardStyle style, string title, IReadOnlyList<DiscordEmbedField> extra)
    {
        // An invite written for a Discord account it never found has no id to name or link.
        var known = !string.IsNullOrWhiteSpace(e.SubjectId);
        var url = known ? CardLink.UrlFor(CardSubject.DiscordPerson, e.SubjectId, style.PublicAddress) : null;
        var named = !string.IsNullOrWhiteSpace(e.SubjectName);

        IReadOnlyList<DiscordEmbedField>? lead = named || !known
            ? null
            : [new DiscordEmbedField("Who", CardLink.DiscordMention(e.SubjectId), Inline: true)];

        return new DiscordEmbedContent(
            CardText.Plain(title, 256),
            null,
            ModerationEventEmbed.ColorFor(e.Type),
            Fields(e, style, extra, lead),
            e.OccurredAt,
            url,
            style.GroupFooter,
            FooterIconUrl: style.FooterIconUrl,
            AuthorName: named ? CardText.Plain(e.SubjectName!, 256) : null,
            AuthorUrl: named ? url : null);
    }

    /// <summary>
    /// A Discord account in a field: their name, linked to them in Modbot, or Discord's own mention
    /// of them when no name is known.
    /// </summary>
    private static string DiscordPerson(string? name, string id, CardStyle style)
        => string.IsNullOrWhiteSpace(name)
            ? CardLink.DiscordMention(id)
            : CardLink.DiscordPerson(name, id, style.PublicAddress);

    /// <summary>
    /// A channel: Discord's own mention when the fact kept its id, which Discord draws as the
    /// channel's name now; else the name the fact kept; else nothing.
    /// </summary>
    private static string? Channel(string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(id))
            return CardLink.ChannelMention(id);

        return string.IsNullOrWhiteSpace(name)
            ? null
            : "#" + CardText.Fit(CardText.EscapeText(name.Trim().TrimStart('#')), ValueLength);
    }

    /// <summary>A time as Discord's timestamp markup, or nothing when the payload's time does not read.</summary>
    private static string? Time(string? value)
        => At(value) is { } at ? DiscordTime.Absolute(at) : null;

    private static DateTimeOffset? At(string? value)
        => DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var at)
            ? at
            : null;

    /// <summary>One inline field, or none when there is nothing to put in it.</summary>
    private static DiscordEmbedField[] Field(string name, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : [new DiscordEmbedField(name, CardText.Fit(value, FieldLength), Inline: true)];
}
