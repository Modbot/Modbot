using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;
using VRChat.API.Model;

namespace Modbot.VRChat.Users;

/// <summary>Which of VRChat's two reads a snapshot came from.</summary>
/// <remarks>
/// They carry different fields and they can disagree about whether a person exists, so every
/// snapshot says which one it is (research: <c>vrchat-public-profile-findings.md</c>).
/// </remarks>
public enum VRChatReadKind
{
    /// <summary><c>GET /profile/{userId}</c> -- the main read: bio, pronouns, name, age verification.</summary>
    PublicProfile,

    /// <summary><c>GET /users/{userId}</c> -- the rare read: join date, tags, status line, pictures.</summary>
    User,
}

/// <summary>
/// A VRChat user's public profile, as Modbot records it: the fields worth watching for change,
/// plus the two that are kept but not watched.
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than the SDK's <c>User</c>, for three reasons. The SDK has several user
/// shapes -- <c>User</c>, <c>PublicProfile</c>, <c>LimitedUser</c>, <c>LimitedUserInstance</c>,
/// <c>CurrentUser</c> -- and whoever records a sighting of one should be able to map whichever
/// they have into one shape. The diff has to be over a chosen set of fields: <c>User</c> carries
/// the person's current instance, their last login and their online state, all of which change
/// constantly and none of which is a profile change. And the two reads carry different fields,
/// so a snapshot has to be able to say <em>nothing</em> about a field rather than say null.
/// </para>
/// <para>
/// <strong>A snapshot only speaks for the fields its response carried</strong>
/// (<see cref="Carried"/>). VRChat stopped returning the bio on <c>GET /users/{userId}</c>, and a
/// snapshot that reported that as "bio: null" would wipe a bio the public profile had just filled
/// in. Presence is read from the body, not from the typed object, because the SDK's generated
/// models give a missing string the value <c>""</c> -- which is exactly what a person who cleared
/// their bio also sends, and those two have to mean different things.
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

    /// <summary>Which call this came from.</summary>
    public VRChatReadKind Source { get; init; } = VRChatReadKind.PublicProfile;

    /// <summary>
    /// VRChat's own names for the fields this response actually carried. Anything not in here is
    /// not claimed by this snapshot and is left as it is stored.
    /// </summary>
    public IReadOnlySet<string> Carried { get; init; } = Fields.All;

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

    /// <summary>VRChat's own field names, so one spelling serves the diff, the presence set and the row.</summary>
    public static class Fields
    {
        public const string DisplayName = "displayName";
        public const string Bio = "bio";
        public const string StatusDescription = "statusDescription";
        public const string Pronouns = "pronouns";
        public const string CurrentAvatarImageUrl = "currentAvatarImageUrl";
        public const string CurrentAvatarThumbnailImageUrl = "currentAvatarThumbnailImageUrl";
        public const string ProfilePicOverride = "profilePicOverride";
        public const string DateJoined = "date_joined";
        public const string Tags = "tags";
        public const string AgeVerificationStatus = "ageVerificationStatus";
        public const string AgeVerified = "ageVerified";
        public const string Status = "status";
        public const string LastPlatform = "last_platform";

        /// <summary>Everything a stored row knows about. What <see cref="FromRow"/> speaks for.</summary>
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            DisplayName, Bio, StatusDescription, Pronouns,
            CurrentAvatarImageUrl, CurrentAvatarThumbnailImageUrl, ProfilePicOverride,
            DateJoined, Tags, AgeVerificationStatus, AgeVerified, Status, LastPlatform,
        };

        /// <summary>What <c>GET /users/{userId}</c> can carry.</summary>
        public static readonly IReadOnlySet<string> OnUser = All;

        /// <summary>
        /// What <c>GET /profile/{userId}</c> can carry, of the fields Modbot stores.
        /// </summary>
        /// <remarks>
        /// The public profile also carries <c>trustTags</c>, <c>languages</c>,
        /// <c>representedGroup</c>, <c>hasVrcPlus</c> and <c>iconUrl</c>. None of those is one of
        /// Modbot's columns, and <c>trustTags</c> and <c>iconUrl</c> are deliberately <em>not</em>
        /// written to <c>tags</c> and <c>profile_picture_url</c>: they are different, smaller
        /// fields, and mixing them would make every alternation between the two calls look like a
        /// profile change. They are kept in <c>raw_public_profile</c> instead.
        /// </remarks>
        public static readonly IReadOnlySet<string> OnPublicProfile = new HashSet<string>(StringComparer.Ordinal)
        {
            DisplayName, Bio, Pronouns, AgeVerificationStatus, AgeVerified,
        };
    }

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
    /// <para>
    /// Both reads carry both fields, so the sticky flag is fed by the frequent one and does not
    /// wait a week for the rare one.
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
    /// status, and what decides which fields this response speaks for.
    /// </param>
    public static VRChatUserSnapshot From(User user, JsonObject? raw = null)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new VRChatUserSnapshot
        {
            UserId = user.Id,
            Source = VRChatReadKind.User,
            Carried = CarriedBy(Fields.OnUser, raw),
            DisplayName = Blank(user.DisplayName),
            Bio = Blank(user.Bio),
            StatusDescription = Blank(user.StatusDescription),
            Pronouns = Blank(user.Pronouns),
            CurrentAvatarImageUrl = Blank(user.CurrentAvatarImageUrl),
            CurrentAvatarThumbnailImageUrl = Blank(user.CurrentAvatarThumbnailImageUrl),
            ProfilePictureUrl = Blank(user.ProfilePicOverride),
            DateJoined = user.DateJoined == default ? null : user.DateJoined,
            Tags = Sorted(user.Tags),
            AgeVerificationStatus = ReadText(raw, Fields.AgeVerificationStatus) ?? StatusWord(user.AgeVerificationStatus),
            AgeVerified = ReadBool(raw, Fields.AgeVerified) ?? user.AgeVerified,
            Status = StatusWord(user.Status),
            LastPlatform = Blank(user.LastPlatform),
        };
    }

    /// <summary>
    /// Builds a snapshot from the public profile -- the main read.
    /// </summary>
    /// <remarks>
    /// It speaks for five fields and says nothing about the rest, so recording one never clears a
    /// status line, a join date, a tag list or an avatar picture that the rarer user read filled in.
    /// </remarks>
    public static VRChatUserSnapshot FromPublicProfile(string userId, PublicProfile profile, JsonObject? raw = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(profile);

        return new VRChatUserSnapshot
        {
            // VRChat's own id for the person, not the one asked for, when it sends one back.
            UserId = Blank(profile.Id) ?? userId,
            Source = VRChatReadKind.PublicProfile,
            Carried = CarriedBy(Fields.OnPublicProfile, raw),
            DisplayName = Blank(profile.DisplayName),
            Bio = Blank(profile.Bio),
            Pronouns = Blank(profile.Pronouns),
            AgeVerificationStatus =
                ReadText(raw, Fields.AgeVerificationStatus)
                ?? (profile.AgeVerificationStatus is { } status ? StatusWord(status) : null),
            AgeVerified = ReadBool(raw, Fields.AgeVerified) ?? profile.AgeVerified,
        };
    }

    /// <summary>The snapshot a stored row represents, or null when the row has never been read.</summary>
    public static VRChatUserSnapshot? FromRow(VRChatUser row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // Either read filling the row makes it a profile there is something to diff against.
        if (row.LastRefreshedAt is null && row.LastUserReadAt is null)
            return null;

        return new VRChatUserSnapshot
        {
            UserId = row.UserId,
            Carried = Fields.All,
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

    /// <summary>
    /// Copies the profile onto its row. Touches nothing about the sticky flag or the timestamps,
    /// and nothing this response did not carry.
    /// </summary>
    public void ApplyTo(VRChatUser row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (Has(Fields.DisplayName)) row.DisplayName = DisplayName;
        if (Has(Fields.Bio)) row.Bio = Bio;
        if (Has(Fields.Status)) row.Status = Status;
        if (Has(Fields.StatusDescription)) row.StatusDescription = StatusDescription;
        if (Has(Fields.Pronouns)) row.Pronouns = Pronouns;
        if (Has(Fields.CurrentAvatarImageUrl)) row.CurrentAvatarImageUrl = CurrentAvatarImageUrl;
        if (Has(Fields.CurrentAvatarThumbnailImageUrl)) row.CurrentAvatarThumbnailImageUrl = CurrentAvatarThumbnailImageUrl;
        if (Has(Fields.ProfilePicOverride)) row.ProfilePictureUrl = ProfilePictureUrl;
        if (Has(Fields.DateJoined)) row.DateJoined = DateJoined;
        if (Has(Fields.Tags)) row.Tags = JsonSerializer.Serialize(Tags);
        if (Has(Fields.LastPlatform)) row.LastPlatform = LastPlatform;
        if (Has(Fields.AgeVerificationStatus)) row.AgeVerificationStatus = AgeVerificationStatus;
        if (Has(Fields.AgeVerified)) row.AgeVerified = AgeVerified;
    }

    /// <summary>
    /// Every watched field this response carried that differs, as <c>{field: {old, new}}</c> under
    /// VRChat's own field names -- the shape the audit-log mapper lifts under <c>changed</c>, so
    /// the timeline has one diff shape.
    /// </summary>
    public JsonObject DifferencesFrom(VRChatUserSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        var changed = new JsonObject();

        Text(changed, Fields.DisplayName, previous.DisplayName, DisplayName);
        Text(changed, Fields.Bio, previous.Bio, Bio);
        Text(changed, Fields.StatusDescription, previous.StatusDescription, StatusDescription);
        Text(changed, Fields.Pronouns, previous.Pronouns, Pronouns);
        Text(changed, Fields.CurrentAvatarImageUrl, previous.CurrentAvatarImageUrl, CurrentAvatarImageUrl);
        Text(changed, Fields.CurrentAvatarThumbnailImageUrl, previous.CurrentAvatarThumbnailImageUrl, CurrentAvatarThumbnailImageUrl);
        Text(changed, Fields.ProfilePicOverride, previous.ProfilePictureUrl, ProfilePictureUrl);
        Text(changed, Fields.AgeVerificationStatus, previous.AgeVerificationStatus, AgeVerificationStatus);

        if (Has(Fields.AgeVerified) && previous.AgeVerified != AgeVerified)
            changed[Fields.AgeVerified] = Pair(previous.AgeVerified, AgeVerified);

        if (Has(Fields.DateJoined) && previous.DateJoined != DateJoined)
        {
            changed["dateJoined"] = Pair(
                previous.DateJoined?.ToString("O", CultureInfo.InvariantCulture),
                DateJoined?.ToString("O", CultureInfo.InvariantCulture));
        }

        if (Has(Fields.Tags) && !previous.Tags.SequenceEqual(Tags, StringComparer.Ordinal))
        {
            changed[Fields.Tags] = new JsonObject
            {
                ["old"] = new JsonArray([.. previous.Tags.Select(t => JsonValue.Create(t))]),
                ["new"] = new JsonArray([.. Tags.Select(t => JsonValue.Create(t))]),
            };
        }

        return changed;
    }

    /// <summary>The small payload the first-seen fact carries: enough to diff from, nothing that churns.</summary>
    public JsonObject Baseline()
    {
        var baseline = new JsonObject();

        if (Has(Fields.DisplayName)) baseline[Fields.DisplayName] = DisplayName;
        if (Has(Fields.Pronouns)) baseline[Fields.Pronouns] = Pronouns;
        if (Has(Fields.DateJoined)) baseline["dateJoined"] = DateJoined?.ToString("O", CultureInfo.InvariantCulture);
        if (Has(Fields.AgeVerificationStatus)) baseline[Fields.AgeVerificationStatus] = AgeVerificationStatus;
        if (Has(Fields.AgeVerified)) baseline[Fields.AgeVerified] = AgeVerified;
        if (Has(Fields.Tags)) baseline[Fields.Tags] = new JsonArray([.. Tags.Select(t => JsonValue.Create(t))]);

        return baseline;
    }

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

    /// <summary>The raw body with <see cref="NeverStored"/> removed, ready for its <c>jsonb</c> column.</summary>
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

    private bool Has(string field) => Carried.Contains(field);

    /// <summary>
    /// Which of a call's fields this particular response actually sent.
    /// </summary>
    /// <remarks>
    /// With no body to look at, everything the call can carry counts as carried -- that is the
    /// SDK's own serialisation of the typed object, which emits every property, and it is the
    /// behaviour that was here before bodies were consulted at all.
    /// </remarks>
    private static IReadOnlySet<string> CarriedBy(IReadOnlySet<string> possible, JsonObject? raw)
    {
        if (raw is null || raw.Count == 0)
            return possible;

        var carried = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in possible)
        {
            if (raw.ContainsKey(field))
                carried.Add(field);
        }

        return carried;
    }

    private void Text(JsonObject changed, string field, string? before, string? after)
    {
        if (Has(field) && !string.Equals(before, after, StringComparison.Ordinal))
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
