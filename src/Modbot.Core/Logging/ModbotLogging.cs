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
    /// <summary>Directory for log files. Created if absent.</summary>
    public string Directory { get; init; } = "logs";

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

    public static Logger Create(ModbotLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        System.IO.Directory.CreateDirectory(options.Directory);

        // MM-dd-yyyy for a human scanning the directory; epoch so each run is distinct and sortable.
        var now = DateTimeOffset.UtcNow;
        var stamp = $"{now:MM-dd-yyyy}_{now.ToUnixTimeSeconds()}";

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(options.Debug ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: ConsoleTemplate,
                restrictedToMinimumLevel: LogEventLevel.Information);

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

        // ── Seq: absent config means an absent sink, never a broken logger or a stream of
        //    connection errors.
        if (!string.IsNullOrWhiteSpace(options.SeqUrl))
            config.WriteTo.Seq(options.SeqUrl, restrictedToMinimumLevel: LogEventLevel.Debug);

        return config.CreateLogger();
    }

    private static bool IsHttp(LogEvent e) =>
        e.Properties.TryGetValue(LogArea.Name, out var v)
        && v is ScalarValue { Value: string area }
        && area == LogArea.Http;
}
