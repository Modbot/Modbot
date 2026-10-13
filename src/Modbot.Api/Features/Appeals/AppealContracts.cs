namespace Modbot.Api.Features.Appeals;

/// <summary>What staff write down when an appeal reaches the team.</summary>
/// <param name="UserId">
/// The VRChat account that appealed. Opaque: taken as sent, never parsed and never checked for shape
/// (foundation §3.1.1). Required.
/// </param>
/// <param name="ReceivedOn">The day it reached the team. Defaults to today; never a day to come.</param>
/// <param name="CaseFileId">The case file of the ban being appealed. Optional; it must be about the same person.</param>
/// <param name="Note">A line or two, stored and shown as text. Optional.</param>
public sealed record LogAppealRequest(string? UserId, DateOnly? ReceivedOn = null, Guid? CaseFileId = null, string? Note = null);

/// <summary>How an appeal ended: <c>upheld</c> or <c>turned-down</c>.</summary>
public sealed record DecideAppealRequest(string? Outcome);

/// <summary>One appeal, as it is read back.</summary>
/// <param name="Outcome"><c>pending</c>, <c>upheld</c> or <c>turned-down</c>.</param>
/// <param name="CaseFileId">The linked ban's case file, when staff linked one.</param>
/// <param name="CaseFileDate">When that ban happened (else when its case file was written).</param>
/// <param name="CanDecide">Whether the person reading may uphold or turn it down: it waits, and they may lift bans.</param>
public sealed record AppealView(
    Guid Id,
    string UserId,
    string? DisplayName,
    DateOnly ReceivedOn,
    string Outcome,
    Guid? CaseFileId,
    DateTimeOffset? CaseFileDate,
    string Note,
    DateTimeOffset LoggedAt,
    string LoggedByName,
    DateTimeOffset? DecidedAt,
    string? DecidedByName,
    bool CanDecide);

/// <param name="Appeals">Newest received first, at most the limit asked for.</param>
/// <param name="Total">How many match, before the limit.</param>
/// <param name="Waiting">How many of those match and still wait for an answer.</param>
/// <param name="CanLog">Whether the person reading may log and decide appeals.</param>
public sealed record AppealList(
    IReadOnlyList<AppealView> Appeals,
    int Total,
    int Waiting,
    bool CanLog,
    DateTimeOffset Now);

/// <summary>One number on the yearly report.</summary>
/// <param name="Key">A stable name for the number: <c>warns</c>, <c>appeals-received</c> and so on.</param>
public sealed record ReportLine(string Key, string Label, long Count);

/// <param name="Year">The calendar year, in UTC days as every analytics page counts them.</param>
/// <param name="Lines">Warnings, kicks, bans and unbans from the daily totals, then the appeals.</param>
/// <param name="Years">The years worth picking, newest first.</param>
/// <param name="CountedSince">
/// The first day Modbot has anything counted for these numbers, or null when it has none. A year that
/// starts before it is a partial year.
/// </param>
public sealed record YearlyReport(
    int Year,
    IReadOnlyList<ReportLine> Lines,
    IReadOnlyList<int> Years,
    DateOnly? CountedSince,
    DateTimeOffset Now);
