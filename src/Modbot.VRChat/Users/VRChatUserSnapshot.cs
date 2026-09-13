using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;
using VRChat.API.Model;

namespace Modbot.VRChat.Users;

/// <summary>
/// A VRChat user's public profile, as Modbot records it: the fields worth watching for change,
/// plus the two that are kept but not watched.
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than the SDK's <c>User</c>, for two reasons. The SDK has several user
/// shapes -- <c>User</c>, <c>LimitedUser</c>, <c>LimitedUserInstance</c>, <c>CurrentUser</c> --
/// and whoever records a sighting of a user object (the sync today; account linking later)
/// should be able to map whichever one they have into one shape. And the diff has to be over a
/// chosen set of fields: <c>User</c> carries the person's current instance, their last login and
/// their online state, all of which change constantly and none of which is a profile change.
/// </para>
/// <para>
/// <strong>Age verification is read from the raw JSON first.</strong> The SDK types
/// <c>ageVerificationStatus</c> as an enum of three values, one of them already marked obsolete,
/// and a value VRChat adds tomorrow would fail deserialisation. The status is text on the wire
/// and is kept as text here (research: <c>vrchat-user-object-findings.md</c>).
/// </para>
/// </remarks>
public sealed record VRChatUserSnapshot
{
    public required string UserId { get; init; }

    public string? DisplayName { get; init; }
    public string? Bio { get; init; }
    public string? StatusDescription { get; init; }
    public string? Pronouns { get; init; }
    public string? CurrentAvatarImageUrl { get; init; }
    public string? CurrentAvatarThumbnailImageUrl { get; init; }
    public string? ProfilePictureUrl { get; init; }
    public DateOnly? DateJoined { get; init; }

    /// <summary>VRChat's tags, sorted, so two responses that differ only in order are not a change.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary><c>ageVerificationStatus</c> as text: <c>18+</c>, <c>hidden</c>, <c>verified</c>, or whatever VRChat sends next.</summary>
    public string? AgeVerificationStatus { get; init; }

    /// <summary><c>ageVerified</c> as sent.</summary>
    public bool? AgeVerified { get; init; }

    // Kept on the row, never diffed: both flip with every session.
    public string? Status { get; init; }
    public string? LastPlatform { get; init; }

    /// <summary>
    /// Whether this profile, as it stands, says the person is 18+ verified.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any positive signal counts (user profile sync design §4): a status of <c>18+</c>, the
    /// obsolete <c>verified</c>, or <c>ageVerified</c> true. VRChat's age verification confirms
    /// 18 or over and nothing else, so every one of these is the same claim made in a different
    /// field or an older vocabulary. What none of them can say is that the person is <em>not</em>
    /// verified -- <c>hidden</c> means hidden -- which is why the flag this feeds is sticky.
    /// </para>
    /// </remarks>
    public bool ShowsEighteenPlus =>
        string.Equals(AgeVerificationStatus, "18+", StringComparison.Ordinal)
        || string.Equals(AgeVerificationStatus, "verified", StringComparison.Ordinal)
        || AgeVerified == true;

    /// <summary>
    /// Builds a snapshot from the SDK's full user object and, when available, the body it was
    /// deserialised from.
    /// </summary>
    /// <param name="raw">
    /// The response body verbatim. Preferred over the typed object for the age verification
    /// status, and the source of <see cref="StoredJson"/>.
    /// </param>
    public static VRChatUserSnapshot From(User user, JsonObject? raw = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new VRChatUserSnapshot
        {
            UserId = user.Id,
            DisplayName = user.DisplayName,
            Bio = Blank(user.Bio),
            StatusDescription = Blank(user.StatusDescription),
            Pronouns = Blank(user.Pronouns),
            CurrentAvatarImageUrl = Blank(user.CurrentAvatarImageUrl),
            CurrentAvatarThumbnailImageUrl = Blank(user.CurrentAvatarThumbnailImageUrl),
            ProfilePictureUrl = Blank(user.ProfilePicOverride),
            DateJoined = user.DateJoined == default ? null : user.DateJoined,
            Tags = Sorted(user.Tags),
            AgeVerificationStatus = ReadText(raw, "ageVerificationStatus") ?? StatusWord(user.AgeVerificationStatus),
            AgeVerified = ReadBool(raw, "ageVerified") ?? user.AgeVerified,
            Status = StatusWord(user.Status),
            LastPlatform = Blank(user.LastPlatform),
        };
    }

    /// <summary>The snapshot a stored row represents, or null when the row has never been refreshed.</summary>
    public static VRChatUserSnapshot? FromRow(VRChatUser row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.LastRefreshedAt is null)
            return null;

        return new VRChatUserSnapshot
        {
            UserId = row.UserId,
            DisplayName = row.DisplayName,
            Bio = row.Bio,
            StatusDescription = row.StatusDescription,
            Pronouns = row.Pronouns,
            CurrentAvatarImageUrl = row.CurrentAvatarImageUrl,
            CurrentAvatarThumbnailImageUrl = row.CurrentAvatarThumbnailImageUrl,
            ProfilePictureUrl = row.ProfilePictureUrl,
            DateJoined = row.DateJoined,
            Tags = ReadTags(row.Tags),
            AgeVerificationStatus = row.AgeVerificationStatus,
            AgeVerified = row.AgeVerified,
            Status = row.Status,
            LastPlatform = row.LastPlatform,
        };
    }

    /// <summary>Copies the profile onto its row. Touches nothing about the sticky flag or the timestamps.</summary>
    public void ApplyTo(VRChatUser row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.DisplayName = DisplayName;
        row.Bio = Bio;
        row.Status = Status;
        row.StatusDescription = StatusDescription;
        row.Pronouns = Pronouns;
        row.CurrentAvatarImageUrl = CurrentAvatarImageUrl;
        row.CurrentAvatarThumbnailImageUrl = CurrentAvatarThumbnailImageUrl;
        row.ProfilePictureUrl = ProfilePictureUrl;
        row.DateJoined = DateJoined;
        row.Tags = JsonSerializer.Serialize(Tags);
        row.LastPlatform = LastPlatform;
        row.AgeVerificationStatus = AgeVerificationStatus;
        row.AgeVerified = AgeVerified;
    }

    /// <summary>
    /// Every watched field that differs, as <c>{field: {old, new}}</c> under VRChat's own field
    /// names -- the shape the audit-log mapper lifts under <c>changed</c>, so the timeline has one
    /// diff shape.
    /// </summary>
    public JsonObject DifferencesFrom(VRChatUserSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        var changed = new JsonObject();

        Text(changed, "displayName", previous.DisplayName, DisplayName);
        Text(changed, "bio", previous.Bio, Bio);
        Text(changed, "statusDescription", previous.StatusDescription, StatusDescription);
        Text(changed, "pronouns", previous.Pronouns, Pronouns);
        Text(changed, "currentAvatarImageUrl", previous.CurrentAvatarImageUrl, CurrentAvatarImageUrl);
        Text(changed, "currentAvatarThumbnailImageUrl", previous.CurrentAvatarThumbnailImageUrl, CurrentAvatarThumbnailImageUrl);
        Text(changed, "profilePicOverride", previous.ProfilePictureUrl, ProfilePictureUrl);
        Text(changed, "ageVerificationStatus", previous.AgeVerificationStatus, AgeVerificationStatus);

        if (previous.AgeVerified != AgeVerified)
            changed["ageVerified"] = Pair(previous.AgeVerified, AgeVerified);

        if (previous.DateJoined != DateJoined)
        {
            changed["dateJoined"] = Pair(
                previous.DateJoined?.ToString("O", CultureInfo.InvariantCulture),
                DateJoined?.ToString("O", CultureInfo.InvariantCulture));
        }

        if (!previous.Tags.SequenceEqual(Tags, StringComparer.Ordinal))
        {
            changed["tags"] = new JsonObject
            {
                ["old"] = new JsonArray([.. previous.Tags.Select(t => JsonValue.Create(t))]),
                ["new"] = new JsonArray([.. Tags.Select(t => JsonValue.Create(t))]),
            };
        }

        return changed;
    }

    /// <summary>The small payload the first-seen fact carries: enough to diff from, nothing that churns.</summary>
    public JsonObject Baseline() => new()
    {
        ["displayName"] = DisplayName,
        ["pronouns"] = Pronouns,
        ["dateJoined"] = DateJoined?.ToString("O", CultureInfo.InvariantCulture),
        ["ageVerificationStatus"] = AgeVerificationStatus,
        ["ageVerified"] = AgeVerified,
        ["tags"] = new JsonArray([.. Tags.Select(t => JsonValue.Create(t))]),
    };

    /// <summary>
    /// The fields Modbot must not keep from a user object, even in the raw copy.
    /// </summary>
    /// <remarks>
    /// The locations can carry <c>~nonce(…)</c>, an instance secret, and spec 5.3 says Modbot
    /// never persists those. <c>note</c> is the Modbot account's private note on the person and
    /// <c>friendKey</c> is a credential-shaped value neither of which is profile data.
    /// </remarks>
    public static readonly IReadOnlyList<string> NeverStored =
    [
        "location",
        "travelingToLocation",
        "travelingToInstance",
        "instanceId",
        "note",
        "friendKey",
    ];

    /// <summary>The raw body with <see cref="NeverStored"/> removed, ready for the <c>raw_profile</c> column.</summary>
    public static string? StoredJson(JsonObject? raw)
    {
        if (raw is null)
            return null;

        var copy = (JsonObject)raw.DeepClone();
        foreach (var field in NeverStored)
            copy.Remove(field);

        return copy.ToJsonString();
    }

    /// <summary>Parses a response body, or returns null when it is not a JSON object.</summary>
    public static JsonObject? ParseRaw(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Text(JsonObject changed, string field, string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
            changed[field] = Pair(before, after);
    }

    private static JsonObject Pair(string? before, string? after) =>
        new() { ["old"] = before, ["new"] = after };

    private static JsonObject Pair(bool? before, bool? after) =>
        new() { ["old"] = before, ["new"] = after };

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static IReadOnlyList<string> Sorted(IEnumerable<string>? tags) =>
        tags is null ? [] : tags.Where(t => t is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> ReadTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return Sorted(JsonSerializer.Deserialize<List<string>>(json));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadText(JsonObject? raw, string field) =>
        raw?[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? ReadBool(JsonObject? raw, string field) =>
        raw?[field] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    /// <summary>
    /// The wire word for an SDK enum member. The SDK names <c>18+</c> <c>plus18</c>, so the
    /// enum name is not the answer; the <c>EnumMember</c> value is.
    /// </summary>
    private static string? StatusWord<T>(T value) where T : struct, Enum
    {
        var member = typeof(T).GetField(value.ToString());
        var wire = member?
            .GetCustomAttributes(typeof(System.Runtime.Serialization.EnumMemberAttribute), false)
            .OfType<System.Runtime.Serialization.EnumMemberAttribute>()
            .FirstOrDefault()?.Value;

        return wire ?? value.ToString();
    }
}
