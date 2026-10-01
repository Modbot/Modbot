namespace Modbot.Core.Data.Entities;

/// <summary>
/// The payload fields of a <see cref="FactType.InstanceClipSaved"/> fact: the saved file's
/// fingerprint, and nothing else about it.
/// </summary>
/// <remarks>
/// The clip never reaches the server with this fact; these two fields are how the server knows the
/// same file when a moderator later attaches it to a case file in a browser (clips design spec §16).
/// </remarks>
public static class ClipKeys
{
    /// <summary>The file's SHA-256, lowercase hex: the same name the evidence store would give it.</summary>
    public const string Hash = "clipHash";

    /// <summary>The file's size in bytes.</summary>
    public const string Bytes = "clipBytes";
}
