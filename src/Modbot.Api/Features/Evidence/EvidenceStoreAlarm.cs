using Modbot.Core.Data.Entities;
using Modbot.Core.Notifications;
using Modbot.Evidence.Health;

namespace Modbot.Api.Features.Evidence;

/// <summary>
/// Tells somebody when the evidence store is not where it should be (evidence storage design §8.4).
/// </summary>
/// <remarks>
/// <para>
/// §8.4 has always said a locked store raises a <c>Critical</c> notification on every available
/// channel. Until the notification pipeline existed there was nowhere to raise it, so the lock
/// produced a banner and a log line and nothing that reaches a person who is not looking at the
/// screen. This is that sentence, finally connected.
/// </para>
/// <para>
/// <strong>It goes to administrators.</strong> The lock is a hosting problem — a volume that did not
/// mount, a bucket that was emptied — and acknowledging the banner is already an administrator's
/// act, so the audience is the same people.
/// </para>
/// </remarks>
public static class EvidenceStoreAlarm
{
    public static async Task RaiseIfLockedAsync(
        EvidenceStoreHealth health, INotifier notifier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(notifier);

        // Only a lock, and only the first time it is noticed. An unreachable store is transient
        // until proven otherwise and never locks, and repeating a lock on every probe is the same
        // mistake as a banner nobody reads.
        if (health is not { State: EvidenceStoreState.Unavailable, ShouldAlarm: true })
            return;

        var body = $"{health.Explanation}\n\n"
                   + $"Expected store: {health.ExpectedStoreId?.ToString() ?? "none recorded"}\n"
                   + $"Found: {health.FoundStoreId?.ToString() ?? "no store marker"}\n\n"
                   + "Uploads are refused until this is sorted out. Open Settings, Evidence.";

        await notifier.RaiseAsync(
            new Notification(
                NotificationKinds.EvidenceStoreMissing,
                NotificationSeverity.Critical,
                "Modbot: the evidence store is not there",
                body,
                NotificationAudience.Holding(ModbotPermissions.ManageSettings))
            {
                // One key for the whole incident. The store being wrong is one thing, however many
                // times it is probed, and it stays wrong until somebody fixes it.
                SameAs = $"{NotificationKinds.EvidenceStoreMissing}:{health.ExpectedStoreId}",
                Link = "/settings#evidence",
            },
            ct);
    }
}
