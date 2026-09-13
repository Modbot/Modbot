namespace Modbot.Evidence.Options;

/// <summary>Which of the three stores (design section 4) holds the bytes.</summary>
/// <remarks>
/// Numbered explicitly because the value is recorded per object in the blob projection
/// (design section 7.1, <c>backend smallint</c>) so that a deployment which has changed backends
/// still knows where each object was written. Renumbering one would silently repoint history.
/// </remarks>
public enum EvidenceBackend
{
    /// <summary>Nothing configured yet. Uploads are refused; nothing else is affected.</summary>
    None = 0,

    /// <summary>S3-compatible object storage. The recommendation (design section 4.1).</summary>
    S3 = 1,

    /// <summary>A directory under the data root. Needs a mounted volume (design section 4.2).</summary>
    Filesystem = 2,

    /// <summary>Chunked rows in PostgreSQL. Supported, not recommended (design section 4.3).</summary>
    Database = 3,
}
