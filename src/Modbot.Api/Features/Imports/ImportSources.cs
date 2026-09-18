using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Imports;

/// <summary>
/// Which of Modbot's sources an imported record may be filed under (import design §5).
/// </summary>
/// <remarks>
/// <para>
/// Every member of <see cref="FactSource"/> except <see cref="FactSource.Import"/>, which is a
/// legacy value only old rows hold. An import no longer stamps a source of its own, because the
/// source column answers "who says so", and "somebody uploaded a file" is not an answer to that
/// question — it is an answer to "how did this get here", which the fact's own <c>importId</c>
/// and the <c>import_record</c> row answer, whatever source the record carries.
/// </para>
/// <para>
/// The names are the enum's own, matched without regard to case, because that is what
/// <c>GET /api/audit/filters</c> and the event stream already use for the same thing.
/// </para>
/// </remarks>
public static class ImportSources
{
    /// <summary>What a record says nothing becomes: a person put this in by hand, from elsewhere.</summary>
    public const FactSource Default = FactSource.Manual;

    /// <summary>Every source a record may be filed under, in enum order.</summary>
    public static IReadOnlyList<FactSource> All { get; } =
        Enum.GetValues<FactSource>().Where(s => s != FactSource.Import).ToList();

    /// <summary>The names <see cref="All"/> goes by, for the docs, the errors and the tests.</summary>
    public static IReadOnlyList<string> Names { get; } = All.Select(s => s.ToString()).ToList();

    /// <summary>
    /// Reads one source name. False with a reason for anything that is not one, so the caller can
    /// say the same sentence whether it came from the upload or from a record.
    /// </summary>
    public static bool TryParse(string? name, out FactSource source, out string? reason)
    {
        source = Default;
        reason = null;

        if (string.IsNullOrWhiteSpace(name))
            return true;

        var word = name.Trim();

        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), word, StringComparison.OrdinalIgnoreCase))
            {
                source = candidate;
                return true;
            }
        }

        reason = string.Equals(word, nameof(FactSource.Import), StringComparison.OrdinalIgnoreCase)
            ? "Import is no longer a source a record can be filed under. Pick the source the record "
              + $"really came from: {string.Join(", ", Names)}."
            : $"'{word}' is not a source. Pick one of: {string.Join(", ", Names)}.";

        return false;
    }
}
