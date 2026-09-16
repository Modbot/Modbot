namespace Modbot.Api.Features.Logs;

/// <summary>Serilog's six levels, in order, as the log table stores them.</summary>
/// <remarks>
/// The table keeps the level as text so a new Serilog level never renumbers the ones already
/// written. "Warning and above" is therefore a list of names rather than a comparison, which is also
/// what lets it use the index on <c>(level, at)</c>.
/// </remarks>
public static class LogLevels
{
    public const string Verbose = "Verbose";
    public const string Debug = "Debug";
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Fatal = "Fatal";

    /// <summary>Lowest first.</summary>
    public static IReadOnlyList<string> All { get; } = [Verbose, Debug, Information, Warning, Error, Fatal];

    /// <summary>
    /// This level and every level above it. An unknown name means every level, so a filter that
    /// arrives misspelled shows more than asked rather than nothing at all.
    /// </summary>
    public static IReadOnlyList<string> AtLeast(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return All;

        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], level, StringComparison.OrdinalIgnoreCase))
                return [.. All.Skip(i)];
        }

        return All;
    }
}
