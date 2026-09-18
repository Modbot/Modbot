using System.Globalization;

namespace Modbot.Core;

/// <summary>
/// Compares two release versions, <c>YYYY.M.PATCH</c> (see <see cref="ModbotVersion"/>).
/// </summary>
/// <remarks>
/// <para>
/// Components are not zero-padded, so this compares them one at a time as numbers. Sorting the
/// strings instead puts <c>2026.1.10</c> before <c>2026.1.2</c>, which is the whole reason this
/// type exists rather than a <c>string.CompareOrdinal</c> at each call site.
/// </para>
/// <para>
/// Anything that is not three whole numbers separated by dots — a development build somebody
/// renamed, a tag with a suffix, an empty string — does not parse, and every comparison against it
/// answers "not newer". Telling an operator to update on the strength of a version nobody can read
/// would be worse than saying nothing.
/// </para>
/// </remarks>
public static class ReleaseVersion
{
    /// <summary>The three numbers in <paramref name="version"/>, or null when it is not a release version.</summary>
    public static (int Year, int Month, int Patch)? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var parts = version.Trim().Split('.');
        if (parts.Length != 3)
            return null;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return null;
        }

        return (year, month, patch);
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a later release than <paramref name="running"/>.
    /// Equal versions, an older candidate, and anything unreadable all answer false.
    /// </summary>
    public static bool IsNewer(string? running, string? candidate)
    {
        if (Parse(running) is not { } have || Parse(candidate) is not { } offered)
            return false;

        return Compare(offered, have) > 0;
    }

    /// <summary>Orders two parsed versions the way <see cref="IComparable"/> does.</summary>
    public static int Compare((int Year, int Month, int Patch) left, (int Year, int Month, int Patch) right)
    {
        if (left.Year != right.Year)
            return left.Year.CompareTo(right.Year);

        if (left.Month != right.Month)
            return left.Month.CompareTo(right.Month);

        return left.Patch.CompareTo(right.Patch);
    }
}
