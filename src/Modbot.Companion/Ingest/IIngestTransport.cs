namespace Modbot.Companion.Ingest;

/// <summary>
/// What the server said, reduced to the handful of cases the client behaves differently about.
/// </summary>
/// <remarks>
/// The client branches on these, which come from the error body's stable machine-readable
/// <c>code</c> and the status line — never on the human-readable message, which is for people and
/// will be reworded. Protocol section 7.
/// </remarks>
public enum IngestOutcome
{
    /// <summary>
    /// 200. Possibly partially: a batch where eleven of forty-eight events had already been
    /// reported by another moderator is a completely successful request, not a failure.
    /// </summary>
    Accepted,

    /// <summary>400. The batch is wrong and will be wrong every time. Do not retry.</summary>
    Malformed,

    /// <summary>401. Token revoked or invalid. Terminal for this pairing.</summary>
    Unauthorised,

    /// <summary>409. The server moved past this client's API range. Renegotiate.</summary>
    VersionUnsupported,

    /// <summary>413. Send less at a time.</summary>
    TooLarge,

    /// <summary>429. The server asked for a pause and said how long.</summary>
    RateLimited,

    /// <summary>5xx. Keep buffering and come back.</summary>
    ServerTrouble,

    /// <summary>No answer at all: no network, DNS gone, connection dropped mid-request.</summary>
    NetworkFailure,
}

/// <param name="Accepted">How many events the server took.</param>
/// <param name="Deduplicated">
/// How many it already had from another moderator. Expected, not a problem: four to six clients in
/// one instance all observe the same join and all report it.
/// </param>
/// <param name="RetryAfter">Honoured when the server sends it. Modbot does, even though VRChat does not.</param>
public sealed record IngestResult(
    IngestOutcome Outcome,
    int Accepted = 0,
    int Deduplicated = 0,
    int Rejected = 0,
    TimeSpan? RetryAfter = null,
    string? Code = null);

/// <summary>
/// The one thing in the client that makes an outbound request carrying observations.
/// </summary>
/// <remarks>
/// An interface so the ingest logic can be tested against every failure the network has, and so
/// that a reader looking for "what does this program send to a server, and where" has exactly one
/// place to look. The only other path is the event backup to Modbot Cloud (<c>ICloudLogClient</c>): no
/// telemetry, no crash reporter carrying event data.
/// </remarks>
public interface IIngestTransport
{
    Task<IngestResult> SendAsync(ServerPairing pairing, EventBatch batch, CancellationToken cancellationToken);
}
