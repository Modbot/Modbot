namespace Modbot.Discord.Cards;

/// <summary>What a card's link opens, matching the popup kinds the web app's <c>?subject=</c> reads.</summary>
public enum CardSubject
{
    Person = 0,
    World = 1,
    Instance = 2,
    DiscordPerson = 3,
}

/// <summary>
/// A person, a world or an instance as a card shows them: the name, linked to the thing in
/// Modbot.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A name, not an id.</strong> Cards used to render a person as
/// <c>**Name** (`usr_1234…`)</c>. The id was in front of a moderator on every line and it was
/// never the thing they wanted: it does not say who somebody is, it cannot be searched by eye,
/// and it took a third of the width of a field. It is still reachable -- it is in the link, and
/// the profile shows it with a copy control -- so the card itself drops it and shows the name
/// alone. The only card that still prints an id is a card for somebody whose name Modbot does not
/// know, where the id is all there is to print.
/// </para>
/// <para>
/// <strong>No public address means no link.</strong> The same rule the reset-link sender follows
/// (accounts and access design §4.2): a link Modbot sends out is built from the address a human
/// typed and confirmed, never from a request's host or a forwarded header. Without one the name
/// is shown on its own rather than pointing at an address that cannot work.
/// </para>
/// <para>
/// <strong>The address carries a popup, not a page.</strong> The web app reads <c>?subject=</c>
/// as a stack of things to open over whatever page is underneath (foundation design §10.2), and
/// since worlds and instances joined people in that stack the value carries a kind:
/// <c>?subject=usr_…</c>, <c>?subject=world:wrld_…</c>, <c>?subject=instance:…</c>,
/// <c>?subject=discord-person:…</c>. A person is written bare, which is what the format has always
/// said and what keeps links posted before this change opening the same person.
/// </para>
/// <para>
/// The page under the popup is where a moderator would go next to see more of the same kind of
/// thing. People keep the audit log they have always had -- every card Modbot posts about a person
/// is a moderation event, and the log is the rest of them -- and the other kinds land on their own
/// list.
/// </para>
/// </remarks>
public static class CardLink
{
    /// <summary>The page each kind of popup opens over, matching the web app's own paths.</summary>
    public static string PageFor(CardSubject kind) => kind switch
    {
        CardSubject.World => "/analytics/worlds",
        CardSubject.Instance => "/live",
        CardSubject.DiscordPerson => "/discord/members",
        _ => "/audit",
    };

    /// <summary>
    /// The address that opens one thing's popup in Modbot, or null when no public address is set.
    /// </summary>
    public static string? UrlFor(CardSubject kind, string id, string? publicAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (string.IsNullOrWhiteSpace(publicAddress))
            return null;

        // A person is written bare; every other kind carries its prefix. Modbot's own namespacing
        // on a value Modbot wrote, never an inference about the shape of an id (foundation §3.1.1).
        var value = kind == CardSubject.Person ? id : $"{Prefix(kind)}:{id}";

        return $"{publicAddress.TrimEnd('/')}{PageFor(kind)}?subject={Uri.EscapeDataString(value)}";
    }

    /// <summary>
    /// One thing as it reads on a card: <c>[Display Name](link)</c>, the plain escaped name when
    /// there is no public address, and the id in code style when the name is not known.
    /// </summary>
    public static string For(CardSubject kind, string? name, string id, string? publicAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var url = UrlFor(kind, id, publicAddress);

        // Nothing but the id to show. Code style, so it reads as an identifier rather than as a
        // person who happens to be called usr_1234, and inert whatever characters it holds.
        var label = string.IsNullOrWhiteSpace(name)
            ? "`" + id.Replace("`", string.Empty, StringComparison.Ordinal) + "`"
            : CardText.Fit(CardText.EscapeName(name.Trim()), CardText.MaxNameLength);

        // An escaped name can still be empty -- a name of nothing but control characters -- and an
        // empty link label renders as the address, so fall back to the id.
        if (label.Length == 0)
            label = "`" + id.Replace("`", string.Empty, StringComparison.Ordinal) + "`";

        return url is null ? label : $"[{label}]({url})";
    }

    /// <summary>A VRChat person: the name, linked to their profile popup.</summary>
    public static string Person(string? name, string id, string? publicAddress)
        => For(CardSubject.Person, name, id, publicAddress);

    /// <summary>A world: the name, linked to its popup.</summary>
    public static string World(string? name, string id, string? publicAddress)
        => For(CardSubject.World, name, id, publicAddress);

    /// <summary>
    /// A world on a card members read: the name, linked to the world's own page on vrchat.com
    /// rather than to Modbot, which members cannot sign in to.
    /// </summary>
    /// <remarks>
    /// Added 2026-10-01 for the calendar's channel post (calendar design §3.3): its World link
    /// opened Modbot's sign-in for every member who clicked it. The id goes into the address as it
    /// is, escaped, never checked for shape (foundation §3.1.1).
    /// </remarks>
    public static string WorldOnVRChat(string? name, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // The label as every other card writes it, then pointed at VRChat instead.
        var label = For(CardSubject.World, name, id, publicAddress: null);
        return $"[{label}](https://vrchat.com/home/world/{Uri.EscapeDataString(id)})";
    }

    /// <summary>An instance: its world's name, linked to the instance's popup.</summary>
    public static string Instance(string? name, string id, string? publicAddress)
        => For(CardSubject.Instance, name, id, publicAddress);

    /// <summary>
    /// A Discord account: the name, linked to the Discord profile Modbot holds for them.
    /// </summary>
    /// <remarks>
    /// Their Modbot popup rather than a Discord mention, because the two are different people
    /// until they link and the popup is the one that carries what Modbot knows. With no public
    /// address and no name, the id is shown as a mention instead -- Discord renders that as the
    /// person's own name, and mentions are off on every message the bot sends, so it pings nobody.
    /// </remarks>
    public static string DiscordPerson(string? name, string id, string? publicAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (string.IsNullOrWhiteSpace(name) && UrlFor(CardSubject.DiscordPerson, id, publicAddress) is null)
            return $"<@{id.Replace(">", string.Empty, StringComparison.Ordinal)}>";

        return For(CardSubject.DiscordPerson, name, id, publicAddress);
    }

    /// <summary>
    /// The address that opens one calendar event on Modbot's calendar page, or null when no public
    /// address is set. The page's own <c>?event=</c>, which the calendar feed links to as well.
    /// </summary>
    public static string? CalendarEvent(string id, string? publicAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return string.IsNullOrWhiteSpace(publicAddress)
            ? null
            : $"{publicAddress.TrimEnd('/')}/calendar?event={Uri.EscapeDataString(id)}";
    }

    /// <summary>
    /// A Discord account as Discord's own mention, <c>&lt;@id&gt;</c>: Discord draws it as the
    /// person's name in the server, whether or not Modbot knows that name. A mention in a card
    /// pings nobody: every message the bot sends turns mentions off.
    /// </summary>
    public static string DiscordMention(string id) => $"<@{Inert(id)}>";

    /// <summary>A Discord channel as Discord's own mention, which Discord draws as its name.</summary>
    public static string ChannelMention(string id) => $"<#{Inert(id)}>";

    /// <summary>A Discord role as Discord's own mention, which Discord draws as its name.</summary>
    public static string RoleMention(string id) => $"<@&{Inert(id)}>";

    /// <summary>
    /// An id with the characters that would end or break the mention taken out. Nothing is checked
    /// about its shape (foundation §3.1.1).
    /// </summary>
    private static string Inert(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return new string(id.Where(c => c is not ('<' or '>' or '`' or '@' or '#' or '&') && !char.IsWhiteSpace(c)).ToArray());
    }

    private static string Prefix(CardSubject kind) => kind switch
    {
        CardSubject.World => "world",
        CardSubject.Instance => "instance",
        CardSubject.DiscordPerson => "discord-person",
        _ => "person",
    };
}
