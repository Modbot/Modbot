using Modbot.Shared.Names;

namespace Modbot.Core.Names;

/// <summary>The plain-letter form of a name, for the screens and the API, when it adds anything.</summary>
public static class PlainName
{
    /// <summary>
    /// <see cref="NameNormalizer.Readable"/> of the name, or null when that is the name itself or
    /// nothing at all -- so a screen shows a second line only for <c>𝕬𝖑𝖊𝖝</c>, never for <c>Alex</c>.
    /// </summary>
    public static string? Of(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        var plain = NameNormalizer.Readable(name);
        return plain.Length == 0 || string.Equals(plain, name, StringComparison.Ordinal) ? null : plain;
    }
}
