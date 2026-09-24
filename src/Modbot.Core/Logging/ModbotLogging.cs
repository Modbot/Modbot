using Modbot.Core.Logging.Store;
using Modbot.Core.Time;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Formatting.Compact;

namespace Modbot.Core.Logging;

/// <summary>
/// Options for <see cref="ModbotLogging"/>. Everything except <see cref="SeqUrl"/> lives in the
/// database; Seq is the one logging setting that must be available before the database is, because
/// it is how you debug a deployment that cannot reach its database.
/// </summary>
public sealed class ModbotLogOptions
{
    /// <summary>
    /// Where log files go when they are written at all. Also the directory probed for
    /// persistence, which is why it is a constant rather than a literal in two places.
    /// </summary>
    public const string DefaultDirectory = "logs";

    /// <summary>Directory for log files. Created if absent.</summary>
    public string Directory { get; init; } = DefaultDirectory;

    /// <summary>
    /// Write the Debug streams. Off by default: Debug records everything, so its text twin is the
    /// largest artefact Modbot produces.
    /// </summary>
    public bool Debug { get; init; }

    /// <summary>Seq endpoint, from the <c>SEQ_URL</c> environment variable. Null disables the sink.</summary>
    public string? SeqUrl { get; init; }

    /// <summary>
    /// The lowest level anything is written at, from <c>LOG_LEVEL</c>. Debug when
    /// <see cref="Debug"/> is on and <c>LOG_LEVEL</c> is unset.
    /// </summary>
    /// <remarks>
    /// It is also the floor on <see cref="DatabaseSink"/>, so the Logs page shows what was asked
    /// for rather than Information and above whatever was asked for. See <c>Create</c>.
    /// </remarks>
    public LogEventLevel Level { get; init; } = LogEventLevel.Information;

    /// <summary>
    /// The lowest level the console shows. Separate from <see cref="Level"/> so that
    /// <c>MODBOT_DEBUG_LOGGING</c> keeps filling the Debug files without filling the terminal;
    /// <c>LOG_LEVEL</c> sets both.
    /// </summary>
    public LogEventLevel ConsoleLevel { get; init; } = LogEventLevel.Information;

    /// <summary>Which of the three shapes the console is written in, from <c>CONSOLE_LOG_MODE</c>.</summary>
    public ConsoleLogMode ConsoleMode { get; init; } = ConsoleLogMode.Serilog;

    /// <summary>Size at which a stream rolls within a run.</summary>
    public long FileSizeLimitBytes { get; init; } = 64L * 1024 * 1024;

    public int RetainedMainFiles { get; init; } = 60;
    public int RetainedHttpFiles { get; init; } = 60;

    /// <summary>Deliberately short — Debug is unbounded by design.</summary>
    public int RetainedDebugFiles { get; init; } = 6;

    /// <summary>
    /// Write the file streams at all. False leaves the console and Seq sinks only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set from <see cref="Modbot.Core.Configuration.PersistenceProbe.ShouldPersist"/>, so that a
    /// deployment whose container filesystem is discarded on redeploy does not spend up to sixteen
    /// gigabytes of its own disk writing six log streams nobody can ever read. On those platforms
    /// the console <em>is</em> the log — Railway, Fly.io and Render all capture stdout — and the
    /// files are pure cost: they compete for the same disk the application needs, and the operator
    /// who goes looking for them after an incident finds an empty directory.
    /// </para>
    /// <para>
    /// This is a decision about the destination, never about the content. Every event is still
    /// emitted at the same level to console and to Seq; nothing is filtered out because the files
    /// are off.
    /// </para>
    /// </remarks>
    public bool WriteFiles { get; init; } = true;

    /// <summary>
    /// The sink that writes the log into Modbot's own database, so it can be read in the app. Null
    /// leaves it out entirely — a test host, or the build that generates the OpenAPI document.
    /// </summary>
    /// <remarks>
    /// Given here rather than created here because it outlives the logger's construction: it is
    /// built before the database is reachable, queues from the first line, and is connected by
    /// <c>DatabaseLogSink.Start</c> once the tables exist.
    /// </remarks>
    public Store.DatabaseLogSink? DatabaseSink { get; init; }
}

/// <summary>
/// Modbot's logging: one console profile and three file streams, each written as both JSONL and
/// plain text, plus an optional Seq sink.
/// </summary>
/// <remarks>
/// <para>See foundation spec section 4.4.1. The shape is:</para>
/// <code>
///   Console                              CONSOLE_LOG_MODE, Information
///   modbot_log_&lt;date&gt;_&lt;epoch&gt;.jsonl/.txt        application record, excludes Http
///   modbot_log_debug_&lt;date&gt;_&lt;epoch&gt;.jsonl/.txt  everything, opt-in
///   modbot_log_http_&lt;date&gt;_&lt;epoch&gt;.jsonl/.txt   API traffic only
///   Seq                                  optional, SEQ_URL
///   modbot_log (the Logs page)           LOG_LEVEL and above, excludes Http
/// </code>
/// <para>
/// Both formats of every stream, because they serve different readers. Text is what a self-hoster
/// opens when something breaks, at an hour when they will not install <c>jq</c>. JSONL is what
/// survives a real question — "every 429 on groups.members last week with the bucket state" is one
/// query against structured properties and unanswerable against a rendered sentence.
/// </para>
/// <para>
/// The filename carries the process-start Unix epoch, so every run gets its own files and a restart
/// is a file boundary rather than a seam mid-file. That is how a crash-loop becomes visible — the
/// failure mode the persisted rate-limit state in section 4.3.2 exists to guard against.
/// </para>
/// </remarks>
public static class ModbotLogging
{
    private const string TextTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}" +
        "{NewLine}{Exception}";

    /// <param name="clock">
    /// Even the log filename comes from <see cref="IModbotClock"/>. The stamp is how an operator
    /// correlates a log file with an incident, so it must agree with the timestamps on the facts.
    /// </param>
    public static Logger Create(ModbotLogOptions options, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        if (options.WriteFiles) System.IO.Directory.CreateDirectory(options.Directory);

        // MM-dd-yyyy for a human scanning the directory; epoch so each run is distinct and sortable.
        var now = clock.UtcNow;
        var stamp = $"{now:MM-dd-yyyy}_{now.ToUnixTimeSeconds()}";

        // Enrichment, the noisy-library levels and the three console shapes are the same in every
        // Modbot program; only the file streams below are the server's own. See ModbotConsoleLog.
        var config = ModbotConsoleLog
            .Start("Modbot", options.Level)
            .WriteTo.ModbotConsole(options.ConsoleMode, options.ConsoleLevel);

        if (options.WriteFiles)
            AddFileStreams(config, options, stamp);

        // ── Seq: absent config means an absent sink, never a broken logger or a stream of
        //    connection errors. It is also the only durable destination left when the files are
        //    off, which is why it survives that switch untouched.
        //
        //    The queue is held to the same ten thousand lines as the database sink. The sink's own
        //    default is a hundred thousand, and a held line measures 696 bytes, so a Seq that goes
        //    away takes 66 MB of memory with it until it comes back -- on a machine with two
        //    gigabytes and a database beside it, that is the Seq outage becoming an outage. Ten
        //    thousand is about seven megabytes, and it costs nothing that is not written down
        //    elsewhere: the same lines are already going to the files, to the console and to the
        //    database, so a line dropped here is a line that is still in three other places.
        if (!string.IsNullOrWhiteSpace(options.SeqUrl))
            config.WriteTo.Seq(
                options.SeqUrl,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                queueSizeLimit: DatabaseLogSink.QueueCapacity);

        // ── The database: the same events as the main stream, for the operator who has no Seq and
        //    no disk that survives a redeploy. The floor follows the level that was asked for,
        //    rather than being pinned at Information as it was until 2026-09-18.
        //
        //    Pinning it made sense while the Logs page was the only place the level could not
        //    reach: it kept a week of Debug out of a table nobody prunes by size. But the operator
        //    who has no Seq and no disk is exactly the operator who has nowhere else to read Debug,
        //    so "you may turn Debug on, but not where you can see it" was the wrong trade — the
        //    request lines and the query lines this switch is usually thrown for would have been
        //    written and then dropped at the door. What it costs is in LogStore: the daily prune
        //    now has a ceiling on rows as well as on days, because at Debug a busy day is worth a
        //    quiet month.
        if (options.DatabaseSink is { } database)
            config.WriteTo.Sink(database, restrictedToMinimumLevel: options.Level);

        return config.CreateLogger();
    }

    /// <summary>The three file streams, each written twice — JSONL and text.</summary>
    private static void AddFileStreams(LoggerConfiguration config, ModbotLogOptions options, string stamp)
    {
        // ── Main: the application record. Excludes Http so it stays readable for an operator
        //    diagnosing a sync problem rather than being buried under API traffic.
        config.WriteTo.Logger(main => main
            .Filter.ByExcluding(IsHttp)
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(options.Directory, $"modbot_log_{stamp}.jsonl"),
                restrictedToMinimumLevel: LogEventLevel.Information,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedMainFiles)
            .WriteTo.File(
                Path.Combine(options.Directory, $"modbot_log_{stamp}.txt"),
                outputTemplate: TextTemplate,
                restrictedToMinimumLevel: LogEventLevel.Information,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedMainFiles));

        // ── HTTP: nothing but outbound API traffic.
        config.WriteTo.Logger(http => http
            .Filter.ByIncludingOnly(IsHttp)
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(options.Directory, $"modbot_log_http_{stamp}.jsonl"),
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedHttpFiles)
            .WriteTo.File(
                Path.Combine(options.Directory, $"modbot_log_http_{stamp}.txt"),
                outputTemplate: TextTemplate,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedHttpFiles));

        // ── Debug: everything, including Http. Opt-in.
        if (options.Debug)
        {
            config.WriteTo.Logger(debug => debug
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    Path.Combine(options.Directory, $"modbot_log_debug_{stamp}.jsonl"),
                    restrictedToMinimumLevel: LogEventLevel.Debug,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: options.RetainedDebugFiles)
                .WriteTo.File(
                    Path.Combine(options.Directory, $"modbot_log_debug_{stamp}.txt"),
                    outputTemplate: TextTemplate,
                    restrictedToMinimumLevel: LogEventLevel.Debug,
                    fileSizeLimitBytes: options.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: options.RetainedDebugFiles));
        }

    }

    private static bool IsHttp(LogEvent e) =>
        e.Properties.TryGetValue(LogArea.Name, out var v)
        && v is ScalarValue { Value: string area }
        && area == LogArea.Http;
}
