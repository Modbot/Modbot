namespace Modbot.Api.Features.Logs;

/// <summary>One stored log line.</summary>
/// <param name="Id">Row id. Also the paging cursor: ask for lines <c>before</c> this one.</param>
/// <param name="At">When it was written.</param>
/// <param name="Level"><c>Verbose</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c> or <c>Fatal</c>.</param>
/// <param name="Message">The line with its values filled in.</param>
/// <param name="Template">The line before the values were filled in.</param>
/// <param name="Source">The class that wrote it.</param>
/// <param name="Area">Which stream it belongs to: <c>Sync</c>, <c>Moderation</c>, <c>Analytics</c>, <c>Setup</c>, <c>Discord</c>.</param>
/// <param name="Exception">The exception, when there was one.</param>
/// <param name="Properties">Everything else the line carried, as a JSON object.</param>
public sealed record LogLine(
    long Id,
    DateTimeOffset At,
    string Level,
    string Message,
    string? Template,
    string? Source,
    string? Area,
    string? Exception,
    string Properties);

/// <param name="Lines">Newest first.</param>
/// <param name="Next">The id to pass as <c>before</c> for the next page. Null at the end.</param>
/// <param name="Now">The server's clock, so ages are measured against it and not the browser's.</param>
public sealed record LogPage(IReadOnlyList<LogLine> Lines, long? Next, DateTimeOffset Now);

/// <summary>What the filters on the Logs page can offer.</summary>
/// <param name="Levels">Every level, lowest first.</param>
/// <param name="Sources">Sources that have written a line, in alphabetical order.</param>
/// <param name="Areas">Areas that have written a line.</param>
/// <param name="Stored">Lines in the table.</param>
/// <param name="Oldest">The oldest line still kept. Null when there are none.</param>
public sealed record LogFilters(
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Areas,
    long Stored,
    DateTimeOffset? Oldest);

/// <param name="KeepDays">How long lines are kept. 0 keeps them forever.</param>
/// <param name="SendToCloud">Send the same lines to Modbot Cloud.</param>
/// <param name="CloudAllowed">False when <c>MODBOT_CLOUD_DISABLED</c> is set: sending is off whatever the switch says.</param>
public sealed record LogSettings(int KeepDays, bool SendToCloud, bool CloudAllowed);

/// <param name="KeepDays">How long lines are kept. 0 keeps them forever.</param>
/// <param name="SendToCloud">Send the same lines to Modbot Cloud.</param>
public sealed record LogSettingsUpdate(int KeepDays, bool SendToCloud);
