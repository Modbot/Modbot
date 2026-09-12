using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Modbot.Explore;

/// <summary>
/// The logging setup Modbot itself will use, prototyped here first.
/// </summary>
/// <remarks>
/// Four tracks, per the foundation spec:
///
///   1. Console      -- human-readable text, for someone watching a terminal.
///   2. JSONL file   -- one compact JSON object per line, for machines. This is the track that
///                      makes logs queryable without a log server: jq, grep, or a later import.
///   3. Text file    -- the same content as the console, retained on disk.
///   4. Seq          -- optional, opt-in, only when SEQ_URL is set.
///
/// Tracks 1 and 3 are the same rendering; 2 is the one that matters for diagnosing anything
/// non-trivial, because structured properties survive instead of being flattened into a sentence.
/// </remarks>
public static class Logging
{
    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static ILogger Create(string? seqUrl, string outputDirectory = "logs")
    {
        Directory.CreateDirectory(outputDirectory);

        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()

            // 1. Console -- text, for a human.
            .WriteTo.Console(outputTemplate: ConsoleTemplate)

            // 2. JSONL -- one JSON object per line. Structured properties are preserved, so this
            //    is what you actually query. CompactJsonFormatter keeps lines small.
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(outputDirectory, "explore-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)

            // 3. Text file -- same rendering as the console, kept on disk.
            .WriteTo.File(
                Path.Combine(outputDirectory, "explore-.log"),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14);

        // 4. Seq -- optional. Absent config means absent sink, not a broken logger.
        if (!string.IsNullOrWhiteSpace(seqUrl))
            config = config.WriteTo.Seq(seqUrl, restrictedToMinimumLevel: LogEventLevel.Debug);

        return config.CreateLogger();
    }
}
