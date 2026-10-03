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
}
