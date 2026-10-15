using Modbot.Api.Features.Posts;

namespace Modbot.Api.Features.BlueskyProfile;

/// <summary>What a <see cref="BlueskyProfileView"/> says about the account.</summary>
public static class BlueskyProfileStates
{
    /// <summary>No Bluesky account is saved: the page says to set one up.</summary>
    public const string NotSetUp = "notSetUp";

    /// <summary>The profile was read.</summary>
    public const string Ready = "ready";

    /// <summary>An account is saved, but its profile could not be read now; <c>problem</c> says why.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>The group's Bluesky account as its profile page shows it.</summary>
/// <param name="State"><c>notSetUp</c>, <c>ready</c> or <c>unavailable</c>.</param>
/// <param name="Problem">Why the profile could not be read, in a sentence. Null unless <c>unavailable</c>.</param>
/// <param name="Handle">The handle, without the <c>@</c>. Set whenever an account is saved.</param>
/// <param name="DisplayName">The name, or null for none.</param>
/// <param name="Bio">The bio, or null for none.</param>
/// <param name="AvatarUrl">The round picture's address, or null for none.</param>
/// <param name="BannerUrl">The wide picture's address, or null for none.</param>
/// <param name="Followers">How many follow the account.</param>
/// <param name="Following">How many the account follows.</param>
/// <param name="Posts">How many posts the account made.</param>
/// <param name="CanEdit">The caller may change the profile: they hold the permission that changes the Bluesky settings.</param>
/// <param name="SignedIn">Modbot can sign in as the account, so a save can go through: the last Check passed and the sign-in has not been refused.</param>
/// <param name="LimitedUntil">Bluesky is limiting Modbot: nothing is asked of it before this. Null when it is not.</param>
/// <param name="SaveError">Why the last save failed, until a save goes through. Null for none.</param>
/// <param name="SaveErrorAt">When that save failed.</param>
public sealed record BlueskyProfileView(
    string State,
    string? Problem,
    string? Handle,
    string? DisplayName,
    string? Bio,
    string? AvatarUrl,
    string? BannerUrl,
    int? Followers,
    int? Following,
    int? Posts,
    bool CanEdit,
    bool SignedIn,
    DateTimeOffset? LimitedUntil,
    string? SaveError,
    DateTimeOffset? SaveErrorAt);

/// <summary>One post Modbot sent to Bluesky, for the Posts tab.</summary>
/// <param name="PostId">The post's id in Modbot.</param>
/// <param name="Title">The post's title, or null for none.</param>
/// <param name="Text">Exactly what went out.</param>
/// <param name="Link">The post's address on Bluesky.</param>
/// <param name="PostedAt">When it was posted.</param>
/// <param name="Counts">The numbers under it when last counted, or null before the first count.</param>
public sealed record BlueskyProfilePostView(Guid PostId, string? Title, string Text, string? Link, DateTimeOffset? PostedAt, BlueskyCountsView? Counts);

/// <summary>One page of the Posts tab, newest first.</summary>
/// <param name="Posts">The posts on this page.</param>
/// <param name="Page">The page number, from 1.</param>
/// <param name="PageSize">How many posts a page holds.</param>
/// <param name="Total">How many posts Modbot sent to the account.</param>
public sealed record BlueskyProfilePosts(IReadOnlyList<BlueskyProfilePostView> Posts, int Page, int PageSize, int Total);
