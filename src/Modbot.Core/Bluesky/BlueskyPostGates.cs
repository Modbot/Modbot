using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Modbot.Core.Bluesky;

/// <summary>
/// Who may reply to a post and whether it may be quoted, and the two small records Bluesky keeps those
/// choices in: <c>app.bsky.feed.threadgate</c> and <c>app.bsky.feed.postgate</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both records sit in the author's own repository at the very same key as the post and name it by its
/// address. A threadgate's <c>allow</c> is a list of rules (<c>mentionRule</c>, <c>followerRule</c>,
/// <c>followingRule</c> or <c>listRule</c>, up to five): an empty list says nobody may reply, and no
/// record says anyone may. A postgate's <c>embeddingRules</c> holds <c>disableRule</c> to turn quote
/// posts off; no record leaves them open (github.com/bluesky-social/atproto, lexicons/app/bsky/feed/threadgate.json
/// and postgate.json).
/// </para>
/// </remarks>
public static class BlueskyPostGates
{
    public const string ThreadgateCollection = "app.bsky.feed.threadgate";
    public const string PostgateCollection = "app.bsky.feed.postgate";

    /// <summary>The choice of nobody. Anyone is no choice at all (null).</summary>
    public const string Nobody = "nobody";

    public const string Followers = "followers";
    public const string Following = "following";
    public const string Mentioned = "mentioned";

    /// <summary>The three groups a mix is made of, in the order the form shows them.</summary>
    public static readonly IReadOnlyList<string> Groups = [Followers, Following, Mentioned];

    /// <summary>
    /// The wire word for "anyone" as the form sends it. It is kept as no choice (null), so an event that
    /// never chose has the very same post as before.
    /// </summary>
    public const string Anyone = "anyone";

    /// <summary>
    /// The choice as it is kept: null for anyone, <c>["nobody"]</c> for nobody, else the groups named, once
    /// each, in the form's order. Words that are not a choice are dropped; <see cref="IsChoice"/> finds them first.
    /// </summary>
    public static List<string>? Normalize(IEnumerable<string>? words)
    {
        if (words is null)
            return null;

        var named = words.Select(w => w.Trim().ToLowerInvariant()).ToList();

        if (named.Contains(Nobody))
            return [Nobody];

        var groups = Groups.Where(named.Contains).ToList();
        return groups.Count == 0 ? null : groups;
    }

    /// <summary>Whether every word is one of the choices; "nobody" and "anyone" stand alone.</summary>
    public static bool IsChoice(IReadOnlyList<string>? words)
    {
        if (words is null)
            return true;

        var named = words.Select(w => w.Trim().ToLowerInvariant()).ToList();

        if (named.Any(w => w != Anyone && w != Nobody && !Groups.Contains(w)))
            return false;

        return named.Count <= 1 || named.All(Groups.Contains);
    }

    /// <summary>
    /// The threadgate for a post, or null when anyone may reply.
    /// </summary>
    /// <param name="postUri">The post's address (<c>at://did/app.bsky.feed.post/key</c>).</param>
    /// <param name="whoCanReply">The choice, as <see cref="Normalize"/> keeps it.</param>
    public static JsonObject? Threadgate(string postUri, IReadOnlyList<string>? whoCanReply, DateTimeOffset createdAt)
    {
        if (whoCanReply is null)
            return null;

        var allow = new JsonArray();

        // Mentions first, as Bluesky's own app writes them.
        if (whoCanReply.Contains(Mentioned))
            allow.Add(new JsonObject { ["$type"] = "app.bsky.feed.threadgate#mentionRule" });
        if (whoCanReply.Contains(Followers))
            allow.Add(new JsonObject { ["$type"] = "app.bsky.feed.threadgate#followerRule" });
        if (whoCanReply.Contains(Following))
            allow.Add(new JsonObject { ["$type"] = "app.bsky.feed.threadgate#followingRule" });

        return new JsonObject
        {
            ["$type"] = ThreadgateCollection,
            ["post"] = postUri,
            ["allow"] = allow,
            ["createdAt"] = Time(createdAt),
        };
    }

    /// <summary>The postgate that turns quote posts off, or null when they stay open.</summary>
    public static JsonObject? Postgate(string postUri, bool quotesOff, DateTimeOffset createdAt) =>
        quotesOff
            ? new JsonObject
            {
                ["$type"] = PostgateCollection,
                ["post"] = postUri,
                ["embeddingRules"] = new JsonArray(new JsonObject { ["$type"] = "app.bsky.feed.postgate#disableRule" }),
                ["createdAt"] = Time(createdAt),
            }
            : null;

    private static string Time(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>The languages a post says it is written in: <c>langs</c> of <c>app.bsky.feed.post</c>, up to three BCP 47 codes.</summary>
public static partial class BlueskyLanguages
{
    /// <summary>The most languages a post names.</summary>
    public const int Most = 3;

    // A language code with optional parts, as "en", "pt-BR" or "zh-Hant". Bluesky's own check is wider.
    [GeneratedRegex("^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$")]
    private static partial Regex Shape();

    /// <summary>Whether this reads as a language code.</summary>
    public static bool IsCode(string? code) => code is { Length: <= 35 } && Shape().IsMatch(code);

    /// <summary>The codes trimmed, without blanks or repeats, in the order given. Null when none are left.</summary>
    public static List<string>? Clean(IEnumerable<string>? codes)
    {
        var kept = (codes ?? [])
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return kept.Count == 0 ? null : kept;
    }
}
