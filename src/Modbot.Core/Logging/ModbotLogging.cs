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
}

/// <summary>
/// Modbot's logging: one console profile and three file streams, each written as both JSONL and
/// plain text, plus an optional Seq sink.
/// </summary>
/// <remarks>
/// <para>See foundation spec section 4.4.1. The shape is:</para>
/// <code>
///   Console                              rendered text, Information
///   modbot_log_&lt;date&gt;_&lt;epoch&gt;.jsonl/.txt        application record, excludes Http
///   modbot_log_debug_&lt;date&gt;_&lt;epoch&gt;.jsonl/.txt  everything, opt-in
///   modbot_log_http_&lt;date&gt;_&lt;epoch&gt;.jsonl/.txt   API traffic only
///   Seq                                  optional, SEQ_URL
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

    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

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

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(options.Debug ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: ConsoleTemplate,
                restrictedToMinimumLevel: LogEventLevel.Information);

        if (options.WriteFiles)
            AddFileStreams(config, options, stamp);

        // ── Seq: absent config means an absent sink, never a broken logger or a stream of
        //    connection errors. It is also the only durable destination left when the files are
        //    off, which is why it survives that switch untouched.
        if (!string.IsNullOrWhiteSpace(options.SeqUrl))
            config.WriteTo.Seq(options.SeqUrl, restrictedToMinimumLevel: LogEventLevel.Debug);

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
