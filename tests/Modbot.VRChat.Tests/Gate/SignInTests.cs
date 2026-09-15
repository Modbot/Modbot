using System.Net;
using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Session;
using Modbot.VRChat.Tests.Fakes;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// Spec 4.1.2: VRChat allows about four or five sign-ins an hour and answers the next with an
/// hour-long block, so signing in is the scarcest thing the gate does.
/// </summary>
public class SignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string GroupId = "grp_test";

    private static readonly VRChatEndpoint Members =
        new(VRChatEndpointClass.GroupsMembers, GroupId, "GetGroupMembers");

    private static readonly VRChatEndpoint GroupRead =
        new(VRChatEndpointClass.GroupsRead, GroupId, "GetGroup");

    /// <summary>An account with no two-factor secret: one counted request per sign-in.</summary>
    private static VRChatConnection Account(string? cookie = null, string? groupId = null) =>
        new("modbot@example.com", "hunter2", AuthCookie: cookie, GroupId: groupId, SessionUserId: "usr_fake");

    // ---- The session check ----------------------------------------------------------------

    [Fact]
    public async Task AStartUpWithAGoodStoredCookieSpendsNoSignIn()
    {
        var vrchat = new FakeVRChat().AlwaysSignedInAs();
        var harness = new Harness(vrchat, Account(cookie: "storedCookie", groupId: GroupId));

        var result = await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        Assert.True(result.Success);

        // Exactly one GET /auth, and no /auth/user at all: that is the call VRChat counts as
        // signing in again, and a redeploy that made it spent one of the hour's handful.
        Assert.Equal(1, vrchat.VerifyAuthTokenCalls);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
        Assert.Equal(0, vrchat.Verify2FACalls);
        Assert.Empty(harness.SignIns.Attempts);

        // And only once: the next call uses the session without checking it again.
        await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);
        Assert.Equal(1, vrchat.VerifyAuthTokenCalls);
    }

    [Fact]
    public async Task AStartUpWithARejectedCookieSpendsExactlyOneSignIn()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.Unauthorized, "{}") }.AlwaysSignedInAs();
        var harness = new Harness(vrchat, Account(cookie: "expiredCookie"));

        var result = await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        Assert.True(result.Success);
        Assert.Equal(1, vrchat.GetCurrentUserCalls);
        Assert.Single(harness.SignIns.Attempts);

        // The sign-in is sent without the rejected cookie, and the new cookie replaces it.
        Assert.Null(harness.Factory.Built[^1].AuthCookie);
        Assert.Equal("authCookieValue", harness.Store.SavedAuthCookie);
        Assert.NotNull(harness.SignIns.LastSignedInAt);
    }

    [Fact]
    public async Task VerifyAuthTokenOkFalseIsABadSession()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.OK, """{"ok":false}""") }.AlwaysSignedInAs();
        var harness = new Harness(vrchat, Account(cookie: "storedCookie"));

        await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        Assert.Equal(1, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task AnUnclearVerifyAuthTokenFallsThroughToGetUser()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.InternalServerError, "{}") }.AlwaysSignedInAs();
        vrchat.Users.Has(VRChatConnection.DefaultSessionCheckUserId, "Nayir");
        var harness = new Harness(vrchat, Account(cookie: "storedCookie", groupId: GroupId));

        var result = await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        Assert.True(result.Success);
        Assert.Equal([VRChatConnection.DefaultSessionCheckUserId], vrchat.Users.Requests);
        Assert.Equal(0, vrchat.Groups.GroupRequests);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task AnUnexpectedVerifyAuthTokenBodyIsUnclearRatherThanSignedOut()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.OK, "{}") }.AlwaysSignedInAs();
        vrchat.Users.Has(VRChatConnection.DefaultSessionCheckUserId, "Nayir");
        var harness = new Harness(vrchat, Account(cookie: "storedCookie"));

        await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        Assert.Single(vrchat.Users.Requests);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task GetUser401IsABadSession()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.BadGateway, "{}") }.AlwaysSignedInAs();
        vrchat.Users.Status = HttpStatusCode.Unauthorized;
        var harness = new Harness(vrchat, Account(cookie: "storedCookie", groupId: GroupId));

        await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        Assert.Equal(0, vrchat.Groups.GroupRequests);
        Assert.Equal(1, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task WhenNeitherCanSayGetGroupDecides()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.BadGateway, "{}") }.AlwaysSignedInAs();
        vrchat.Users.Status = HttpStatusCode.InternalServerError;
        var harness = new Harness(vrchat, Account(cookie: "storedCookie", groupId: GroupId));

        var result = await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        Assert.True(result.Success);
        Assert.Equal(1, vrchat.Groups.GroupRequests);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task AGroupRefusalAfterTwoUnclearAnswersIsNotReadAsABadSession()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.BadGateway, "{}") }.AlwaysSignedInAs();
        vrchat.Users.Status = HttpStatusCode.InternalServerError;
        vrchat.Groups.GroupStatus = HttpStatusCode.Unauthorized;
        var harness = new Harness(vrchat, Account(cookie: "storedCookie", groupId: GroupId));

        var result = await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        // Could be the session, could be the group. Signing in on a guess spends a sign-in.
        Assert.False(result.Success);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
        Assert.Empty(harness.SignIns.Attempts);
    }

    public static TheoryData<string> RateLimitedSteps => ["auth", "user", "group"];

    [Theory]
    [MemberData(nameof(RateLimitedSteps))]
    public async Task A429AtAnyStepOfTheCheckIsAColdStopAndNotASignIn(string step)
    {
        var vrchat = new FakeVRChat().AlwaysSignedInAs();
        vrchat.AuthToken = step == "auth" ? (HttpStatusCode.TooManyRequests, "{}") : (HttpStatusCode.BadGateway, "{}");
        vrchat.Users.Status = step == "user" ? HttpStatusCode.TooManyRequests : HttpStatusCode.InternalServerError;
        vrchat.Groups.GroupStatus = step == "group" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK;
        var harness = new Harness(vrchat, Account(cookie: "storedCookie", groupId: GroupId));

        var result = await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        Assert.False(result.Success);
        Assert.Equal(VRChatFailureKind.RateLimited, result.Kind);

        // The check stops where the limit was hit: nothing after it is asked.
        Assert.Equal(step == "auth" ? 0 : 1, vrchat.Users.RequestCount);
        Assert.Equal(step == "group" ? 1 : 0, vrchat.Groups.GroupRequests);

        // No sign-in, and not the sign-in wait either: this is an ordinary cold stop.
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
        Assert.Equal(VRChatSessionState.RateLimited, harness.Gate.State);
        Assert.Null((await harness.Gate.DescribeSignInAsync(Ct)).Wait);
    }

    // ---- Lost group access ----------------------------------------------------------------

    [Fact]
    public async Task AGroup401OnAWorkingSessionIsLostGroupAccessAndNeverASignIn()
    {
        var vrchat = new FakeVRChat().AlwaysSignedInAs();
        var harness = new Harness(vrchat, Account(cookie: "storedCookie", groupId: GroupId));

        await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);
        var checks = vrchat.VerifyAuthTokenCalls;

        var refused = await harness.Gate.ExecuteAsync(Members, Status<string>(HttpStatusCode.Unauthorized), ct: Ct);

        Assert.False(refused.Success);
        Assert.Contains("cannot read the group", refused.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(VRChatSessionState.NoGroupAccess, harness.Gate.State);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
        Assert.Equal(checks + 1, vrchat.VerifyAuthTokenCalls);

        // The next refused poll does not check again: nothing has changed that a check could learn.
        await harness.Gate.ExecuteAsync(Members, Status<string>(HttpStatusCode.Unauthorized), ct: Ct);
        Assert.Equal(checks + 1, vrchat.VerifyAuthTokenCalls);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task AGroupRead403IsLostGroupAccessWithoutACheck()
    {
        var vrchat = new FakeVRChat().AlwaysSignedInAs();
        var harness = new Harness(vrchat, Account(cookie: "storedCookie", groupId: GroupId));

        await harness.Gate.ExecuteAsync(GroupRead, Status<Group>(HttpStatusCode.Forbidden, """{"error":{"status_code":403}}"""), ct: Ct);

        Assert.Equal(VRChatSessionState.NoGroupAccess, harness.Gate.State);
        Assert.Equal(1, vrchat.VerifyAuthTokenCalls);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);

        // A profile read succeeding does not clear it; reading the group again does.
        await harness.Gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.UsersRead, null, "GetUser"), Ok("user"), ct: Ct);
        Assert.Equal(VRChatSessionState.NoGroupAccess, harness.Gate.State);

        await harness.Gate.ExecuteAsync(GroupRead, Ok(new Group()), ct: Ct);
        Assert.Equal(VRChatSessionState.Healthy, harness.Gate.State);
    }

    // ---- One at a time --------------------------------------------------------------------

    [Fact]
    public async Task TenSimultaneous401sShareOneCheckAndOneSignIn()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.Unauthorized, "{}") }.AlwaysSignedInAs();
        var harness = new Harness(vrchat, Account(), LimiterHarness.Unpaced());

        // The first session, before anything fails.
        await harness.Gate.ExecuteAsync(Members, Ok("warm"), ct: Ct);
        Assert.Equal(1, vrchat.GetCurrentUserCalls);

        // Hold the check open until all ten have had their 401, so they really are waiting on it
        // together rather than arriving one after another. Calls through one lane are issued one
        // at a time, and a call that arrives while the check holds the session waits for it
        // before it is sent at all -- so the ten may not all get that far; the fallback release
        // keeps the test from waiting on a count that scheduling did not reach.
        var refusedCount = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vrchat.VerifyAuthTokenGate = gate;
        _ = Task.Delay(TimeSpan.FromSeconds(1), Ct).ContinueWith(_ => gate.TrySetResult(), TaskScheduler.Default);

        var calls = Enumerable.Range(0, 10).Select(_ => Task.Run(() => harness.Gate.ExecuteAsync(
            Members,
            async (_, _) =>
            {
                await Task.Yield();

                if (vrchat.GetCurrentUserCalls >= 2)
                    return Response(HttpStatusCode.OK, "members");

                if (Interlocked.Increment(ref refusedCount) == 10)
                    gate.TrySetResult();

                return Response<string>(HttpStatusCode.Unauthorized);
            },
            ct: Ct), Ct)).ToList();

        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.True(refusedCount >= 2, $"only {refusedCount} of the calls were refused together");
        Assert.Equal(1, vrchat.VerifyAuthTokenCalls);
        Assert.Equal(2, vrchat.GetCurrentUserCalls);
        Assert.Equal(2, harness.SignIns.Attempts.Count);
    }

    [Fact]
    public async Task AfterASignInFailsTheNext401DoesNotSignInStraightAway()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.Unauthorized, "{}") }
            .SignedInAs()
            .RespondsWith(FakeVRChat.Status(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid Username/Email or Password"}}"""));

        var harness = new Harness(vrchat, Account(), LimiterHarness.Unpaced());
        await harness.Gate.ExecuteAsync(Members, Ok("warm"), ct: Ct);

        var first = await harness.Gate.ExecuteAsync(Members, Status<string>(HttpStatusCode.Unauthorized), ct: Ct);
        Assert.Equal(VRChatFailureKind.CredentialsRejected, first.Kind);
        Assert.Equal(2, vrchat.GetCurrentUserCalls);

        // The same refused password is not sent again on a producer's behalf.
        for (var i = 0; i < 5; i++)
        {
            var next = await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);
            Assert.False(next.Success);
        }

        Assert.Equal(2, vrchat.GetCurrentUserCalls);
    }

    // ---- The limit per hour ---------------------------------------------------------------

    [Fact]
    public async Task FourSignInsAnHourAreAllowedAndTheFifthWaitsForTheOldestToAgeOut()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.Unauthorized, "{}") }.AlwaysSignedInAs();
        var harness = new Harness(vrchat, Account(), LimiterHarness.Unpaced());
        var first = harness.Clock.UtcNow;

        // Four operator checks, ten minutes apart, each finding the session gone.
        for (var i = 0; i < 4; i++)
        {
            var signedIn = await harness.Gate.SignInAsync(Ct);
            Assert.True(signedIn.Success);
            harness.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.Equal(4, vrchat.GetCurrentUserCalls);

        var fifth = await harness.Gate.SignInAsync(Ct);

        Assert.False(fifth.Success);
        Assert.Equal(VRChatFailureKind.SignInWaiting, fifth.Kind);
        Assert.Equal(4, vrchat.GetCurrentUserCalls);

        var status = await harness.Gate.DescribeSignInAsync(Ct);
        Assert.Equal(VRChatSessionState.SignInWaiting, status.State);
        Assert.Equal(SignInWaitReason.SignInLimitReached, status.Wait!.Reason);
        Assert.Equal(first + TimeSpan.FromHours(1), status.Wait.RetryAt);

        // Nothing that needs a session is sent while it lasts, not even a check.
        var checks = vrchat.VerifyAuthTokenCalls;
        var refused = await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);
        Assert.True(refused.WasNotSent);
        Assert.Equal(VRChatFailureKind.SignInWaiting, refused.Kind);
        Assert.Equal(checks, vrchat.VerifyAuthTokenCalls);

        // Once the oldest is an hour old, there is room for one.
        harness.Clock.UtcNow = first + TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1);
        var after = await harness.Gate.SignInAsync(Ct);

        Assert.True(after.Success);
        Assert.Equal(5, vrchat.GetCurrentUserCalls);
        Assert.Null((await harness.Gate.DescribeSignInAsync(Ct)).Wait);
    }

    [Fact]
    public async Task ATwoFactorSignInIsNotStartedWithoutRoomToFinish()
    {
        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.Unauthorized, "{}") }.ChallengesWithTotp();
        var connection = Account() with { TotpSecret = "JBSWY3DPEHPK3PXP" };
        var harness = new Harness(vrchat, connection, LimiterHarness.Unpaced());

        // Two already spent this hour, by an earlier process.
        await harness.SignIns.RecordAttemptAsync(harness.Clock.UtcNow, "GetCurrentUser", Ct);
        await harness.SignIns.RecordAttemptAsync(harness.Clock.UtcNow, "GetCurrentUser", Ct);

        var result = await harness.Gate.SignInAsync(Ct);

        // Three are needed -- GetCurrentUser, Verify2FA, GetCurrentUser -- and two are left.
        Assert.Equal(VRChatFailureKind.SignInWaiting, result.Kind);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public void ConfigurationMayLowerTheLimitButNeverRaiseIt()
    {
        Assert.Equal(4, new RateLimitOptions().SignInsPerHour);
        Assert.Equal(4, new RateLimitOptions { SignInsPerHour = 10 }.SignInsPerHour);
        Assert.Equal(2, new RateLimitOptions { SignInsPerHour = 2 }.SignInsPerHour);
        Assert.Equal(1, new RateLimitOptions { SignInsPerHour = 0 }.SignInsPerHour);
    }

    [Fact]
    public void TheLimitWaitsForTheRightAttemptToAgeOut()
    {
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset[] attempts = [now.AddMinutes(-50), now.AddMinutes(-30), now.AddMinutes(-20), now.AddMinutes(-70)];

        Assert.Null(SignInBudget.Check(attempts, 4, 1, now));

        var wait = SignInBudget.Check(attempts, 4, 3, now);
        Assert.Equal(SignInWaitReason.SignInLimitReached, wait!.Reason);

        // Three in the window and three needed: two have to age out, so the second oldest decides.
        Assert.Equal(now.AddMinutes(-30).AddHours(1), wait.RetryAt);
    }

    // ---- A rate limit on signing in -------------------------------------------------------

    [Fact]
    public async Task A429OnSignInWaitsAnHourThroughARestartThenTriesOnce()
    {
        var clock = new FakeClock();
        var signIns = new MemorySignInStore();
        var vrchat = new FakeVRChat()
            .RespondsWith(FakeVRChat.Status(HttpStatusCode.TooManyRequests, """{"error":{"message":"Too many requests","status_code":429}}"""));

        var before = new Harness(vrchat, Account(), clock: clock, signIns: signIns);
        var limited = await before.Gate.SignInAsync(Ct);

        Assert.Equal(VRChatFailureKind.SignInWaiting, limited.Kind);
        Assert.Equal(new SignInWait(SignInWaitReason.RateLimitedByVRChat, clock.UtcNow + TimeSpan.FromHours(1)), signIns.Wait);
        var retryAt = signIns.Wait!.RetryAt;

        // A redeploy half an hour in. The new process must not cut the wait short.
        clock.Advance(TimeSpan.FromMinutes(30));
        var after = new Harness(vrchat, Account(), clock: clock, signIns: signIns);

        var status = await after.Gate.DescribeSignInAsync(Ct);
        Assert.Equal(VRChatSessionState.SignInWaiting, status.State);
        Assert.Equal(retryAt, status.Wait!.RetryAt);

        var refused = await after.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);
        Assert.True(refused.WasNotSent);
        Assert.Equal(VRChatFailureKind.SignInWaiting, refused.Kind);

        // Nor does an operator's deliberate attempt, or the timer.
        Assert.Equal(VRChatFailureKind.SignInWaiting, (await after.Gate.SignInAsync(Ct)).Kind);
        await after.Gate.ResumeAfterWaitAsync(Ct);
        Assert.Equal(1, vrchat.GetCurrentUserCalls);

        // After the hour, one attempt -- rate limited again, so another hour from that moment.
        clock.UtcNow = retryAt;
        vrchat.RespondsWith(FakeVRChat.Status(HttpStatusCode.TooManyRequests));
        await after.Gate.ResumeAfterWaitAsync(Ct);

        Assert.Equal(2, vrchat.GetCurrentUserCalls);
        Assert.Equal(retryAt + TimeSpan.FromHours(1), signIns.Wait!.RetryAt);

        await after.Gate.ResumeAfterWaitAsync(Ct);
        await after.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);
        Assert.Equal(2, vrchat.GetCurrentUserCalls);

        // And the hour after that, it works, and the wait is gone.
        clock.UtcNow = retryAt + TimeSpan.FromHours(1);
        vrchat.SignedInAs();
        await after.Gate.ResumeAfterWaitAsync(Ct);

        Assert.Equal(3, vrchat.GetCurrentUserCalls);
        Assert.Null(signIns.Wait);
        Assert.Equal(VRChatSessionState.Healthy, after.Gate.State);
    }

    [Fact]
    public async Task ARateLimitDuringTwoFactorAlsoStartsTheWait()
    {
        var vrchat = new FakeVRChat()
            .RespondsWith(FakeVRChat.Ok(new CurrentUser { RequiresTwoFactorAuth = ["totp"] }))
            .VerifiesWith(new ApiResponse<Verify2FAResult>(HttpStatusCode.TooManyRequests, new Multimap<string, string>(), null!, "{}"));

        var harness = new Harness(vrchat, Account() with { TotpSecret = "JBSWY3DPEHPK3PXP" });

        var result = await harness.Gate.SignInAsync(Ct);

        Assert.Equal(VRChatFailureKind.SignInWaiting, result.Kind);
        Assert.Equal(SignInWaitReason.RateLimitedByVRChat, harness.SignIns.Wait!.Reason);
        Assert.Equal(1, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task TheWaitIsLoggedOnceWhenItStartsAndOnceWhenSigningInWorksAgain()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var vrchat = new FakeVRChat().RespondsWith(FakeVRChat.Status(HttpStatusCode.TooManyRequests));
        var harness = new Harness(vrchat, Account(), logger: logger);

        await harness.Gate.SignInAsync(Ct);
        for (var i = 0; i < 20; i++)
            await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        // One line about the wait. The HTTP log's own line for the 429 is a separate stream.
        Assert.Single(sink.Events, e => e.Level == LogEventLevel.Warning
                                        && e.MessageTemplate.Text.StartsWith("VRChat rate limited signing in", StringComparison.Ordinal));

        harness.Clock.Advance(TimeSpan.FromHours(1));
        vrchat.SignedInAs();
        await harness.Gate.ResumeAfterWaitAsync(Ct);

        Assert.Single(sink.Events, e => e.Level == LogEventLevel.Information
                                        && e.MessageTemplate.Text.StartsWith("Signed in to VRChat again", StringComparison.Ordinal));
    }

    // ---- Cookies ----------------------------------------------------------------------------

    [Fact]
    public async Task CookiesVRChatReplacesAreStored()
    {
        var vrchat = new FakeVRChat().AlwaysSignedInAs();
        vrchat.Cookies.Clear();
        vrchat.Cookies.Add(new Cookie("auth", "oldCookie", "/", "api.vrchat.cloud"));
        var harness = new Harness(vrchat, Account(cookie: "oldCookie"));

        await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);
        Assert.Equal(0, harness.Store.SessionSaves);

        vrchat.Cookies.Clear();
        vrchat.Cookies.Add(new Cookie("auth", "newCookie", "/", "api.vrchat.cloud"));

        await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        Assert.Equal("newCookie", harness.Store.SavedAuthCookie);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
    }

    [Fact]
    public async Task ASessionForADifferentAccountIsDroppedWhenTheCredentialsChange()
    {
        var vrchat = new FakeVRChat().AlwaysSignedInAs();
        var harness = new Harness(vrchat, Account(cookie: "firstAccountsCookie") with { SessionAccount = "modbot@example.com" });

        await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        // The operator enters another account; the settings store no longer offers the old cookie.
        harness.Store.Connection = new VRChatConnection("other@example.com", "different");

        var result = await harness.Gate.SignInAsync(Ct);

        Assert.True(result.Success);
        Assert.Equal(1, vrchat.GetCurrentUserCalls);
        Assert.Equal("other@example.com", harness.Factory.Built[^1].Username);
    }

    [Fact]
    public async Task NeitherTheCookieNorTheTokenIsEverLogged()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        var vrchat = new FakeVRChat { AuthToken = (HttpStatusCode.Unauthorized, "{}") }.AlwaysSignedInAs();
        var harness = new Harness(vrchat, Account(cookie: "storedSecretCookie"), logger: logger);

        await harness.Gate.ExecuteAsync(Members, Ok("members"), ct: Ct);

        vrchat.AuthToken = (HttpStatusCode.OK, """{"ok":true,"token":"authcookie_secret"}""");
        await harness.Gate.ExecuteAsync(Members, Status<string>(HttpStatusCode.Unauthorized), ct: Ct);
        await harness.Gate.SignInAsync(Ct);

        Assert.NotEmpty(sink.Events);

        foreach (var e in sink.Events)
        {
            var text = e.RenderMessage() + " " + string.Join(" ", e.Properties.Values);
            Assert.DoesNotContain("storedSecretCookie", text, StringComparison.Ordinal);
            Assert.DoesNotContain("authCookieValue", text, StringComparison.Ordinal);
            Assert.DoesNotContain("authcookie_secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        }
    }

    // ---- Plumbing --------------------------------------------------------------------------

    private static Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> Ok<T>(T value) =>
        (_, _) => Task.FromResult(Response(HttpStatusCode.OK, value));

    private static Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> Status<T>(HttpStatusCode status, string raw = "") =>
        (_, _) => Task.FromResult(Response<T>(status, raw: raw));

    private static ApiResponse<T> Response<T>(HttpStatusCode status, T? data = default, string raw = "") =>
        new(status, new Multimap<string, string>(), data!, raw);

    private sealed class Harness
    {
        public Harness(
            FakeVRChat vrchat,
            VRChatConnection connection,
            RateLimitOptions? limits = null,
            FakeClock? clock = null,
            MemorySignInStore? signIns = null,
            ILogger? logger = null)
        {
            Clock = clock ?? new FakeClock();
            Factory = new FakeClientFactory(vrchat.Client);
            Store = new FakeConnectionStore(connection);
            SignIns = signIns ?? new MemorySignInStore();
            Limiter = new LimiterHarness(limits ?? LimiterHarness.Unpaced(), Clock);

            Gate = new VRChatGate(
                Factory, Store, Limiter.Limiter, Clock, new FakeMonotonicClock(),
                logger ?? new LoggerConfiguration().CreateLogger(),
                SignIns);
        }

        public FakeClock Clock { get; }
        public FakeClientFactory Factory { get; }
        public FakeConnectionStore Store { get; }
        public MemorySignInStore SignIns { get; }
        public LimiterHarness Limiter { get; }
        public VRChatGate Gate { get; }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }
}
