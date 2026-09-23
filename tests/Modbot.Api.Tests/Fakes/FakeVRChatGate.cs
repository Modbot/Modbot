using Modbot.VRChat;
using Modbot.VRChat.Files;
using Modbot.VRChat.Proxy;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Session;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.Api.Tests.Fakes;

/// <summary>
/// A gate that answers from a script instead of from the internet.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than a substitute because <see cref="IVRChatGate"/> has three members and
/// the interesting one is generic over the response type — which a mocking framework expresses
/// far less clearly than a dictionary of prepared answers does.
/// </para>
/// <para>
/// The callback is deliberately never invoked. What it would do is call the VRChat SDK, and the
/// SDK's behaviour is pinned by <c>SdkContractTests</c> in the gate's own suite; re-proving it
/// here would mean standing up an HTTP server to test an endpoint's error messages.
/// </para>
/// </remarks>
public sealed class FakeVRChatGate : IVRChatGate
{
    private readonly Dictionary<string, object> _responses = new(StringComparer.Ordinal);

    public VRChatSessionState State { get; set; } = VRChatSessionState.Unconfigured;

    /// <summary>What <see cref="SignInAsync"/> returns. Defaults to "nothing configured".</summary>
    public VRChatResult<CurrentUserLoginResponse> SignIn { get; set; } =
        VRChatResult<CurrentUserLoginResponse>.Failure(
            0, "No VRChat account is configured.", kind: VRChatFailureKind.NotConfigured);

    /// <summary>
    /// Every call the code under test made, in order, with the priority it asked for. The
    /// priority is recorded because "a human is waiting on this" is a claim a slice makes and can
    /// therefore get wrong (spec 4.3.3).
    /// </summary>
    public List<(VRChatEndpoint Endpoint, VRChatCallPriority Priority)> Calls { get; } = [];

    public int SignInCalls { get; private set; }

    public FakeVRChatGate SignedInAs(string displayName = "Modbot", string userId = "usr_modbot")
    {
        SignIn = VRChatResult<CurrentUserLoginResponse>.Ok(CurrentUserNamed(displayName, userId), 200);
        State = VRChatSessionState.Healthy;
        return this;
    }

    /// <summary>Scripts one operation's answer, keyed by <see cref="VRChatEndpoint.Operation"/>.</summary>
    public FakeVRChatGate Returns<T>(string operation, VRChatResult<T> result)
    {
        _responses[operation] = result;
        return this;
    }

    public FakeVRChatGate Returns<T>(string operation, T value) =>
        Returns(operation, VRChatResult<T>.Ok(value, 200));

    public Task<VRChatResult<T>> ExecuteAsync<T>(
        VRChatEndpoint endpoint,
        Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default)
    {
        Calls.Add((endpoint, priority));

        var key = endpoint.Operation ?? endpoint.Class;

        if (_responses.TryGetValue(key, out var scripted) && scripted is VRChatResult<T> typed)
            return Task.FromResult(typed);

        return Task.FromResult(VRChatResult<T>.Failure(
            0, $"Nothing scripted for {key}.", kind: VRChatFailureKind.Other));
    }

    public Task<VRChatResult<CurrentUserLoginResponse>> SignInAsync(CancellationToken ct = default)
    {
        SignInCalls++;
        return Task.FromResult(SignIn);
    }

    public Task<IReadOnlyList<RateLimitBucketHealth>> DescribeBucketsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RateLimitBucketHealth>>([]);

    /// <summary>What <see cref="DescribeSignInAsync"/> returns. Null means "not waiting, never signed in".</summary>
    public SignInStatus? SignInStatus { get; set; }

    public Task<SignInStatus> DescribeSignInAsync(CancellationToken ct = default) =>
        Task.FromResult(SignInStatus ?? new SignInStatus(State, null, null, 0, 4, DateTimeOffset.UnixEpoch));

    /// <summary>A gate waiting to sign in until <paramref name="retryAt"/>, as seen at <paramref name="now"/>.</summary>
    public FakeVRChatGate WaitingToSignIn(DateTimeOffset now, DateTimeOffset retryAt,
        SignInWaitReason reason = SignInWaitReason.RateLimitedByVRChat)
    {
        State = VRChatSessionState.SignInWaiting;
        SignInStatus = new SignInStatus(State, new SignInWait(reason, retryAt), null, 4, 4, now);
        SignIn = VRChatResult<CurrentUserLoginResponse>.Failure(
            0, "Waiting to sign in to VRChat.", kind: VRChatFailureKind.SignInWaiting);
        return this;
    }

    public Task ResumeAfterWaitAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Every forwarded request, with the account it was to go out on.</summary>
    public List<(VRChatEndpoint Endpoint, VRChatProxyRequest Request, VRChatProxyAccount Account)> Forwarded { get; } = [];

    /// <summary>What <see cref="ForwardAsync"/> answers. Defaults to a 200 with an empty JSON object.</summary>
    public VRChatResult<VRChatProxyResponse> Forward { get; set; } =
        VRChatResult<VRChatProxyResponse>.Ok(
            new VRChatProxyResponse(
                200,
                [new KeyValuePair<string, string>("Content-Type", "application/json")],
                "{}"u8.ToArray(),
                "application/json"),
            200);

    public Task<VRChatResult<VRChatProxyResponse>> ForwardAsync(
        VRChatEndpoint endpoint,
        VRChatProxyRequest request,
        VRChatProxyAccount account,
        VRChatCallPriority priority = VRChatCallPriority.Interactive,
        CancellationToken ct = default)
    {
        Forwarded.Add((endpoint, request, account));
        return Task.FromResult(Forward);
    }

    /// <summary>Every file address the code under test asked for, in order.</summary>
    public List<Uri> Fetched { get; } = [];

    /// <summary>
    /// What <see cref="FetchFileAsync"/> answers. Defaults to "nothing configured", which is
    /// also what a demo's gate says, so a test that scripts nothing is testing the miss.
    /// </summary>
    public VRChatFileResult Fetch { get; set; } =
        VRChatFileResult.Problems(VRChatFileOutcome.NoSession, "No VRChat account is configured.");

    /// <summary>Scripts one picture for every address asked for.</summary>
    public FakeVRChatGate Serves(byte[] bytes, string contentType = "image/png")
    {
        Fetch = VRChatFileResult.Ok(new VRChatFile(bytes, contentType));
        return this;
    }

    /// <summary>Scripts a refusal, so a test can check how the endpoint answers one.</summary>
    public FakeVRChatGate Refuses(VRChatFileOutcome outcome, string problem = "No.")
    {
        Fetch = VRChatFileResult.Problems(outcome, problem);
        return this;
    }

    public Task<VRChatFileResult> FetchFileAsync(Uri url, CancellationToken ct = default)
    {
        Fetched.Add(url);
        return Task.FromResult(Fetch);
    }

    /// <summary>
    /// <c>CurrentUserLoginResponse</c> has around a hundred properties, so it is built by setting
    /// the two anything here reads.
    /// </summary>
    private static CurrentUserLoginResponse CurrentUserNamed(string displayName, string userId) =>
        new() { DisplayName = displayName, Id = userId };
}
