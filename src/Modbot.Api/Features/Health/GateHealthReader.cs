using Modbot.VRChat;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Session;

namespace Modbot.Api.Features.Health;

/// <summary>
/// Turns the gate's state and its buckets into something an operator can act on.
/// </summary>
/// <remarks>
/// One place, used by both the sidebar indicator and the health screen, so the two can never
/// disagree about whether Modbot is broken. Two implementations of this judgement is how a
/// dashboard ends up with a green dot beside a red panel.
/// </remarks>
public static class GateHealthReader
{
    public static async Task<(GateHealth Gate, IReadOnlyList<BucketHealth> Buckets)> ReadAsync(
        IVRChatGate gate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        IReadOnlyList<RateLimitBucketHealth> raw;

        try
        {
            raw = await gate.DescribeBucketsAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // The bucket store reads the database. A health screen that cannot answer because its
            // own diagnostics threw is worse than one that answers without the bucket detail --
            // the state and the status below do not depend on it.
            raw = [];
        }

        var buckets = raw
            .Select(b => new BucketHealth(
                b.Name,
                b.EndpointClass,
                b.ResourceId,
                b.EffectiveRatePerSecond,
                b.BudgetMultiplier,
                b.IsColdStopped,
                b.StoppedUntil,
                b.Alerting,
                b.RateLimitHits,
                b.LastRateLimitedAt))
            .OrderByDescending(b => b.Alerting)
            .ThenByDescending(b => b.IsColdStopped)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .ToList();

        SignInStatus? signIn;

        try
        {
            signIn = await gate.DescribeSignInAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reasoning as the buckets: the stored wait lives in the database, and a health
            // read that cannot reach it still answers with what the gate knows in memory.
            signIn = null;
        }

        return (Describe(signIn?.State ?? gate.State, buckets, signIn), buckets);
    }

    /// <summary>The status decision, as a pure function of the state, the buckets and signing in.</summary>
    public static GateHealth Describe(
        VRChatSessionState state, IReadOnlyList<BucketHealth> buckets, SignInStatus? signIn = null)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        var health = DescribeState(state, buckets, signIn);

        if (signIn is null)
            return health;

        return health with
        {
            SignInWait = signIn.Wait is { } wait
                ? new SignInWaitHealth(
                    wait.Reason,
                    wait.RetryAt,
                    // Whole seconds, rounded up, from the server's clock: the banner counts down
                    // from this rather than from the browser's clock, which may be wrong.
                    (int)Math.Max(0, Math.Ceiling((wait.RetryAt - signIn.Now).TotalSeconds)))
                : null,
            LastSignedInAt = signIn.LastSignedInAt,
            SignInsInLastHour = signIn.SignInsInLastHour,
            SignInLimit = signIn.SignInLimit,
        };
    }

    private static GateHealth DescribeState(
        VRChatSessionState state, IReadOnlyList<BucketHealth> buckets, SignInStatus? signIn)
    {

        var stopped = buckets.Count(b => b.IsColdStopped);
        var alerting = buckets.Count(b => b.Alerting);

        var endsAt = buckets
            .Where(b => b.IsColdStopped && b.StoppedUntil is not null)
            .Select(b => b.StoppedUntil!.Value)
            .DefaultIfEmpty()
            .Min();

        var coldStopEndsAt = endsAt == default ? (DateTimeOffset?)null : endsAt;

        // A wait on signing in stops everything, so it heads the list: nothing else below is
        // happening while it lasts (spec 4.1.2).
        if (state is VRChatSessionState.SignInWaiting || signIn?.Wait is not null)
        {
            return new GateHealth(
                VRChatSessionState.SignInWaiting.ToString(),
                GateStatus.WaitingOnPurpose,
                "Waiting to sign in to VRChat.",
                stopped,
                coldStopEndsAt,
                alerting);
        }

        // Ordered by how much the operator has to care, most first. A bucket that has run out of
        // probes outranks the session state: a gate reporting Healthy while one class has given up
        // is still a deployment with a hole in it, and "healthy" is the wrong headline for that.
        if (alerting > 0)
        {
            return new GateHealth(
                state.ToString(),
                GateStatus.NeedsOperator,
                $"{Count(alerting, "endpoint class has", "endpoint classes have")} stopped after "
                + "repeated rate limits.",
                stopped,
                coldStopEndsAt,
                alerting);
        }

        return state switch
        {
            VRChatSessionState.Unconfigured => new GateHealth(
                state.ToString(),
                GateStatus.NotConfigured,
                "No VRChat account is configured.",
                stopped,
                coldStopEndsAt,
                alerting),

            VRChatSessionState.WafBlocked => new GateHealth(
                state.ToString(),
                GateStatus.NeedsOperator,
                "Cloudflare is blocking this host. Modbot needs a proxy to reach VRChat.",
                stopped,
                coldStopEndsAt,
                alerting),

            VRChatSessionState.NoGroupAccess => new GateHealth(
                state.ToString(),
                GateStatus.NeedsOperator,
                "The VRChat account cannot read the group.",
                stopped,
                coldStopEndsAt,
                alerting),

            VRChatSessionState.RateLimited => new GateHealth(
                state.ToString(),
                GateStatus.WaitingOnPurpose,
                stopped > 0
                    ? $"{Count(stopped, "endpoint class is", "endpoint classes are")} waiting out a rate limit."
                    : "Waiting out a rate limit.",
                stopped,
                coldStopEndsAt,
                alerting),

            VRChatSessionState.Reauthenticating => new GateHealth(
                state.ToString(),
                GateStatus.Working,
                "Signing back in to VRChat.",
                stopped,
                coldStopEndsAt,
                alerting),

            _ when stopped > 0 => new GateHealth(
                state.ToString(),
                GateStatus.WaitingOnPurpose,
                $"{Count(stopped, "endpoint class is", "endpoint classes are")} waiting out a rate limit.",
                stopped,
                coldStopEndsAt,
                alerting),

            _ => new GateHealth(
                state.ToString(),
                GateStatus.Working,
                "Reading from VRChat normally.",
                stopped,
                coldStopEndsAt,
                alerting),
        };
    }

    private static string Count(int n, string singular, string plural)
        => n == 1 ? $"One {singular}" : $"{n} {plural}";
}
