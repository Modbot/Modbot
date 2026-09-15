using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Modbot.Core.Calendar;

/// <summary>
/// A short, stable hash of what a place should say, so a place is written only when that changes
/// (calendar design §3).
/// </summary>
public static class CalendarFingerprint
{
    public static string Of(params object?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var text = new StringBuilder();

        foreach (var part in parts)
        {
            text.Append(part switch
            {
                null => "␀",
                DateTimeOffset at => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                IEnumerable<string> list => string.Join('', list),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => part.ToString(),
            });

            text.Append('');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
