using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;
using VRChat.API.Model;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Reads each open group instance's own page for how many people are in it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the instance's page and not the group's list.</strong> The list's <c>memberCount</c>
/// read 2 in the first live probe while the instance's own page said 3, so the list's number may
/// count group members only (research: vrchat-instance-findings.md section 3). The instance's page
/// is read for the head count; the list's number stays as the fallback (<see cref="HeadCounts"/>).
/// </para>
/// <para>
/// <strong>Which of the page's numbers.</strong> <c>userCount</c>, with <c>n_users</c> kept beside
/// it. <c>n_users</c> ran up to about thirty above the real count on a busy club, reading 80 in a
/// world that holds 80 while <c>userCount</c> said 51 (<see cref="HeadCounts"/>). A body with no
/// <c>userCount</c> falls back to <c>n_users</c>, and that count is unsure.
/// </para>
/// <para>
/// <strong>Only instances the group's list carries right now.</strong>
/// <c>GET /instances/{location}</c> answers 200 with a full body for an instance that never
/// existed (research section 1), so this never asks about an instance the list does not vouch for, and
/// a read that says <c>active: false</c> is not believed.
/// </para>
/// <para>
/// <strong>This is how an instance nobody from the team is standing in gets a head count.</strong> No
/// moderator's client is involved; the Live page and the Discord card show the number either way.
/// </para>
/// <para>
/// <strong>Budget.</strong> <c>instances.read</c>, measured at one request per second
/// (<see cref="VRChatEndpointClass.InstancesRead"/>). Each instance is read about once every
/// <see cref="ReadEvery"/>, so the steady rate is the number of open instances divided by thirty, and
/// the bucket paces anything beyond that. A 429 is never retried: the pass stops, and the instances
/// fall back to the list's count once their last read is stale (spec 4.3.1).
/// </para>
/// </remarks>
public sealed class InstanceHeadCountSync
{
    /// <summary>How often each instance's page is read.</summary>
    public static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many instances one pass reads. The bucket allows one a second, so a pass takes at most this
    /// many seconds and then lets the loop look again.
    /// </summary>
    public const int InstancesPerPass = 10;

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public InstanceHeadCountSync(IVRChatGate gate, ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<InstanceHeadCountRunResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settings.ManagedGroupId))
            return new InstanceHeadCountRunResult(SyncOutcome.NotConfigured);

        var groupId = settings.ManagedGroupId;
        var due = _clock.UtcNow - ReadEvery;

        // Open, carried by the group's list, and not read in the last thirty seconds. Instances never
        // read come first, then the longest-waiting.
        var instances = await _db.VRChatInstances
            .Where(i => i.GroupId == groupId
                && i.SeenInGroupList
                && i.ClosedAt == null
                && (i.PageCheckedAt == null || i.PageCheckedAt <= due))
            .OrderBy(i => i.PageCheckedAt.HasValue)
            .ThenBy(i => i.PageCheckedAt)
            .Take(InstancesPerPass)
            .ToListAsync(ct).ConfigureAwait(false);

        if (instances.Count == 0)
            return new InstanceHeadCountRunResult(SyncOutcome.Quiet);

        var read = 0;
        var failed = 0;

        foreach (var row in instances)
        {
            var colon = row.Location.IndexOf(':');
            if (colon <= 0 || colon == row.Location.Length - 1)
            {
                row.PageCheckedAt = _clock.UtcNow;
                failed++;
                continue;
            }

            // VRChat's ids are passed exactly as they were stored, qualifiers and all, and never
            // checked for shape (spec 3.1.1).
            var worldId = row.Location[..colon];
            var instanceId = row.Location[(colon + 1)..];

            var endpoint = new VRChatEndpoint(VRChatEndpointClass.InstancesRead, row.Location, "GetInstance");

            var result = await _gate.ExecuteAsync(
                endpoint,
                (client, token) => client.Instances.GetInstanceWithHttpInfoAsync(worldId, instanceId, token),
                VRChatCallPriority.Background,
                ct).ConfigureAwait(false);

            if (result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
            {
                // Cold stop. Never retried (spec 4.3.1): the pass ends here, what was read is kept,
                // and every instance falls back to the group list's count once its read goes stale.
                _log.Information(
                    "Reading instance head counts is paused: {Reason}",
                    result.ErrorMessage ?? "the instances.read bucket is cold-stopped");

                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                return new InstanceHeadCountRunResult(SyncOutcome.RateLimited, read, failed, result.ErrorMessage);
            }

            var at = _clock.UtcNow;
            row.PageCheckedAt = at;

            // A 200 means nothing on its own: the endpoint answers one with a full body for an instance
            // that never existed, and says active: false (research section 1). So an instance reading
            // inactive keeps the list's count rather than taking this body's zero.
            if (!result.Success || result.Value is not { Active: true } instance)
            {
                failed++;

                if (row.LastUserCount is { } listed)
                    HeadCounts.Record(_db, row, listed, HeadCounts.FromList, at);

                continue;
            }

            // userCount is the head count; n_users read up to about thirty higher on a busy evening
            // (HeadCounts). n_users stands in only when the body has no userCount, and is then unsure.
            var userCount = UserCountIn(instance, result.RawResponse);
            var (headCount, _) = HeadCounts.FromPageRead(instance.NUsers, userCount);

            row.PageReadAt = at;
            row.PageUserCount = userCount;

            // The name the instance was opened with comes in the same body, so it is kept at no cost.
            // Every good read overwrites it, so a rename shows and a cleared name goes back to the
            // number.
            row.Name = VRChatInstance.NameFrom(instance.DisplayName, row.VRChatInstanceId);
            HeadCounts.Record(_db, row, headCount, HeadCounts.FromPage, at, userCount, instance.NUsers);
            read++;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new InstanceHeadCountRunResult(SyncOutcome.Produced, read, failed);
    }

    /// <summary>
    /// The page's <c>userCount</c>, or null when the body did not carry one.
    /// </summary>
    /// <remarks>
    /// The SDK's model holds <c>userCount</c> as a plain number, so a missing one and a nought look the
    /// same there. The body as it arrived tells them apart: no <c>userCount</c>, or a null one, is
    /// missing. Without a body to look at, the model's number is taken.
    /// </remarks>
    internal static int? UserCountIn(Instance instance, string? body)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(body))
            return instance.UserCount;

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return instance.UserCount;

            return document.RootElement.TryGetProperty("userCount", out var value)
                   && value.ValueKind == JsonValueKind.Number
                   && value.TryGetInt32(out var people)
                ? people
                : null;
        }
        catch (JsonException)
        {
            return instance.UserCount;
        }
    }
}

/// <summary>What one pass of the instance head count read did.</summary>
/// <param name="Read">Instances whose page was read and believed.</param>
/// <param name="Failed">Instances whose page could not be read, or said the instance was not active.</param>
public sealed record InstanceHeadCountRunResult(
    SyncOutcome Outcome,
    int Read = 0,
    int Failed = 0,
    string? Message = null);
