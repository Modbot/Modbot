using Modbot.Client.Ingest;
using Modbot.Client.Journal;
using Modbot.Client.Pairing;
using Modbot.Client.Pipeline;
using Modbot.Core.Time;

namespace Modbot.Client.Presentation;

/// <summary>One paired server, as the window shows it.</summary>
/// <param name="Detail">
/// A plain sentence for the moderator. Not a status code, not a stack trace: this window exists
/// for somebody deciding whether to keep trusting the program.
/// </param>
public sealed record ServerRow(
    string ServerId,
    string Address,
    string ManagedGroupId,
    ConnectionState State,
    bool IsPaused,
    int Pending,
    int AcceptedTotal,
    int DeduplicatedTotal,
    string Detail);

/// <summary>What the tray window is showing right now.</summary>
public sealed record ClientAppSnapshot(
    IReadOnlyList<ServerRow> Servers,
    IReadOnlyList<JournalEntry> Journal,
    LogHealthStatus LogStatus,
    string LogDetail,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Everything the window renders, with no Avalonia in it.
/// </summary>
/// <remarks>
/// <para>Separate from the window, and in the library rather than the executable, on purpose.
/// What the moderator is told about what this program does is the substance of the trust
/// argument, so the wording and the rules behind it are worth testing directly rather than only
/// through a rendered control -- and the window itself is Windows-only, while this is not.</para>
/// <para><strong>It reads nothing and sends nothing.</strong> It reports counters the engine
/// already keeps and lines the journal already holds. There is no path from this type to the
/// network or to the disk.</para>
/// </remarks>
public sealed class ClientAppState
{
    private readonly IModbotClock _clock;
    private readonly SentJournal _journal;

    /// <summary>
    /// How long without a recognised line before the log reader is called unhealthy.
    /// </summary>
    /// <remarks>
    /// A log parser that silently stops matching is the worst outcome available: presence history
    /// stops accruing, nobody notices for weeks, and the gap cannot be backfilled. Long enough not
    /// to fire while somebody sits alone in a quiet instance; short enough to catch a format
    /// change within one session.
    /// </remarks>
    public static readonly TimeSpan LogSilenceThreshold = TimeSpan.FromMinutes(10);

    public ClientAppState(IModbotClock clock, SentJournal journal)
    {
        _clock = clock;
        _journal = journal;
        Connections = [];
        UnusablePairings = [];
    }

    public List<ServerConnection> Connections { get; }

    /// <summary>
    /// Pairings that loaded but cannot be used — almost always a token encrypted for a different
    /// Windows account. Shown by name so the moderator knows which server to re-pair, rather than
    /// being told that something, somewhere, is wrong.
    /// </summary>
    public List<LoadedPairing> UnusablePairings { get; }

    public LogHealth LogHealth { get; set; } = LogHealth.Empty;

    public ClientAppSnapshot Snapshot()
    {
        var logStatus = LogHealth.Evaluate(_clock.UtcNow, LogSilenceThreshold);

        return new ClientAppSnapshot(
            [.. Connections.Select(Describe)],
            _journal.Recent(200),
            logStatus,
            DescribeLog(logStatus),
            [.. Warnings(logStatus)]);
    }

    private IEnumerable<string> Warnings(LogHealthStatus logStatus)
    {
        if (logStatus is LogHealthStatus.NotUnderstood)
        {
            // The two conditions share a symptom -- nothing is being reported -- and only the
            // second is a Modbot fault, so both are named rather than guessed between.
            yield return
                "VRChat is running and writing to its log, but Modbot has not recognised anything "
                + "in it recently. Either VRChat was started without its verbose logging flags, or "
                + "its log format has changed and this client needs updating.";
        }

        foreach (var unusable in UnusablePairings)
        {
            yield return unusable.Fault switch
            {
                PairingFault.TokenUndecryptable =>
                    $"The saved credential for “{unusable.ServerId}” cannot be decrypted on this "
                    + "Windows account. That is what happens when the settings file is copied "
                    + "between accounts or machines. Pair this server again.",
                _ => $"The saved settings for “{unusable.ServerId}” could not be read. Pair this server again.",
            };
        }

        foreach (var stopped in Connections.Where(c => c.State is ConnectionState.Stopped))
        {
            yield return
                $"“{stopped.ServerId}” rejected this device. Reporting to it has stopped and will "
                + "not restart on its own. If you were removed from that group's staff, this is "
                + "expected.";
        }
    }

    private string DescribeLog(LogHealthStatus status) => status switch
    {
        LogHealthStatus.Idle => "VRChat is not running. Nothing to read.",
        LogHealthStatus.Healthy =>
            $"Reading VRChat's log: {LogHealth.LinesRead:N0} lines seen, "
            + $"{LogHealth.BehaviourLines:N0} of them the kind Modbot looks at, "
            + $"{LogHealth.RecognisedEvents:N0} recognised.",
        _ => "VRChat is writing to its log and Modbot no longer recognises any of it.",
    };

    private static ServerRow Describe(ServerConnection connection) => new(
        connection.ServerId,
        connection.Pairing.BaseUri.ToString(),
        connection.ManagedGroupId,
        connection.State,
        connection.IsPaused,
        connection.Pending,
        connection.AcceptedTotal,
        connection.DeduplicatedTotal,
        Detail(connection));

    private static string Detail(ServerConnection connection) => connection.State switch
    {
        ConnectionState.Paused =>
            "Paused. Nothing about what you do is being captured or sent to this server. Pausing "
            + "stops Modbot reporting what you do from now on; it does not save it up to report "
            + "later. Anything queued before you paused is still owed to this server and goes out "
            + "when you resume.",
        ConnectionState.Stopped =>
            "Stopped. This server rejected the device token, so nothing more will be sent.",
        ConnectionState.NeedsRenegotiation =>
            "This server has been upgraded past what this client speaks. Update Modbot's client.",
        ConnectionState.Waiting =>
            connection.Pending == 0
                ? "Waiting to retry."
                : $"Waiting to retry. {connection.Pending:N0} observations are queued and none are lost.",
        _ => connection.Pending == 0
            ? "Up to date."
            : $"{connection.Pending:N0} observations queued to send.",
    };
}
