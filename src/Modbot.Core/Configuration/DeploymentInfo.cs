namespace Modbot.Core.Configuration;

/// <summary>
/// What Modbot worked out about its own surroundings at startup, so the settings page can show it
/// rather than leaving the operator to infer it.
/// </summary>
/// <param name="Platform">The detected host.</param>
/// <param name="Persistence">What the marker probe found for the log directory.</param>
/// <param name="LogFilesWritten">
/// Whether the file sinks were actually registered. The decision is made once, at startup, and
/// this is the record of it — otherwise the settings page would have to re-derive it and could
/// disagree with what the process is really doing.
/// </param>
/// <param name="PersistenceExplanation">The probe's own sentence, shown verbatim.</param>
/// <param name="PublicAddressSuggestion">
/// The public address the platform says this service has, if it says so (Railway's
/// <c>RAILWAY_PUBLIC_DOMAIN</c>). A suggestion for the operator to confirm, never used on its own:
/// links are built only from the address a person saved (<see cref="PublicAddress"/>).
/// </param>
/// <remarks>
/// Registered as a singleton because it describes the process, not the request. It is deliberately
/// a snapshot: re-probing per request would let the answer drift from the configuration the logger
/// was actually built with.
/// </remarks>
public sealed record DeploymentInfo(
    HostPlatform Platform,
    PersistenceEvidence Persistence,
    bool LogFilesWritten,
    string PersistenceExplanation,
    string? PublicAddressSuggestion = null);
