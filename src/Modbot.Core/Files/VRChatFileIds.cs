using System.Text.RegularExpressions;

namespace Modbot.Core.Files;

/// <summary>
/// The VRChat file id inside whatever somebody pasted for a VRChat picture: the id itself, or any
/// VRChat link with one in it (calendar design §15.1, added 2026-10-02).
/// </summary>
/// <remarks>
/// <para>
/// A tester pasted the whole link VRChat's site gives a picture, then the part from <c>file_</c> to
/// the end, <c>_blob</c> and all, and VRChat refused both at publish time. The id is in the link, so
/// Modbot takes it out rather than asking a person to cut it by hand.
/// </para>
/// <para>
/// <strong>This finds; it does not judge.</strong> Ids are never checked for shape (foundation
/// §3.1.1). An id in VRChat's usual form, <c>file_</c> and a UUID, is taken as exactly that, which is
/// what drops a <c>_blob</c>, an extension or a version after it. Anything else that starts
/// <c>file_</c> is taken up to the next character that cannot be part of an id in a link (a slash,
/// a question mark, a dot and so on), so an odd id VRChat really issued still goes through. Only text
/// with no <c>file_</c> in it at all, or a link on somebody else's site, has no id.
/// </para>
/// </remarks>
public static partial class VRChatFileIds
{
    /// <summary>What the refusal shows as the shape to aim for.</summary>
    public const string Example = "file_1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d";

    /// <summary>The sentence a save gives for text with no file id in it.</summary>
    public const string NotFound = "Use a VRChat file link or id, like " + Example + ".";

    /// <summary>
    /// The file id in <paramref name="text"/>, or null when there is none: blank text, a link that
    /// is not VRChat's, or text with no <c>file_</c> in it.
    /// </summary>
    public static string? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();

        // A link is VRChat's or it is not a VRChat picture: "file_" in somebody else's address is
        // somebody else's file.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var link)
            && link.Scheme is "http" or "https"
            && !IsVRChatHost(link.Host))
        {
            return null;
        }

        if (UsualForm().Match(trimmed) is { Success: true } usual)
            return usual.Value;

        return AnyForm().Match(trimmed) is { Success: true } any ? any.Value : null;
    }

    /// <summary>
    /// The file id in a picture link, only when the link is on one of VRChat's hosts. The form fills
    /// the VRChat picture from it (calendar design §15.1).
    /// </summary>
    public static string? InLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)
            || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https")
            || !IsVRChatHost(address.Host))
        {
            return null;
        }

        return Find(link);
    }

    /// <summary><c>vrchat.cloud</c>, <c>vrchat.com</c> and anything under either.</summary>
    public static bool IsVRChatHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var h = host.TrimEnd('.');
        return h.Equals("vrchat.cloud", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".vrchat.cloud", StringComparison.OrdinalIgnoreCase)
            || h.Equals("vrchat.com", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".vrchat.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>vrchat.cloud</c> and anything under it: where VRChat serves its files from, only to a
    /// signed-in session, so a picture there is fetched through Modbot's VRChat side.
    /// </summary>
    public static bool IsVRChatFileHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var h = host.TrimEnd('.');
        return h.Equals("vrchat.cloud", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".vrchat.cloud", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        "file_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsualForm();

    // Letters, digits, '-' and '_' only: everything else ends an id inside a link.
    [GeneratedRegex("file_[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex AnyForm();
}
