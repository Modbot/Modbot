namespace Modbot.VRChat.Users;

/// <summary>Whether a VRChat account has VRChat+, read off its own tags.</summary>
public static class VRChatPlus
{
    /// <summary>The tag VRChat puts on an account with an active VRChat+ subscription.</summary>
    public const string Tag = "system_supporter";

    /// <summary>
    /// True when the tags carry <see cref="Tag"/>, false when they are there without it, and null
    /// when VRChat sent no tags at all: that is "not known", never "no VRChat+", because turning
    /// pictures off on a missing list would punish an account for something VRChat left out.
    /// </summary>
    public static bool? FromTags(IEnumerable<string>? tags)
    {
        if (tags is null)
            return null;

        var any = false;

        foreach (var tag in tags)
        {
            any = true;

            if (string.Equals(tag?.Trim(), Tag, StringComparison.Ordinal))
                return true;
        }

        return any ? false : null;
    }
}
