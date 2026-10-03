using System.Text.Json;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Posts;

/// <summary>
/// The words each site is sent for a post, and how they are counted (posts design §3.3, §4.3).
/// </summary>
/// <remarks>
/// <para>
/// One place builds them, used by the senders, the preview and the counters alike, so the preview
/// is what goes out (calendar design §14.2's rule) and the look-and-adopt compares against the
/// very text that was sent.
/// </para>
/// <para>
/// <strong>Tidied the way the site would.</strong> Line ends become <c>\n</c> and the text is
/// trimmed, because Discord stores a message that way: a message read back for the look must equal
/// what Modbot wrote down, character for character.
/// </para>
/// </remarks>
public static class PostTexts
{
    /// <summary>The most a Discord message holds, counted in UTF-16 units as the calendar counts.</summary>
    public const int DiscordLimit = 2000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Line ends made <c>\n</c>, and the whole trimmed. Null becomes empty.</summary>
    public static string Tidy(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    /// <summary>A title on one line, trimmed; null when there is none.</summary>
    public static string? TidyTitle(string? title)
    {
        var tidy = Tidy(title).Replace('\n', ' ');
        return tidy.Length == 0 ? null : tidy;
    }

    /// <summary>The title one destination sends: its own, or the post's.</summary>
    public static string? TitleFor(Post post, PostDestination destination)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);
        return TidyTitle(destination.TitleOverride ?? post.Title);
    }

    /// <summary>The text one destination sends: its own, or the post's.</summary>
    public static string TextFor(Post post, PostDestination destination)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);
        return Tidy(destination.TextOverride ?? post.Text);
    }

    /// <summary>
    /// A Discord message: the role mention on its own line when there is one, then the title in bold
    /// when there is one, then the text (posts design §3.5).
    /// </summary>
    /// <remarks>
    /// The mention is in the text, so the role stays shown after an edit, which never pings. Whether
    /// the first send pings it is the gateway's allowed mentions, never this text.
    /// </remarks>
    public static string Discord(string? title, string text, string? roleId)
    {
        var lines = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(roleId))
            lines.Add($"<@&{roleId.Trim()}>");

        if (TidyTitle(title) is { } bold)
            lines.Add($"**{bold}**");

        var body = Tidy(text);
        if (body.Length > 0)
            lines.Add(body);

        return string.Join('\n', lines);
    }

    /// <summary>The Discord message one destination sends.</summary>
    public static string Discord(Post post, PostDestination destination)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);
        return Discord(TitleFor(post, destination), TextFor(post, destination), DiscordOptionsOf(destination).RoleId);
    }

    /// <summary>How long a Discord message is, the way the limit counts it.</summary>
    public static int DiscordLength(string content) => (content ?? string.Empty).Length;

    /// <summary>Whether a Discord message fits.</summary>
    public static bool DiscordFits(string content) => DiscordLength(content) <= DiscordLimit;

    /// <summary>A Discord destination's own choices. A row with none, or with words it cannot read, has the defaults.</summary>
    public static DiscordPostOptions DiscordOptionsOf(PostDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return ParseDiscordOptions(destination.Options);
    }

    /// <summary>Reads <see cref="DiscordPostOptions"/> from their JSON.</summary>
    public static DiscordPostOptions ParseDiscordOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new DiscordPostOptions();

        try
        {
            return JsonSerializer.Deserialize<DiscordPostOptions>(json, Json) ?? new DiscordPostOptions();
        }
        catch (JsonException)
        {
            return new DiscordPostOptions();
        }
    }

    /// <summary>Writes <see cref="DiscordPostOptions"/> as the JSON a destination keeps.</summary>
    public static string WriteDiscordOptions(DiscordPostOptions options) =>
        JsonSerializer.Serialize(options ?? new DiscordPostOptions(), Json);

    /// <summary>The address of a Discord message, for Open.</summary>
    public static string DiscordLink(string guildId, string channelId, string messageId) =>
        $"https://discord.com/channels/{guildId}/{channelId}/{messageId}";

    // ── VRChat (posts design §3.6) ───────────────────────────────────────────────────────

    /// <summary>
    /// How long a VRChat post's text is, for the counter: "VRChat 212". VRChat documents no
    /// maximum, so there is no limit to be over.
    /// </summary>
    public static int VRChatLength(string text) => Tidy(text).Length;

    /// <summary>A VRChat destination's own choices. A row with none, or with words it cannot read, has the defaults.</summary>
    public static VRChatPostOptions VRChatOptionsOf(PostDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return ParseVRChatOptions(destination.Options);
    }

    /// <summary>Reads <see cref="VRChatPostOptions"/> from their JSON; who sees it is Group unless it says Everyone.</summary>
    public static VRChatPostOptions ParseVRChatOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new VRChatPostOptions();

        try
        {
            var options = JsonSerializer.Deserialize<VRChatPostOptions>(json, Json) ?? new VRChatPostOptions();
            return VRChatPostVisibilities.IsKnown(options.Visibility) ? options : options with { Visibility = VRChatPostVisibilities.Group };
        }
        catch (JsonException)
        {
            return new VRChatPostOptions();
        }
    }

    /// <summary>Writes <see cref="VRChatPostOptions"/> as the JSON a destination keeps.</summary>
    public static string WriteVRChatOptions(VRChatPostOptions options) =>
        JsonSerializer.Serialize(options ?? new VRChatPostOptions(), Json);

    /// <summary>
    /// The roles a VRChat post is for: none for Everyone, otherwise the ids with blanks and repeats
    /// taken out. Never checked for shape (foundation §3.1.1).
    /// </summary>
    public static IReadOnlyList<string> VRChatRoles(VRChatPostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Visibility == VRChatPostVisibilities.Everyone)
            return [];

        return [.. (options.RoleIds ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The picture id a VRChat post is sent with, or null for text only: only while VRChat picture
    /// uploads are on (decision 14), and only when it was uploaded from the picture the post has now.
    /// </summary>
    public static string? VRChatImageFor(Post post, VRChatPostOptions options, bool uploadsOn)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(options);

        return uploadsOn
            && post.PictureId is { } picture
            && options.PictureId == picture
            && !string.IsNullOrWhiteSpace(options.ImageId)
                ? options.ImageId
                : null;
    }

    /// <summary>
    /// The group's posts page on vrchat.com, for Open. VRChat gives no address for one post, and
    /// the page's shape is not documented (posts design §9).
    /// </summary>
    public static string VRChatLink(string groupId) =>
        $"https://vrchat.com/home/group/{Uri.EscapeDataString(groupId)}/posts";

    /// <summary>
    /// Text cut down to its letters and digits, in lower case, after look-alike characters are
    /// folded to their plain form; text with none of those is kept as it is, trimmed. VRChat
    /// rewrites some characters it is sent (an en dash dropped, "." turned into a look-alike dot,
    /// calendar design §3.1), so a post read back is compared by this, never character for character.
    /// </summary>
    public static string LettersAndDigits(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        var folded = trimmed.Normalize(System.Text.NormalizationForm.FormKC);
        var plain = new System.Text.StringBuilder(folded.Length);

        foreach (var rune in folded.EnumerateRunes())
        {
            if (System.Text.Rune.IsLetterOrDigit(rune))
                plain.Append(System.Text.Rune.ToLowerInvariant(rune).ToString());
        }

        return plain.Length > 0 ? plain.ToString() : trimmed;
    }

    /// <summary>Whether two texts are the same once VRChat's changes to them are set aside.</summary>
    public static bool SameWords(string? a, string? b) =>
        string.Equals(LettersAndDigits(a), LettersAndDigits(b), StringComparison.Ordinal);

    // ── Bluesky (posts design §4.2c) ─────────────────────────────────────────────────────

    /// <summary>
    /// What Bluesky gets when it has no text of its own: the title on its own line when there is one,
    /// then the text, the way Discord's message reads without the bold. Bluesky has no title of its
    /// own, and a post read without it would lose what it is about.
    /// </summary>
    public static string Bluesky(string? title, string text)
    {
        var lines = new List<string>(2);

        if (TidyTitle(title) is { } heading)
            lines.Add(heading);

        var body = Tidy(text);
        if (body.Length > 0)
            lines.Add(body);

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The text one Bluesky destination sends: its own text as it is when it has one (the writer
    /// chose every word, title or none), otherwise the post's title and text.
    /// </summary>
    public static string Bluesky(Post post, PostDestination destination)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);

        return destination.TextOverride is { } own ? Tidy(own) : Bluesky(post.Title, post.Text);
    }

    /// <summary>A Bluesky destination's own choices. A row with none, or with words it cannot read, has the defaults.</summary>
    public static BlueskyPostOptions BlueskyOptionsOf(PostDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return ParseBlueskyOptions(destination.Options);
    }

    /// <summary>Reads <see cref="BlueskyPostOptions"/> from their JSON.</summary>
    public static BlueskyPostOptions ParseBlueskyOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new BlueskyPostOptions();

        try
        {
            return JsonSerializer.Deserialize<BlueskyPostOptions>(json, Json) ?? new BlueskyPostOptions();
        }
        catch (JsonException)
        {
            return new BlueskyPostOptions();
        }
    }

    /// <summary>Writes <see cref="BlueskyPostOptions"/> as the JSON a destination keeps.</summary>
    public static string WriteBlueskyOptions(BlueskyPostOptions options) =>
        JsonSerializer.Serialize(options ?? new BlueskyPostOptions(), Json);

    /// <summary>
    /// The card picture a Bluesky post goes with, or null for none: only when the text has a link for
    /// the card, and only when the small copy was made from the picture the post has now.
    /// </summary>
    public static Guid? BlueskyPictureFor(Post post, PostDestination destination, string text)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(destination);

        return destination.SitePictureId is { } copy
            && post.PictureId is { } picture
            && BlueskyOptionsOf(destination).PictureId == picture
            && global::Modbot.Core.Bluesky.BlueskyText.CardLink(text) is not null
                ? copy
                : null;
    }
}
