using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Modbot.Core.Logging;

/// <summary>
/// The whole logger for a Modbot web service that keeps no log files of its own: my.modbot.co,
/// Modbot Cloud and the landing page.
/// </summary>
/// <remarks>
/// <para>
/// All three run on a platform that captures standard output, so the console is the log. Files
/// would be written to a disk that is discarded on the next deploy, and the one durable copy worth
/// having is a Seq server, which <c>SEQ_URL</c> switches on and which costs nothing when it is
/// unset.
/// </para>
/// <para>
/// The server has its own builder, <c>ModbotLogging.Create</c> in Modbot.Core, because it writes
/// six log files as well. Both start from <see cref="ModbotConsoleLog.Start"/>, so an event looks
/// the same whichever program wrote it.
/// </para>
/// </remarks>
public static class ModbotServiceLog
{
    /// <summary>A Seq server to send a durable copy to. Unset means no Seq sink at all.</summary>
    public const string SeqUrlVariable = "SEQ_URL";

    /// <summary>Information: enough to follow what a service did, without a line per query.</summary>
    public const LogEventLevel DefaultLevel = LogEventLevel.Information;

    /// <param name="service">The name of the program, written onto every event.</param>
    /// <param name="get">Where the environment variables come from. Defaults to the process.</param>
    public static Logger Create(string service, Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;

        var level = ModbotConsoleLog.ReadLevel(DefaultLevel, get);
        var mode = ModbotConsoleLog.ReadMode(get);

        var config = ModbotConsoleLog
            .Start(service, level)
            .WriteTo.ModbotConsole(mode, level);

        var seqUrl = get(SeqUrlVariable);
        if (!string.IsNullOrWhiteSpace(seqUrl))
            config.WriteTo.Seq(seqUrl.Trim(), restrictedToMinimumLevel: LogEventLevel.Debug);

        return config.CreateLogger();
    }
}
