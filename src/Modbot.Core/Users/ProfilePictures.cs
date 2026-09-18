using Modbot.Core.Data.Entities;

namespace Modbot.Core.Users;

/// <summary>
/// Which of a person's stored pictures Modbot shows: one rule, used everywhere a face appears.
/// </summary>
/// <remarks>
/// <para>
/// VRChat has carried a person's picture in three different fields over time. The profile
/// picture override (<c>profilePicOverride</c>) is the one a person chose, but it left every
/// call in API specification v1.21.0, so it is empty for anyone first seen after 2026-09-16 and
/// frozen for everyone else. The user icon (<c>iconUrl</c>) is what the public profile carries
/// today. The avatar thumbnail is the oldest and is on no call for other people any more, but a
/// row read before v1.21.0 may still hold one.
/// </para>
/// <para>
/// So: the override if set, else the icon, else the thumbnail, else nothing. The members list,
/// search, the Discord screens, the profile, the case-file snapshot and AutoMod's picture check
/// all ask here rather than each choosing for themselves, which is how they stopped agreeing
/// the last time a field moved.
/// </para>
/// </remarks>
public static class ProfilePictures
{
    /// <summary>The best picture available, or null when the row holds none.</summary>
    public static string? Best(string? profilePictureUrl, string? iconUrl, string? avatarThumbnailUrl)
    {
        if (!string.IsNullOrWhiteSpace(profilePictureUrl))
            return profilePictureUrl;

        if (!string.IsNullOrWhiteSpace(iconUrl))
            return iconUrl;

        return string.IsNullOrWhiteSpace(avatarThumbnailUrl) ? null : avatarThumbnailUrl;
    }

    /// <summary>The best picture a stored row holds, or null for a row with none or no row.</summary>
    public static string? Best(VRChatUser? row)
        => row is null ? null : Best(row.ProfilePictureUrl, row.IconUrl, row.CurrentAvatarThumbnailImageUrl);
}
