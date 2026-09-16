using Microsoft.Extensions.Logging;

namespace Modbot.TestSupport;

/// <summary>
/// The same "ASP.NET Core and Entity Framework are quiet unless you asked for detail" rule the
/// real server applies (Modbot.Shared's <c>ModbotConsoleLog.Start</c>), for a test host that never
/// wires up Serilog at all and so never gets that rule for free.
/// </summary>
/// <remarks>
/// Without it, a passing suite's own console is nothing but a line for every SQL statement Entity
/// Framework runs and every request ASP.NET Core handles -- hundreds of thousands of them across a
/// suite this size -- and the one line that would explain a real failure is the one buried under
/// them. Nothing here touches Modbot's own loggers, so a genuine error or warning from application
/// code still prints.
/// </remarks>
public static class QuietTestLogging
{
    public static ILoggingBuilder QuietForTests(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

        return logging;
    }
}
