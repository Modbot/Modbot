using System.Net;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.Explore;

/// <summary>
/// Finds the largest <c>offset</c> each paginated group endpoint accepts, by asking.
/// </summary>
/// <remarks>
/// <para>
/// The audit log is known to reject <c>offset=7501</c> with a 400 whose body says
/// <c>offset＝7501 is above the limit</c>. Whether members, bans, invites and join requests share
/// that cap is not documented anywhere, so this measures it.
/// </para>
/// <para>
/// Three outcomes are told apart, because they mean different things:
/// </para>
/// <list type="bullet">
///   <item><b>200 with items</b> — the offset is fine.</item>
///   <item><b>200 with an empty list</b> — the group has fewer items than that; the cap, if any,
///   is beyond the data and cannot be observed from this group.</item>
///   <item><b>400</b> — the cap. The body is printed verbatim so nobody has to trust the
///   classification.</item>
/// </list>
/// <para>
/// Paced at one request every 3.5 s — the most conservative class in the foundation spec — and
/// <b>a 429 aborts the entire run</b>. Never retried: VRChat's limiter extends its penalty for
/// traffic sent while it is in force.
/// </para>
/// </remarks>
public static class OffsetProbe
{
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(3.5);

    /// <summary>The value the audit log is known to cap at. Probed directly first.</summary>
    private const int KnownCap = 7500;

    private enum Outcome { Ok, Empty, Cap, RateLimited, Other }

    private sealed record Probe(int Offset, Outcome Outcome, int Status, int Count, string Body);

    public static async Task RunAsync(IVRChat vrchat, string groupId)
    {
        var endpoints = new (string Name, Func<int, Task<ApiResponse<List<GroupMember>>>> Call)[]
        {
            ("group bans", o => vrchat.Groups.GetGroupBansWithHttpInfoAsync(groupId, n: 1, offset: o)),
            ("group members", o => vrchat.Groups.GetGroupMembersWithHttpInfoAsync(groupId, n: 1, offset: o)),
            ("group invites", o => vrchat.Groups.GetGroupInvitesWithHttpInfoAsync(groupId, n: 1, offset: o)),
            ("group join requests", o => vrchat.Groups.GetGroupRequestsWithHttpInfoAsync(groupId, n: 1, offset: o)),
        };

        var summary = new List<string>();

        foreach (var (name, call) in endpoints)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {name} ===");

            var result = await ProbeEndpointAsync(name, call);
            summary.Add($"{name,-22} {result}");

            if (result.StartsWith("ABORTED", StringComparison.Ordinal))
            {
                Console.WriteLine("Stopping the whole run: a 429 means every further request extends the penalty.");
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== summary ===");
        foreach (var line in summary) Console.WriteLine(line);
    }

    private static async Task<string> ProbeEndpointAsync(
        string name, Func<int, Task<ApiResponse<List<GroupMember>>>> call)
    {
        // Cheapest first: the two requests that settle it if this endpoint matches the audit log.
        var at = await OneAsync(call, KnownCap);
        if (at.Outcome is Outcome.RateLimited) return "ABORTED on 429";

        var past = await OneAsync(call, KnownCap + 1);
        if (past.Outcome is Outcome.RateLimited) return "ABORTED on 429";

        switch (at.Outcome, past.Outcome)
        {
            case (Outcome.Ok, Outcome.Cap):
                return $"cap = {KnownCap} (same as the audit log)";

            case (Outcome.Empty, Outcome.Cap):
                return $"cap = {KnownCap}, enforced even past the end of the data (group has < {KnownCap})";

            case (Outcome.Empty, Outcome.Empty):
                // Fewer items than the cap and no 400: the cap is not observable from this group.
                // Find where the data actually ends, since that is at least a useful number.
                var end = await SearchAsync(call, 0, KnownCap, stopOn: Outcome.Empty);
                return $"no cap seen up to {KnownCap + 1}; data ends near offset {end} (cap unobservable here)";

            case (Outcome.Ok, Outcome.Ok):
                // Higher than the audit log's. Find where it actually stops.
                var high = await SearchAsync(call, KnownCap + 1, 20_000, stopOn: Outcome.Cap);
                return high < 0 ? "no cap found up to 20,000" : $"cap ≈ {high} (higher than the audit log)";

            case (Outcome.Cap, _):
                // Lower than the audit log's. Find it.
                var low = await SearchAsync(call, 0, KnownCap, stopOn: Outcome.Cap);
                return $"cap ≈ {low} (LOWER than the audit log)";

            default:
                return $"inconclusive: offset {KnownCap} → {at.Outcome}/{at.Status}, {KnownCap + 1} → {past.Outcome}/{past.Status}";
        }
    }

    /// <summary>
    /// Binary search for the first offset in (lo, hi] whose outcome is <paramref name="stopOn"/>,
    /// assuming outcomes are monotonic in the offset. Returns -1 if none in range.
    /// </summary>
    private static async Task<int> SearchAsync(
        Func<int, Task<ApiResponse<List<GroupMember>>>> call, int lo, int hi, Outcome stopOn)
    {
        var found = -1;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            var p = await OneAsync(call, mid);
            if (p.Outcome is Outcome.RateLimited) return found;

            if (p.Outcome == stopOn) { found = mid; hi = mid; }
            else lo = mid + 1;
        }
        return found;
    }

    private static async Task<Probe> OneAsync(Func<int, Task<ApiResponse<List<GroupMember>>>> call, int offset)
    {
        await Task.Delay(Pace);

        int status;
        var count = 0;
        var body = string.Empty;

        try
        {
            var response = await call(offset);
            status = (int)response.StatusCode;
            count = response.Data?.Count ?? 0;
            body = response.RawContent ?? string.Empty;
        }
        catch (ApiException ex)
        {
            // The SDK's WithHttpInfo overloads return non-2xx; this is for transport failures.
            status = ex.ErrorCode;
            body = ex.ErrorContent?.ToString() ?? ex.Message;
        }

        var outcome = status switch
        {
            200 when count > 0 => Outcome.Ok,
            200 => Outcome.Empty,
            400 => Outcome.Cap,
            429 => Outcome.RateLimited,
            _ => Outcome.Other,
        };

        var shownBody = outcome is Outcome.Ok or Outcome.Empty ? "" : "  " + Truncate(body, 220);
        Console.WriteLine($"  offset {offset,6} → {status} {outcome,-11} items={count}{shownBody}");

        return new Probe(offset, outcome, status, count, body);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s.Replace('\n', ' ') : s[..max].Replace('\n', ' ') + "…";
}
