using Modbot.Discord.Cards;

namespace Modbot.Discord.Tests.Cards;

/// <summary>
/// The rule every card follows for a person, a world or an instance: a name, linked, and the id
/// only when there is no name to show. Nothing here touches a database or a gateway.
/// </summary>
public class CardLinkTests
{
    private const string Address = "https://modbot.example.com";
    private const string Person = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";

    [Fact]
    public void WithAPublicAddress_APersonIsTheirNameLinkedToTheirPopup()
    {
        Assert.Equal(
            $"[jessie]({Address}/audit?subject={Person})",
            CardLink.Person("jessie", Person, Address));
    }

    [Fact]
    public void WithNoPublicAddress_APersonIsJustTheirName()
    {
        Assert.Equal("jessie", CardLink.Person("jessie", Person, null));
        Assert.Equal("jessie", CardLink.Person("jessie", Person, "   "));
    }

    /// <summary>
    /// The id is not on the card. It is in the link, and the profile shows it with a control that
    /// copies it, so a moderator who wants it is one click away and everybody else is not reading
    /// forty opaque characters on every line.
    /// </summary>
    [Fact]
    public void APersonWithANameNeverShowsTheirId()
    {
        Assert.DoesNotContain(Person, CardLink.Person("jessie", Person, Address), StringComparison.Ordinal);
        Assert.DoesNotContain(Person, CardLink.Person("jessie", Person, null), StringComparison.Ordinal);
    }

    /// <summary>
    /// The one exception, and the reason it is one: there is nothing else to print. Code style, so
    /// it reads as an identifier rather than as somebody called usr_1234.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void APersonWithNoName_ShowsTheirIdInCodeStyle(string? name)
    {
        Assert.Equal($"[`{Person}`]({Address}/audit?subject={Person})", CardLink.Person(name, Person, Address));
        Assert.Equal($"`{Person}`", CardLink.Person(name, Person, null));
    }

    /// <summary>
    /// A display name is chosen by anybody. Markdown in one renders as typed, and -- the reason the
    /// escape had to get stricter -- a bracket in one must not end the link early and put the
    /// address on screen.
    /// </summary>
    [Fact]
    public void MarkdownInANameIsEscaped()
    {
        Assert.Equal(
            $@"[\*\*@everyone\*\*]({Address}/audit?subject={Person})",
            CardLink.Person("**@everyone**", Person, Address));
    }

    [Fact]
    public void ABracketInANameCannotEndTheLink()
    {
        var link = CardLink.Person("ada](https://evil.example)", Person, Address);

        Assert.StartsWith(@"[ada\]\(https://evil.example\)](", link, StringComparison.Ordinal);
        Assert.EndsWith($"{Address}/audit?subject={Person})", link, StringComparison.Ordinal);
    }

    /// <summary>A name of nothing but control characters escapes to nothing, and an empty link
    /// label renders as the address, so the id stands in.</summary>
    [Fact]
    public void ANameThatEscapesToNothing_FallsBackToTheId()
        => Assert.Equal($"[`{Person}`]({Address}/audit?subject={Person})", CardLink.Person("\n\t ", Person, Address));

    [Fact]
    public void ALongNameIsCutToFit()
    {
        var link = CardLink.Person(new string('a', 400), Person, null);

        Assert.Equal(CardText.MaxNameLength, link.Length);
        Assert.EndsWith("…", link, StringComparison.Ordinal);
    }

    // ── The other kinds the popup takes ──────────────────────────────────────────────────

    /// <summary>
    /// A person is written bare and everything else carries its kind, which is exactly what the web
    /// app reads (foundation design §10.2) and what keeps links posted years ago still working.
    /// </summary>
    [Fact]
    public void AWorldAnInstanceAndADiscordAccountEachCarryTheirKind()
    {
        Assert.Equal(
            $"[The Black Cat]({Address}/analytics/worlds?subject=world%3Awrld_a)",
            CardLink.World("The Black Cat", "wrld_a", Address));

        Assert.Equal(
            $"[The Black Cat]({Address}/live?subject=instance%3A42)",
            CardLink.Instance("The Black Cat", "42", Address));

        Assert.Equal(
            $"[nova]({Address}/discord/members?subject=discord-person%3A1234)",
            CardLink.DiscordPerson("nova", "1234", Address));
    }

    /// <summary>
    /// A Discord account Modbot has no name for, on a deployment with no address: a mention, which
    /// Discord renders as the person's own name. Mentions are off on every message the bot sends,
    /// so it pings nobody.
    /// </summary>
    [Fact]
    public void ADiscordAccountWithNoNameAndNoAddress_IsAMention()
        => Assert.Equal("<@1234>", CardLink.DiscordPerson(null, "1234", null));

    [Fact]
    public void ADiscordAccountWithNoName_StillLinksWhenThereIsAnAddress()
        => Assert.Equal(
            $"[`1234`]({Address}/discord/members?subject=discord-person%3A1234)",
            CardLink.DiscordPerson(null, "1234", Address));

    // ── The address itself ───────────────────────────────────────────────────────────────

    [Fact]
    public void NoPublicAddressMeansNoAddress()
    {
        Assert.Null(CardLink.UrlFor(CardSubject.Person, Person, null));
        Assert.Null(CardLink.UrlFor(CardSubject.World, "wrld_a", "  "));
    }

    [Fact]
    public void ATrailingSlashOnTheAddressIsNotDoubled()
        => Assert.Equal(
            $"{Address}/audit?subject={Person}",
            CardLink.UrlFor(CardSubject.Person, Person, Address + "/"));

    /// <summary>
    /// VRChat ids are arbitrary text (foundation design §3.1.1), so an id is encoded rather than
    /// checked. An instance's qualifiers carry brackets and tildes and must survive.
    /// </summary>
    [Fact]
    public void AnIdIsEncoded_NeverValidated()
    {
        Assert.Equal(
            $"{Address}/audit?subject=usr%20odd%2Fid%26x",
            CardLink.UrlFor(CardSubject.Person, "usr odd/id&x", Address));

        Assert.Equal(
            $"{Address}/live?subject=instance%3A26093~group%28grp_a%29",
            CardLink.UrlFor(CardSubject.Instance, "26093~group(grp_a)", Address));
    }

    [Fact]
    public void AnEmptyIdIsAMistake_NotABlankLink()
    {
        Assert.Throws<ArgumentException>(() => CardLink.UrlFor(CardSubject.Person, "  ", Address));
        Assert.Throws<ArgumentException>(() => CardLink.Person("jessie", string.Empty, Address));
    }
}
