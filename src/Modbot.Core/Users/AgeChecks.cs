namespace Modbot.Core.Users;

/// <summary>The ways a moderator checks that somebody is 18 or older.</summary>
public static class AgeCheckMethods
{
    /// <summary>The person shows VRChat's 18+ badge.</summary>
    public const string Badge = "badge";

    /// <summary>The person showed a photo ID in a ticket. Modbot never sees or keeps it.</summary>
    public const string Photo = "photo";

    /// <summary>Whether this is one of the two methods.</summary>
    public static bool IsKnown(string? method) => method is Badge or Photo;

    /// <summary>The method in the words a reply uses.</summary>
    public static string Label(string method) => method == Photo ? "photo ID" : "badge";
}

/// <summary>One 18+ check a moderator asks for.</summary>
/// <param name="CheckerId">The Modbot account that did the check.</param>
/// <param name="Method">One of <see cref="AgeCheckMethods"/>.</param>
/// <param name="DiscordUserId">
/// The Discord member, when the check was asked for in Discord. Null for the profile button, where
/// the member is whoever is linked to the VRChat account.
/// </param>
/// <param name="VRChatUser">
/// A VRChat profile link or user id. Needed when the Discord member has no linked VRChat account;
/// ignored in favour of the link when they have one (a different one is refused).
/// </param>
/// <param name="ChannelId">The channel the check was typed in; the ticket to delete, if that is switched on.</param>
public sealed record AgeCheckRequest(
    Guid CheckerId,
    string Method,
    string? DiscordUserId,
    string? VRChatUser,
    string? ChannelId = null);

/// <summary>What an 18+ check came to.</summary>
/// <param name="Done">
/// The check is logged and the flag is set. Whatever came after (a role, the ticket) may still have
/// failed; <paramref name="Lines"/> says so.
/// </param>
/// <param name="Lines">One sentence each, the first being the headline.</param>
public sealed record AgeCheckAnswer(bool Done, IReadOnlyList<string> Lines)
{
    public static AgeCheckAnswer Refused(string reason) => new(false, [reason]);

    public string Text => string.Join('\n', Lines);
}

/// <summary>
/// The 18+ check: set the flag, give and take the roles the server chose, log method, date and
/// checker, and delete the ticket if that is switched on. The API implements it; <c>/ageverified</c>
/// in Discord and the profile button both call it, so they do the same thing.
/// </summary>
public interface IAgeCheck
{
    Task<AgeCheckAnswer> RunAsync(AgeCheckRequest request, CancellationToken ct = default);
}
