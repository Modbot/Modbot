using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Modbot.Core.Data;

/// <summary>
/// The five Entity Framework events Modbot writes at a different level than Entity Framework
/// chose, and the reason for each.
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework lets a level be set for one named event where the context is configured. That
/// is a much smaller instrument than a floor over the whole of <c>Microsoft.EntityFrameworkCore</c>
/// in the logger, which is what Modbot used to reach for: a floor cannot tell the line per
/// statement from the migration it is about to apply, so hiding one hid the other
/// (<c>ModbotConsoleLog.Start</c>, 2026-09-18).
/// </para>
/// <para>
/// Nothing here is silenced. Every one of these is written at Debug, so
/// <c>LOG_LEVEL=Debug</c> — or <c>MODBOT_DEBUG_LOGGING</c>, for the Debug files — brings all of it
/// back, which is the point: an operator who asks for detail should get the SQL.
/// </para>
/// </remarks>
public static class DatabaseLogLevels
{
    /// <summary>
    /// Files the line-per-statement and the two first-boot alarms under Debug.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RelationalEventId.CommandExecuted"/> is one Information line for every statement
    /// Modbot runs. A sync runs thousands, and the stream the foundation spec calls "the
    /// application record" is then SQL with a sync buried in it.
    /// </para>
    /// <para>
    /// <see cref="RelationalEventId.CommandError"/> and
    /// <see cref="RelationalEventId.ConnectionError"/> are Errors, and on a first boot both are
    /// normal: the <c>__EFMigrationsHistory</c> table is missing until the migrations create it,
    /// and a database that is still starting refuses the first connections. A successful first
    /// deploy should not open with a red line, and a retry that is about to succeed should not look
    /// like a failure. Whatever actually goes wrong is reported a line later in Modbot's own words,
    /// by <c>DatabaseMigrator</c>.
    /// </para>
    /// <para>
    /// <see cref="CoreEventId.ContextInitialized"/> is "Entity Framework Core initialized…", once
    /// for every context created — which, with a context per request, is a line per request saying
    /// nothing. It only stopped being invisible when the blanket floor came off.
    /// </para>
    /// <para>
    /// <see cref="CoreEventId.SaveChangesFailed"/> is an Error for every save that throws,
    /// including the ones Modbot throws on purpose. Five places in the app claim a row by writing
    /// it and letting the unique index decide — that is how two clicks arriving at once are told
    /// apart without a read-then-write — so the second click is an ordinary outcome that EF
    /// narrates as a failure. Like the two above it, a save that really did fail is reported a
    /// line later by whoever was waiting on it, in Modbot's own words; this line is the same news
    /// in Entity Framework's, before anyone has decided whether it was news at all.
    /// </para>
    /// </remarks>
    public static DbContextOptionsBuilder LogQueriesAtDebug(this DbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.ConfigureWarnings(warnings => warnings
            .Log(
                (RelationalEventId.CommandExecuted, LogLevel.Debug),
                (RelationalEventId.CommandError, LogLevel.Debug),
                (RelationalEventId.ConnectionError, LogLevel.Debug),
                (CoreEventId.ContextInitialized, LogLevel.Debug),
                (CoreEventId.SaveChangesFailed, LogLevel.Debug)));
    }
}
