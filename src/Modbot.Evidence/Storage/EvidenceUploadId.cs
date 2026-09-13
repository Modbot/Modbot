namespace Modbot.Evidence.Storage;

/// <summary>
/// Identifies one upload attempt, and therefore one staging key.
/// </summary>
/// <remarks>
/// <para>
/// Design section 9.1 derives the staging key from the upload id, which quietly makes the upload id
/// part of a path — so it gets the same treatment as <see cref="EvidenceHash"/>. Thirty-two
/// lowercase hex characters, generated from a GUID, and nothing else parses. A store never sees a
/// string a caller chose.
/// </para>
/// <para>
/// The key is stable across retries on purpose: a retry of the same upload overwrites its staging
/// object rather than accumulating a new one (design section 9.1), which is what keeps a moderator
/// on a bad connection from leaving five copies of a video behind.
/// </para>
/// </remarks>
public readonly record struct EvidenceUploadId
{
    private readonly string? _value;

    private EvidenceUploadId(string value) => _value = value;

    /// <summary>The canonical form: 32 lowercase hex characters.</summary>
    public string Value => _value ?? throw new InvalidOperationException("Uninitialised upload id.");

    public static EvidenceUploadId New() => new(Guid.NewGuid().ToString("N"));

    public static EvidenceUploadId FromGuid(Guid id) => new(id.ToString("N"));

    public static EvidenceUploadId Parse(string value)
        => TryParse(value, out var id)
            ? id
            : throw new FormatException("An upload id is 32 lowercase hex characters.");

    public static bool TryParse(string? value, out EvidenceUploadId id)
    {
        id = default;
        if (value is null || value.Length != 32)
            return false;

        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        id = new EvidenceUploadId(value);
        return true;
    }

    public override string ToString() => _value ?? "(none)";
}
