namespace Modbot.Analytics.Facts;

/// <summary>
/// What an imported fact carries in its payload so it can be traced back to the upload that
/// wrote it (import design §5.1).
/// </summary>
/// <remarks>
/// An imported fact is filed under the source the record really came from — VRChat's audit log,
/// Discord, a person — so the source column can no longer be read as "this arrived in a file".
/// These keys are where that reading went. They are here, beside the writer, rather than in the
/// import feature, because the writer has to recognise an imported fact too: working out the
/// roles somebody held is a query per fact, and an import that reaches back years runs for
/// thousands of them with nothing in the recorded role changes to answer from.
/// </remarks>
public static class ImportedFact
{
    /// <summary>The import's id.</summary>
    public const string ImportIdKey = "importId";

    /// <summary>The upload's source label: the name of the platform the file came from.</summary>
    public const string SourceKey = "importSource";

    /// <summary>The old platform's own id for the record, when it had one.</summary>
    public const string ExternalIdKey = "externalId";
}
