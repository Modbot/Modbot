using System.Net;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Session;
using Modbot.VRChat.Tests.Fakes;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// The gate: one session, one queue, 401 re-login, WAF classification, cold stop (spec 4.1).
/// </summary>
public class VRChatGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Members =
        new(VRChatEndpointClass.GroupsMembers, "grp_test", "GetGroupMembers");

    [Fact]
    public async Task AnUnconfiguredAccountFailsWithoutIssuingAnything()
    {
        var vrchat = new FakeVRChat();
        var gate = NewGate(vrchat, out _, new FakeConnectionStore(new VRChatConnection()));

        var result = await gate.ExecuteAsync<string>(
            Members, (_, _) => throw new UnreachableException(), ct: Ct);

        Assert.False(result.Success);
        Assert.True(result.WasNotSent);
        Assert.Equal(VRChatSessionState.Unconfigured, gate.State);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task ASuccessfulLoginStoresTheSessionCookie()
    {
        var vrchat = new FakeVRChat().SignedInAs("Modbot", "usr_1");
        var store = new FakeConnectionStore();
        var gate = NewGate(vrchat, out _, store);

        var result = await gate.SignInAsync(Ct);

        Assert.True(result.Success);
        Assert.Equal("usr_1", result.Value!.Id);
        Assert.Equal(VRChatSessionState.Healthy, gate.State);

        // Spec 4.1: persisted in the database, never a cookie.txt on disk. Without this every
        // restart is a fresh login against the auth endpoint.
        Assert.Equal(1, store.SessionSaves);
        Assert.Equal("authCookieValue", store.SavedAuthCookie);
    }

    [Fact]
    public async Task AStoredCookieIsUsedInsteadOfLoggingInAgain()
    {
        var vrchat = new FakeVRChat().SignedInAs();
        var store = new FakeConnectionStore(
            new VRChatConnection("modbot@example.com", "hunter2", AuthCookie: "storedCookie"));

        var gate = NewGate(vrchat, out var factory, store);
        await gate.SignInAsync(Ct);

        Assert.Equal("storedCookie", factory.Built[0].AuthCookie);
    }

    [Fact]
    public async Task AnExpiredStoredSessionFallsBackToThePasswordOnce()
    {
        var vrchat = new FakeVRChat()
            .RespondsWith(FakeVRChat.Status(HttpStatusCode.Unauthorized))
            .SignedInAs("Modbot", "usr_1");

        var store = new FakeConnectionStore(
            new VRChatConnection("modbot@example.com", "hunter2", AuthCookie: "staleCookie"));

        var gate = NewGate(vrchat, out var factory, store);
        var result = await gate.SignInAsync(Ct);

        // A 401 from a stored cookie says the session expired, not that the password is wrong,
        // and those need different answers (spec 4.1.1).
        Assert.True(result.Success);
        Assert.Equal(2, factory.Built.Count);
        Assert.Equal("staleCookie", factory.Built[0].AuthCookie);
        Assert.Null(factory.Built[1].AuthCookie);
        Assert.Equal("authCookieValue", store.SavedAuthCookie);
    }

    [Fact]
    public async Task ATwoFactorChallengeIsAnsweredFromTheStoredSecret()
    {
        var vrchat = new FakeVRChat().ChallengesWithTotp();
        var store = new FakeConnectionStore();
        var gate = NewGate(vrchat, out _, store);

        var result = await gate.SignInAsync(Ct);

        Assert.True(result.Success);
        Assert.Equal(1, vrchat.Verify2FACalls);
        Assert.Matches(@"^\d{6}$", vrchat.SubmittedCodes.Single());

        // The current user is re-fetched afterwards, because the first response was the challenge
        // rather than the account (spec 4.1.1).
        Assert.Equal(2, vrchat.GetCurrentUserCalls);
        Assert.Equal("twoFactorCookieValue", store.SavedTwoFactorAuthCookie);
    }

    [Fact]
    public async Task ATwoFactorChallengeWithNoStoredSecretIsExplained()
    {
        var vrchat = new FakeVRChat()
            .RespondsWith(FakeVRChat.Ok(new CurrentUser { RequiresTwoFactorAuth = ["emailOtp"] }));

        var store = new FakeConnectionStore(new VRChatConnection("modbot@example.com", "hunter2"));
        var gate = NewGate(vrchat, out _, store);

        var result = await gate.SignInAsync(Ct);

        Assert.False(result.Success);

        // A daemon cannot be asked for a code, and the message has to say so -- otherwise this
        // presents as a mysterious login failure with correct credentials.
        Assert.Contains("TOTP", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("emailOtp", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedCredentialsAreReportedAsSuchRatherThanRetried()
    {
        var vrchat = new FakeVRChat().RespondsWith(FakeVRChat.Status(HttpStatusCode.Unauthorized));
        var gate = NewGate(vrchat, out _, new FakeConnectionStore());

        var result = await gate.SignInAsync(Ct);

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal(VRChatSessionState.Unconfigured, gate.State);

        // One attempt. Retrying a refused password only locks the account faster.
        Assert.Equal(1, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task A401OnACallTriggersOneReLoginAndOneRetry()
    {
        var vrchat = new FakeVRChat()
            .SignedInAs()
            .RespondsWith(FakeVRChat.Ok(new CurrentUser { DisplayName = "Modbot", Id = "usr_1" }));

        var store = new FakeConnectionStore();
        var gate = NewGate(vrchat, out var factory, store);

        var calls = 0;
        var result = await gate.ExecuteAsync(
            Members,
            (_, _) =>
            {
                calls++;
                return Task.FromResult(calls == 1
                    ? Response<string>(HttpStatusCode.Unauthorized)
                    : Response(HttpStatusCode.OK, "members"));
            },
            ct: Ct);

        Assert.True(result.Success);
        Assert.Equal("members", result.Value);
        Assert.Equal(2, calls);

        // The re-login discards the session that was just refused. Sending it again would
        // authenticate with the cookie that caused the 401.
        Assert.Null(factory.Built[^1].AuthCookie);
        Assert.Equal(VRChatSessionState.Healthy, gate.State);
    }

    [Fact]
    public async Task A401AfterReAuthenticatingIsNotRetriedAgain()
    {
        var vrchat = new FakeVRChat()
            .SignedInAs()
            .RespondsWith(FakeVRChat.Ok(new CurrentUser { DisplayName = "Modbot", Id = "usr_1" }));

        var gate = NewGate(vrchat, out _, new FakeConnectionStore());

        var calls = 0;
        var result = await gate.ExecuteAsync(
            Members,
            (_, _) =>
            {
                calls++;
                return Task.FromResult(Response<string>(HttpStatusCode.Unauthorized));
            },
            ct: Ct);

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);

        // Exactly two: one, a re-login, and one more. A loop here would hammer the auth endpoint
        // for as long as the session stayed broken.
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A429ColdStopsAndTheNextCallIsNeverSent()
    {
        var vrchat = new FakeVRChat().SignedInAs();
        var gate = NewGate(vrchat, out _, new FakeConnectionStore());

        var calls = 0;
        var limited = await gate.ExecuteAsync(
            Members,
            (_, _) =>
            {
                calls++;
                return Task.FromResult(Response<string>(HttpStatusCode.TooManyRequests));
            },
            ct: Ct);

        Assert.True(limited.IsRateLimited);
        Assert.Equal(VRChatSessionState.RateLimited, gate.State);

        var refused = await gate.ExecuteAsync(
            Members,
            (_, _) =>
            {
                calls++;
                return Task.FromResult(Response(HttpStatusCode.OK, "members"));
            },
            ct: Ct);

        Assert.False(refused.Success);
        Assert.True(refused.WasNotSent);

        // The second call never reached the SDK. Not "retried and failed" -- not issued.
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ACloudflareBlockIsClassifiedAsAWafBlockRatherThanAPermissionsError()
    {
        var vrchat = new FakeVRChat().SignedInAs();
        var gate = NewGate(vrchat, out _, new FakeConnectionStore());

        var result = await gate.ExecuteAsync(
            Members,
            (_, _) => Task.FromResult(Response<string>(
                HttpStatusCode.Forbidden,
                raw: "<!DOCTYPE html><html><head><title>Attention Required! | Cloudflare</title></head>"
                     + "<body>Sorry, you have been blocked. Error code: 1020. Cloudflare Ray ID: 8f2</body></html>")),
            ct: Ct);

        Assert.False(result.Success);
        Assert.True(result.IsWafBlocked);
        Assert.Equal(1020, result.WafCode);
        Assert.Equal(VRChatSessionState.WafBlocked, gate.State);

        // The message has to say the account is fine, because a 403 otherwise sends an operator
        // to audit their group roles for an afternoon (spec 7.1.1).
        Assert.Contains("proxy", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnOrdinaryForbiddenIsNotMistakenForAWafBlock()
    {
        var vrchat = new FakeVRChat().SignedInAs();
        var gate = NewGate(vrchat, out _, new FakeConnectionStore());

        var result = await gate.ExecuteAsync(
            Members,
            (_, _) => Task.FromResult(Response<string>(
                HttpStatusCode.Forbidden,
                raw: """{"error":{"message":"You do not have permission.","status_code":403}}""")),
            ct: Ct);

        Assert.False(result.Success);
        Assert.False(result.IsWafBlocked);
        Assert.NotEqual(VRChatSessionState.WafBlocked, gate.State);
    }

    [Fact]
    public async Task ATransportFailureIsNotEvidenceAboutTheRateLimit()
    {
        var vrchat = new FakeVRChat().SignedInAs();
        var gate = NewGate(vrchat, out _, new FakeConnectionStore(), out var harness);

        var result = await gate.ExecuteAsync(
            Members,
            (_, _) => Task.FromException<ApiResponse<string>>(new HttpRequestException("no route to host")),
            ct: Ct);

        Assert.False(result.Success);
        Assert.True(result.WasNotSent);

        // No 429 was seen, so nothing is cold-stopped and no budget was spent. A DNS failure is
        // not the API telling Modbot to slow down.
        var buckets = await harness.Limiter.DescribeAsync(Ct);
        Assert.All(buckets, b => Assert.False(b.IsColdStopped));
        Assert.All(buckets, b => Assert.Equal(1.0, b.BudgetMultiplier, 6));
    }

    [Fact]
    public async Task CallsThroughTheGateNeverOverlap()
    {
        var vrchat = new FakeVRChat().SignedInAs();
        var gate = NewGate(vrchat, out _, new FakeConnectionStore(), out _, LimiterHarness.Unpaced());

        var concurrent = 0;
        var peak = 0;

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => gate.ExecuteAsync(
            Members,
            async (_, token) =>
            {
                var depth = Interlocked.Increment(ref concurrent);
                Interlocked.CompareExchange(ref peak, depth, peak < depth ? peak : depth);

                await Task.Yield();
                Interlocked.Decrement(ref concurrent);

                return Response(HttpStatusCode.OK, "ok");
            },
            ct: Ct)));

        // One authenticated session means one call at a time (spec 4.1).
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task TheSessionIsEstablishedOnceAndReused()
    {
        var vrchat = new FakeVRChat().SignedInAs();
        var gate = NewGate(vrchat, out var factory, new FakeConnectionStore(), out _, LimiterHarness.Unpaced());

        for (var i = 0; i < 5; i++)
        {
            await gate.ExecuteAsync(
                Members, (_, _) => Task.FromResult(Response(HttpStatusCode.OK, "ok")), ct: Ct);
        }

        Assert.Equal(1, vrchat.GetCurrentUserCalls);
        Assert.Single(factory.Built);
    }

    private static ApiResponse<T> Response<T>(HttpStatusCode status, T? data = default, string raw = "") =>
        new(status, new Multimap<string, string>(), data!, raw);

    private static VRChatGate NewGate(
        FakeVRChat vrchat, out FakeClientFactory factory, FakeConnectionStore store) =>
        NewGate(vrchat, out factory, store, out _);

    private static VRChatGate NewGate(
        FakeVRChat vrchat,
        out FakeClientFactory factory,
        FakeConnectionStore store,
        out LimiterHarness harness,
        RateLimitOptions? limits = null)
    {
        factory = new FakeClientFactory(vrchat.Client);
        harness = new LimiterHarness(limits);

        return new VRChatGate(
            factory, store, harness.Limiter, harness.Clock, new FakeMonotonicClock());
    }

    private sealed class UnreachableException()
        : InvalidOperationException("The gate issued a call it should have refused.");
}
