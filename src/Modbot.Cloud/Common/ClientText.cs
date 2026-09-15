using System.Globalization;

namespace Modbot.Cloud.Common;

/// <summary>Short strings a client sent that an admin will read.</summary>
public static class ClientText
{
    /// <summary>
    /// Trims, removes control and format characters, and cuts to <paramref name="maxLength"/>.
    /// Null when nothing is left.
    /// </summary>
    /// <remarks>
    /// Format characters go because a right-to-left override makes one row in a list render as
    /// another. The same rule as the server's pairing handler.
    /// </remarks>
    public static string? Clean(string? value, int maxLength)
    {
        if (value is null)
            return null;

        var cleaned = new string([.. value.Where(c =>
            !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)]).Trim();

        return cleaned.Length switch
        {
            0 => null,
            _ when cleaned.Length > maxLength => cleaned[..maxLength],
            _ => cleaned,
        };
    }
}
