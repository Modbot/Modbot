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

    /// <summary>
    /// The Modbot account the reporting device was paired to. Written by the server from the
    /// pairing, never taken from the event: the clip is credited to whoever owns the device.
    /// </summary>
    public const string SavedByUserId = "savedByUserId";

    /// <summary>That account's username when the clip was reported, for "saved on …'s PC".</summary>
    public const string SavedByUsername = "savedByUsername";
}
