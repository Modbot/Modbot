using Modbot.Client.CloudBackup;
using Modbot.Client.Ingest;
using Modbot.Client.Journal;
using Modbot.Client.Pairing;
using Modbot.Client.Pipeline;
using Modbot.Client.Startup;
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

/// <summary>How loudly a warning should be shown.</summary>
public enum WarningSeverity
{
    /// <summary>Worth knowing. Nothing is broken.</summary>
    Info,

    /// <summary>Something needs attention before it costs history.</summary>
    Warning,

    /// <summary>
    /// Presence is not being recorded and will not start again on its own. This is the one worth
    /// interrupting somebody for: the gap it creates cannot be filled in later.
    /// </summary>
    Critical,
}

public sealed record ClientWarning(WarningSeverity Severity, string Message);

/// <summary>Where the most recent pairing attempt got to.</summary>
public enum PairingNoticeKind
{
    /// <summary>A request is in flight. One, never more.</summary>
    Working,

    Succeeded,

    Failed,
}

/// <summary>
/// The last thing pairing had to say, whichever way the token arrived — pressed in a browser or
/// pasted into the window.
/// </summary>
/// <remarks>
/// Kept here rather than inside the window so a link that Windows hands to the running client
/// while the window is closed has somewhere to put its answer, and so the window can be rebuilt
/// on a timer without losing it.
/// </remarks>
public sealed record PairingNotice(PairingNoticeKind Kind, string Message);

/// <summary>What the client window is showing right now.</summary>
/// <param name="PairingPage">Where "Pair with a server" sends the browser. Shown so nobody has to guess.</param>
public sealed record ClientAppSnapshot(
    IReadOnlyList<ServerRow> Servers,
    IReadOnlyList<JournalEntry> Journal,
    LogHealthStatus LogStatus,
    string LogDetail,
    long LinesRead,
    long BehaviourLines,
    long RecognisedEvents,
    IReadOnlyList<ClientWarning> Warnings,
    PairingNotice? LastPairing,
    string PairingPage,
    CloudBackupStatus? CloudBackup = null,
    StartupState? Startup = null)
{
    public static ClientAppSnapshot Empty { get; } =
        new([], [], LogHealthStatus.Idle, "Starting up.", 0, 0, 0, [], null, ClientSettings.DefaultPairingPage);
}

/// <summary>
/// Everything the window renders, with no Avalonia in it.
/// </summary>
/// <remarks>
/// <para>Separate from the window, and in the library rather than the executable, on purpose.
/// What the moderator is told about what this program does is the substance of the trust argument,
/// so the wording and the rules behind it are worth testing directly rather than only through a
/// rendered control — and the window itself is Windows-only, while this is not.</para>
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
    /// stops accruing, nobody notices for weeks, and the gap cannot be filled in later. Long enough not
    /// to fire while somebody sits alone in a quiet instance; short enough to catch a format
    /// change within one session.
    /// </remarks>
    public static readonly TimeSpan LogSilenceThreshold = TimeSpan.FromMinutes(10);

    public ClientAppState(IModbotClock clock, SentJournal journal, ClientSettings? settings = null)
    {
        _clock = clock;
        _journal = journal;
        Settings = settings ?? ClientSettings.Default;
        Connections = [];
        UnusablePairings = [];
    }

    public ClientSettings Settings { get; set; }

    /// <summary>The event backup to Modbot Cloud, once the host has made it. Its status is shown on the settings page.</summary>
    public CloudEventBackup? CloudBackup { get; set; }

    /// <summary>How the start-with-Windows switch should look; hidden unless this copy is installed.</summary>
    public StartupState Startup { get; set; } = StartupState.Hidden;

    public List<ServerConnection> Connections { get; }

    /// <summary>The last pairing attempt's outcome, or null when there has not been one this run.</summary>
    public PairingNotice? LastPairing { get; set; }

    /// <summary>
    /// Pairings that loaded but cannot be used — almost always a token encrypted for a different
    /// Windows account. Shown by name so the moderator knows which server to re-pair, rather than
    /// being told that something, somewhere, is wrong.
    /// </summary>
    public List<LoadedPairing> UnusablePairings { get; }

    public LogHealth LogHealth { get; set; } = LogHealth.Empty;

    /// <summary>
    /// Why the last attempt to read VRChat's log failed, in one line, or null while reading works.
    /// The full story is in the client's own log file; this is the pointer to it.
    /// </summary>
    public string? ReadingFault { get; set; }

    /// <summary>
    /// The version of Modbot that has been downloaded and is waiting to be installed the next
    /// time the client starts, or null when there is none. Set by the updater; shown as a line
    /// in the window, never as a restart.
    /// </summary>
    public string? UpdateReady { get; set; }

    public ClientAppSnapshot Snapshot()
    {
        var logStatus = LogHealth.Evaluate(_clock.UtcNow, LogSilenceThreshold);

        return new ClientAppSnapshot(
            [.. Connections.Select(Describe)],
            _journal.Recent(200),
            logStatus,
            DescribeLog(logStatus),
            LogHealth.LinesRead,
            LogHealth.BehaviourLines,
            LogHealth.RecognisedEvents,
            [.. Warnings(logStatus)],
            LastPairing,
            Settings.PairingPage.ToString(),
            CloudBackup?.Status,
            Startup);
    }

    private IEnumerable<ClientWarning> Warnings(LogHealthStatus logStatus)
    {
        if (ReadingFault is { } fault)
        {
            yield return new ClientWarning(
                WarningSeverity.Critical,
                $"Reading VRChat's log failed: {fault}. Nothing is being recorded until this is fixed. "
                + "The details are in the client's log file under %APPDATA%\\Modbot\\logs.");
        }

        if (logStatus is LogHealthStatus.NotUnderstood)
        {
            // Critical, because it is the silent-failure case: nothing errors, presence simply
            // stops accruing, and the history lost while nobody noticed cannot be recovered.
            yield return new ClientWarning(
                WarningSeverity.Critical,
                "VRChat is running and writing to its log, but Modbot has not recognised anything "
                + "in it recently. Either VRChat was started without its verbose logging flags, or "
                + "its log format has changed and this client needs updating. Nothing is being "
                + "recorded until this is fixed.");
        }

        foreach (var unusable in UnusablePairings)
        {
            yield return new ClientWarning(
                WarningSeverity.Critical,
                unusable.Fault switch
                {
                    PairingFault.TokenUndecryptable =>
                        $"The saved credential for “{unusable.ServerId}” cannot be decrypted on this "
                        + "Windows account. That is what happens when the settings file is copied "
                        + "between accounts or machines. Pair this server again.",
                    _ => $"The saved settings for “{unusable.ServerId}” could not be read. Pair this server again.",
                });
        }

        foreach (var stopped in Connections.Where(c => c.State is ConnectionState.Stopped))
        {
            yield return new ClientWarning(
                WarningSeverity.Critical,
                $"“{stopped.ServerId}” rejected this device. Reporting to it has stopped and will "
                + "not restart on its own. If you were removed from that group's staff, this is "
                + "expected and there is nothing to fix.");
        }

        foreach (var stale in Connections.Where(c => c.State is ConnectionState.NeedsRenegotiation))
        {
            yield return new ClientWarning(
                WarningSeverity.Warning,
                $"“{stale.ServerId}” has been upgraded past what this client can speak to. Update "
                + "Modbot's client; observations are still being queued in the meantime.");
        }

        // Counted rather than silent. "We quietly lost some" is precisely the failure this
        // subsystem must not have, and the buffer forgets on purpose when it fills or ages out.
        foreach (var connection in Connections.Where(c => c.MalformedBatches > 0))
        {
            yield return new ClientWarning(
                WarningSeverity.Warning,
                $"“{connection.ServerId}” refused {connection.MalformedBatches:N0} batch(es) as "
                + "malformed and they were dropped rather than retried. That is a bug worth "
                + "reporting.");
        }

        // Informational, and it stays informational: an update is never installed under a
        // running client, so the moderator chooses the moment by quitting and reopening.
        if (UpdateReady is { } update)
        {
            yield return new ClientWarning(
                WarningSeverity.Info,
                $"Modbot {update} has been downloaded and will be installed the next time Modbot "
                + "starts. Quit from the tray icon and open Modbot again whenever suits you; "
                + "nothing changes while it is running, and your pairings are kept.");
        }
    }

    private string DescribeLog(LogHealthStatus status) => status switch
    {
        LogHealthStatus.Idle => "VRChat is not running, so there is nothing to read.",
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

    /// <summary>
    /// The sentence under each server.
    /// </summary>
    /// <remarks>
    /// The distinction this exists to make legible is "buffering, will send later" against
    /// "stopped, will never send". On a screen that is not moving those look identical and they
    /// mean opposite things: one needs nothing doing, and the other means this moderator's
    /// coverage has quietly ended.
    /// </remarks>
    private static string Detail(ServerConnection connection) => connection.State switch
    {
        ConnectionState.Paused =>
            "Paused. Nothing about what you do is being captured or sent to this server. Pausing "
            + "stops Modbot reporting what you do from now on; it does not save it up to report "
            + "later. Anything queued before you paused is still owed to this server and goes out "
            + "when you resume.",

        ConnectionState.Stopped =>
            "Stopped. This server rejected the device token, so nothing more will be sent and "
            + "nothing is being queued for it.",

        ConnectionState.NeedsRenegotiation =>
            "This server has been upgraded past what this client speaks. Observations are still "
            + "being queued, and will be sent once the client is updated.",

        ConnectionState.Waiting =>
            connection.Pending == 0
                ? "Could not reach this server on the last attempt. Waiting to retry."
                : $"Could not reach this server on the last attempt. Waiting to retry; "
                  + $"{connection.Pending:N0} observations are queued and none are lost.",

        _ => connection.Pending == 0
            ? "Up to date. Everything observed for this group has been reported."
            : $"{connection.Pending:N0} observations queued to send.",
    };
}
