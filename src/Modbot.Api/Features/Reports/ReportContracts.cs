namespace Modbot.Api.Features.Reports;

/// <summary>A Discord account named on a report, as it was when the report was made.</summary>
/// <param name="DiscordId">Opaque. Never parsed or validated (foundation §3.1.1).</param>
/// <param name="Name">The name then; null when Modbot had none for a reported member.</param>
/// <param name="VRChatUserId">For the reported person: the VRChat account linked to them then, or null.</param>
public sealed record ReportPerson(string DiscordId, string? Name, string? VRChatUserId = null);

/// <summary>The message a report was made from. Absent for a report made with the slash command.</summary>
/// <param name="Attachments">The names of the files attached. Their links expired; only names were kept.</param>
/// <param name="Url">The message's own link, which opens it in Discord.</param>
public sealed record ReportMessageView(
    string? ChannelId,
    string? ChannelName,
    DateTimeOffset? SentAt,
    string? Text,
    IReadOnlyList<string> Attachments,
    string? Url);

/// <summary>One report, with the reporter: everyone who may see reports sees who reported.</summary>
/// <param name="State"><c>open</c> or <c>closed</c>.</param>
/// <param name="Text">What the reporter wrote. Null once retention removed it.</param>
/// <param name="Message">The quoted message, or null when there is none or retention removed it.</param>
/// <param name="TextRemovedAt">When retention removed the text and the message copy; null while they are kept.</param>
public sealed record MemberReportView(
    Guid Id,
    string State,
    DateTimeOffset CreatedAt,
    ReportPerson Reporter,
    ReportPerson About,
    string? Text,
    ReportMessageView? Message,
    DateTimeOffset? TextRemovedAt,
    DateTimeOffset? ClosedAt,
    string? ClosedByUsername,
    string? CloseNote);

/// <param name="OpenCount">Every open report this person may see, whatever tab the page is on.</param>
public sealed record MemberReportList(IReadOnlyList<MemberReportView> Reports, int OpenCount, DateTimeOffset Now);

public sealed record OpenReportCount(int Open);

/// <param name="Note">Required. What was done or concluded, in the closer's words.</param>
public sealed record CloseReportRequest(string Note);
