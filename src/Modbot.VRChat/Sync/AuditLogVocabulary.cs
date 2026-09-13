using Modbot.Core.Time;
using Serilog;
using VRChat.API.Client;

namespace Modbot.VRChat.Sync;

/// <param name="CheckedAt">When VRChat was asked.</param>
/// <param name="Declared">Every event type VRChat says this group's audit log can contain.</param>
/// <param name="Unmapped">
/// Declared by VRChat, not mapped by Modbot. These are events happening in the group that Modbot
/// is choosing not to record — a gap, but a known one.
/// </param>
/// <param name="MissingPrimary">
/// <strong>The finding that matters.</strong> A spelling Modbot treats as real that VRChat does
/// not declare. Almost always means the constant is wrong, and that whatever it maps to is being
/// silently lost.
/// </param>
/// <param name="UnusedAliases">
/// Speculative spellings VRChat does not declare. Entirely expected — they are insurance — and
/// listed only so the other two lists are not read as containing them.
/// </param>
public sealed record AuditLogVocabularyReport(
    DateTimeOffset CheckedAt,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Unmapped,
    IReadOnlyList<string> MissingPrimary,
    IReadOnlyList<string> UnusedAliases)
{
    /// <summary>True when a mapping Modbot depends on is not in VRChat's own list.</summary>
    public bool HasProblem => MissingPrimary.Count > 0;
}

/// <summary>
/// Asks VRChat what its audit log can actually contain, and checks Modbot's mapping table against
/// the answer.
/// </summary>
/// <remarks>
/// <para>
/// <c>GroupAuditLogEvents</c> is a table of observed strings, because VRChat's OpenAPI schema types
/// <c>eventType</c> as a bare <c>string</c> with a single example — there is no enum to compile
/// against. Until now the only way to discover a wrong spelling was to wait for a real event to
/// arrive and be reported as unmapped, which for a rare event type could be months, and which for
/// a <em>misspelled</em> type would never happen at all: Modbot would sit waiting for
/// <c>group.member.user.ban</c> while VRChat emitted something else, and the fact log would look
/// healthy while missing every ban.
/// </para>
/// <para>
/// This closes that. VRChat publishes the vocabulary per group, so the table stops being guesswork
/// and becomes something checkable on a live deployment.
/// </para>
/// <para>
/// <strong>It must never interfere with the sync it checks.</strong> It runs on its own endpoint
/// class and its own lane (spec 4.3.4.1), so a 429 here cold-stops vocabulary discovery and
/// nothing else. Any failure is reported and discarded — a diagnostic that cannot run is an
/// inconvenience, and a moderation history that stops arriving is not.
/// </para>
/// </remarks>
public sealed class AuditLogVocabulary(IVRChatGate gate, IModbotClock clock, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? Log.Logger;

    /// <summary>
    /// Rechecked about daily. VRChat's vocabulary changes with product releases, not with polls,
    /// so anything more frequent spends budget to re-learn a constant.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    public async Task<AuditLogVocabularyReport?> CheckAsync(string groupId, CancellationToken ct)
    {
        var endpoint = new VRChatEndpoint(
            VRChatEndpointClass.GroupsAuditLogTypes, groupId, "GetGroupAuditLogEntryTypes");

        var result = await gate.ExecuteAsync(
            endpoint,
            (client, token) => client.Groups.GetGroupAuditLogEntryTypesWithHttpInfoAsync(groupId, token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (!result.Success || result.Value is null)
        {
            // Deliberately quiet and deliberately not retried. This is a diagnostic; the audit log
            // keeps running without it, and the next scheduled check will try again.
            _logger.Debug(
                "Audit-log vocabulary check did not complete ({Status}). The mapping table is "
                + "unverified until the next attempt; sync is unaffected.",
                result.StatusCode);
            return null;
        }

        return Compare(result.Value, clock.UtcNow);
    }

    /// <summary>Pure, so the comparison is testable without a gate.</summary>
    internal static AuditLogVocabularyReport Compare(IEnumerable<string> declared, DateTimeOffset now)
    {
        var vrchat = declared
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unmapped = vrchat
            .Where(t => !GroupAuditLogEvents.TryMap(t, out _))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        var missingPrimary = GroupAuditLogEvents.Primary
            .Where(t => !vrchat.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        var unusedAliases = GroupAuditLogEvents.Aliases
            .Where(t => !vrchat.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        return new AuditLogVocabularyReport(
            now,
            vrchat.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
            unmapped,
            missingPrimary,
            unusedAliases);
    }

    /// <summary>
    /// Writes the report to the log at a level matching what it found.
    /// </summary>
    /// <remarks>
    /// A missing primary is a warning because it means facts are being lost right now. Unmapped
    /// types are informational: they are events Modbot has decided not to record, which is a
    /// choice rather than a fault.
    /// </remarks>
    public void Report(AuditLogVocabularyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.HasProblem)
        {
            _logger.Warning(
                "VRChat does not declare {Count} audit-log event type(s) Modbot maps as real: "
                + "{Missing}. Whatever those map to is not being recorded. Check the constants in "
                + "GroupAuditLogEvents against this group's declared list: {Declared}",
                report.MissingPrimary.Count,
                report.MissingPrimary,
                report.Declared);
        }

        if (report.Unmapped.Count > 0)
        {
            _logger.Information(
                "VRChat declares {Count} audit-log event type(s) Modbot does not record: "
                + "{Unmapped}",
                report.Unmapped.Count,
                report.Unmapped);
        }

        if (!report.HasProblem && report.Unmapped.Count == 0)
        {
            _logger.Information(
                "Audit-log vocabulary verified against VRChat: every declared event type is "
                + "mapped, and every mapping Modbot depends on is declared.");
        }
    }
}
