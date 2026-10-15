using System.Text.Json.Nodes;

namespace Modbot.Core.Bluesky;

/// <summary>An account's profile as Bluesky shows it: the words, the two pictures and the three numbers.</summary>
/// <param name="Did">The account.</param>
/// <param name="Handle">The handle; <c>handle.invalid</c> when Bluesky could not confirm it.</param>
/// <param name="DisplayName">The name, or null for none.</param>
/// <param name="Bio">The bio, or null for none.</param>
/// <param name="AvatarUrl">The round picture's address on Bluesky's image server, or null for none.</param>
/// <param name="BannerUrl">The wide picture's address on Bluesky's image server, or null for none.</param>
/// <param name="Followers">How many follow the account, or null when not known.</param>
/// <param name="Following">How many the account follows, or null when not known.</param>
/// <param name="Posts">How many posts the account made, or null when not known.</param>
public sealed record BlueskyAccountProfile(
    string Did, string Handle, string? DisplayName, string? Bio, string? AvatarUrl, string? BannerUrl, int? Followers, int? Following, int? Posts);

/// <summary>
/// What a save of the profile changes: only the four things the page edits. A null part is left as it is.
/// </summary>
/// <param name="DisplayName">The new name; empty clears it.</param>
/// <param name="Bio">The new bio; empty clears it.</param>
/// <param name="Avatar">The new round picture, as the blob Bluesky took.</param>
/// <param name="Banner">The new wide picture, as the blob Bluesky took.</param>
public sealed record BlueskyProfileChange(string? DisplayName, string? Bio, JsonObject? Avatar, JsonObject? Banner);

/// <summary>
/// The profile record (<c>app.bsky.actor.profile/self</c>) and its limits. A save reads the record,
/// changes four fields and writes the rest back as it was: a pinned post, labels, the date the account
/// began and anything Bluesky adds later all stay.
/// </summary>
public static class BlueskyProfileRecord
{
    /// <summary>The most characters a name holds: <c>maxGraphemes</c> of <c>displayName</c> in the lexicon.</summary>
    public const int NameLimit = 64;

    /// <summary>The most characters a bio holds: <c>maxGraphemes</c> of <c>description</c> in the lexicon.</summary>
    public const int BioLimit = 256;

    /// <summary>The most bytes a picture or banner holds: <c>maxSize</c> 1000000 in the lexicon.</summary>
    public const int PictureLimit = BlueskyText.PictureMaxBytes;

    /// <summary>The picture types Bluesky takes for a profile: <c>accept</c> of <c>avatar</c> and <c>banner</c> in the lexicon.</summary>
    public static readonly IReadOnlyList<string> PictureTypes = ["image/png", "image/jpeg"];

    /// <summary>The refusal for a name over <see cref="NameLimit"/>.</summary>
    public const string NameTooLong = "The name can be at most 64 characters.";

    /// <summary>The refusal for a bio over <see cref="BioLimit"/>.</summary>
    public const string BioTooLong = "The bio can be at most 256 characters.";

    /// <summary>The refusal for a picture over <see cref="PictureLimit"/>.</summary>
    public const string PictureTooBig = "A picture must be under 1 MB.";

    /// <summary>The refusal for a picture that is not a PNG or a JPEG.</summary>
    public const string PictureNotAccepted = "A picture must be a PNG or a JPEG.";

    /// <summary>The words for a profile somebody saved on Bluesky while Modbot was saving it.</summary>
    public const string ChangedMeanwhile = "The profile was changed on Bluesky while saving. Try again.";

    /// <summary>The problem with a name, or null when it fits.</summary>
    public static string? ProblemWithName(string? name) => BlueskyText.Graphemes(name?.Trim()) > NameLimit ? NameTooLong : null;

    /// <summary>The problem with a bio, or null when it fits.</summary>
    public static string? ProblemWithBio(string? bio) => BlueskyText.Graphemes(bio?.Trim()) > BioLimit ? BioTooLong : null;

    /// <summary>
    /// The profile record after <paramref name="change"/>: a copy of <paramref name="current"/> (or a new
    /// record when the account has none) with the name, bio, picture and banner it names replaced, and
    /// every other field as it was. A name or bio of only spaces clears the field.
    /// </summary>
    public static JsonObject Apply(JsonObject? current, BlueskyProfileChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var record = current is null ? new JsonObject() : (JsonObject)current.DeepClone();
        record["$type"] = BlueskyClient.ProfileCollection;

        Set(record, "displayName", change.DisplayName?.Trim());
        Set(record, "description", change.Bio?.Trim());

        if (change.Avatar is { } avatar)
            record["avatar"] = avatar.DeepClone();

        if (change.Banner is { } banner)
            record["banner"] = banner.DeepClone();

        return record;
    }

    /// <summary>
    /// The address of a picture on Bluesky's image server: the account and the blob's content id, as the
    /// profile record holds them. Null for a record with no such picture.
    /// </summary>
    /// <param name="record">The profile record, or null for none.</param>
    /// <param name="field"><c>avatar</c> or <c>banner</c>.</param>
    /// <param name="did">The account the record is of.</param>
    public static string? PictureAddress(JsonObject? record, string field, string did)
    {
        ArgumentException.ThrowIfNullOrEmpty(did);

        return record?[field]?["ref"]?["$link"] is JsonValue link && link.TryGetValue<string>(out var cid) && cid.Length > 0
            ? $"https://cdn.bsky.app/img/{field}/plain/{Uri.EscapeDataString(did)}/{Uri.EscapeDataString(cid)}@jpeg"
            : null;
    }

    private static void Set(JsonObject record, string field, string? value)
    {
        if (value is null)
            return;

        if (value.Length == 0)
            record.Remove(field);
        else
            record[field] = value;
    }
}
