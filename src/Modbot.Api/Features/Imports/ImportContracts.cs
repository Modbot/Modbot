using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Imports;

/// <summary>One record the job refused, and why.</summary>
/// <param name="Line">The line in a newline-delimited file; the 1-based position in a JSON array.</param>
public sealed record ImportRejection(int Line, string Reason);

/// <summary>One import as the list and the progress endpoint show it (import design §4.2).</summary>
/// <param name="SeenBy">
/// The source records are filed under unless a record names its own (import design §5).
/// </param>
/// <param name="Status"><c>Queued</c>, <c>Running</c>, <c>Done</c> or <c>Failed</c>.</param>
/// <param name="Received">Records read from the file so far, well-formed or not.</param>
/// <param name="Imported">Facts written. For a dry run, facts that would have been.</param>
/// <param name="Skipped">Records that were already imported.</param>
/// <param name="AlreadyKnown">Records Modbot already had from somewhere else.</param>
/// <param name="Rejected">Records refused. <paramref name="Rejections"/> holds the first fifty.</param>
/// <param name="Error">For a failed import, why.</param>
public sealed record ImportView(
    Guid Id,
    string Source,
    string? FileName,
    bool DryRun,
    [property: JsonConverter(typeof(JsonStringEnumConverter<FactSource>))] FactSource SeenBy,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ImportStatus>))] ImportStatus Status,
    int Received,
    int Imported,
    int Skipped,
    int AlreadyKnown,
    int Rejected,
    IReadOnlyList<ImportRejection> Rejections,
    string? Error,
    string StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ImportView Of(Import import)
    {
        ArgumentNullException.ThrowIfNull(import);

        List<ImportRejection>? rejections = null;
        try
        {
            rejections = JsonSerializer.Deserialize<List<ImportRejection>>(import.Rejections, Json);
        }
        catch (JsonException)
        {
            // A row this process cannot read is still a row worth listing.
        }

        return new ImportView(
            import.Id,
            import.Source,
            import.FileName,
            import.DryRun,
            import.SeenBy,
            import.Status,
            import.Received,
            import.Imported,
            import.Skipped,
            import.AlreadyKnown,
            import.Rejected,
            rejections ?? [],
            import.Error,
            import.StartedByName,
            import.CreatedAt,
            import.StartedAt,
            import.FinishedAt);
    }

    public static string RejectionsJson(IEnumerable<ImportRejection> rejections)
        => JsonSerializer.Serialize(rejections, Json);
}

public sealed record ImportsResponse(IReadOnlyList<ImportView> Imports);
