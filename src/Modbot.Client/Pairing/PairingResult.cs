using Modbot.Client.Ingest;

namespace Modbot.Client.Pairing;

/// <summary>
/// How a pairing attempt ended, reduced to the cases the moderator is told apart.
/// </summary>
/// <remarks>
/// Every one of these is shown, because pairing is the moment a moderator decides whether to
/// trust this program at all. A pairing that fails silently and retries in the background is
/// exactly the behaviour the client promises not to have.
/// </remarks>
public enum PairingOutcome
{
    /// <summary>A device token came back and has been stored.</summary>
    Paired,

    /// <summary>
    /// The code was wrong, already used, or expired. Short-lived and single-use is the point of
    /// it: a code that lived long enough to be reusable would end up pasted into Discord.
    /// </summary>
    CodeRejected,

    /// <summary>
    /// <c>401</c>. Terminal — the operator revoked this moderator while the request was in
    /// flight, or the code belonged to somebody whose account is gone. Surfaced, never retried.
    /// </summary>
    Unauthorised,

    /// <summary>
    /// No API version this client speaks overlaps what the server speaks. Both numbers are named
    /// when this is shown: a version mismatch that presents as a parse error or as ingest quietly
    /// stopping is indistinguishable from the log parser having broken, and is just as
    /// unrecoverable.
    /// </summary>
    VersionUnsupported,

    /// <summary>The address is not a Modbot server, or not one this client understood.</summary>
    NotAModbotServer,

    /// <summary>No answer at all. The moderator may simply be offline; retrying is their choice.</summary>
    NetworkFailure,
}

/// <param name="Outcome">What happened. Everything else is null unless this is <see cref="PairingOutcome.Paired"/>.</param>
/// <param name="Pairing">The new pairing, ready to be stored.</param>
/// <param name="ServerTime">
/// The instant the server reported while answering, which seeds the clock offset so the very first
/// report is already corrected rather than being wrong until the first separate time probe.
/// </param>
/// <param name="Detail">
/// A human-readable line for the moderator. Never branched on — the client branches on
/// <see cref="Outcome"/>, because message text is for people and gets reworded.
/// </param>
public sealed record PairingResult(
    PairingOutcome Outcome,
    ServerPairing? Pairing = null,
    DateTimeOffset? ServerTime = null,
    string? Detail = null)
{
    public static PairingResult Failed(PairingOutcome outcome, string detail) => new(outcome, Detail: detail);
}
