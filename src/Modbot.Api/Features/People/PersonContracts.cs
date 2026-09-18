namespace Modbot.Api.Features.People;

/// <summary>What tied one of a person's accounts to the one the caller asked about.</summary>
/// <remarks>
/// Carried so nothing on screen can imply a tie the data does not hold. A Discord account found
/// through <see cref="Link"/> was proved by both sides; one found through <see cref="Account"/>
/// was typed into a Modbot account by hand and never proved (accounts and access design §6).
/// Names are never compared — a person is never found by what they are called.
/// </remarks>
public static class FoundBy
{
    /// <summary>The account the link named. Nothing was resolved to reach it.</summary>
    public const string Asked = "asked";

    /// <summary>Through the proved Discord–VRChat account link.</summary>
    public const string Link = "link";

    /// <summary>Because a Modbot account records it.</summary>
    public const string Account = "account";
}

/// <param name="Id">Opaque. Never parsed, never validated (foundation §3.1.1).</param>
/// <param name="Name">The name stored for them now, or null when Modbot has only ever seen the id.</param>
/// <param name="FoundBy">One of <see cref="People.FoundBy"/>.</param>
public sealed record PersonSide(string Id, string? Name, string FoundBy);

/// <param name="Roles">The roles the account holds, by name.</param>
/// <param name="FoundBy">One of <see cref="People.FoundBy"/>.</param>
public sealed record PersonAccount(
    Guid Id,
    string Username,
    string FoundBy,
    IReadOnlyList<string> Roles,
    bool IsDisabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>
/// One human being, as the accounts Modbot can tie together.
/// </summary>
/// <remarks>
/// <para>
/// A null side is one Modbot has no record of. <see cref="CanSeeAccount"/> tells that apart from
/// a side this caller may not read: a screen leaves out what it may not read rather than showing
/// it empty, and saying "no Modbot account" to somebody who is simply not allowed to know would
/// be a lie.
/// </para>
/// </remarks>
/// <param name="CanSeeAccount">
/// Whether this caller may be told which Modbot account belongs to this person. False leaves
/// <see cref="Account"/> null whether or not one exists.
/// </param>
public sealed record PersonView(
    PersonSide? VRChat,
    PersonSide? Discord,
    PersonAccount? Account,
    bool CanSeeAccount);
