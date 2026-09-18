using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Modbot.Cloud.Data;

/// <summary>
/// The Entity Framework events Cloud writes at a different level than Entity Framework chose.
/// </summary>
/// <remarks>
/// <para>
/// The twin of <c>Modbot.Core.Data.DatabaseLogLevels</c>, and deliberately a copy rather than a
/// reference: Cloud is built on Modbot.Shared alone, which is also what the companion is built on
/// and therefore carries no Entity Framework at all. Six lines of duplication is the price of that,
/// and the two lists should be changed together.
/// </para>
/// <para>
/// It matters here because Cloud shares the logger. <c>ModbotConsoleLog.Start</c> used to hold the
/// whole of <c>Microsoft.EntityFrameworkCore</c> at Warning, which hid Cloud's query lines as a
/// side effect; with that floor gone (2026-09-18) Cloud has to file them under Debug itself or
/// every statement it runs lands in its console at Information.
/// </para>
/// </remarks>
internal static class DatabaseLogLevels
{
    /// <summary>
    /// Files the line-per-statement under Debug, along with the two errors that are normal on a
    /// first boot: the migrations history table is missing until the migrations create it, and a
    /// database that is still starting refuses the first connections.
    /// </summary>
    public static DbContextOptionsBuilder LogQueriesAtDebug(this DbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.ConfigureWarnings(warnings => warnings
            .Log(
                (RelationalEventId.CommandExecuted, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug),
                (RelationalEventId.ConnectionError, LogLevel.Debug),
                (CoreEventId.ContextInitialized, LogLevel.Debug)));
    }
}
