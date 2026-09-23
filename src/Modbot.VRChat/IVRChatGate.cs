using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Session;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat;

/// <summary>
/// The single most important interface in the system. <strong>Nothing else may ever construct a
/// VRChat client.</strong>
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.1. Everything that reaches VRChat passes through here: the one authenticated session,
/// the hierarchical token buckets, the priority queue, the 401 re-login, the cold stop, and the
/// WAF classification. Each of those only works if it is unavoidable, which is why the rule is
/// absolute rather than a convention.
/// </para>
/// <para>
/// The callback returns <c>ApiResponse&lt;T&gt;</c>, so callers <strong>must</strong> use the
/// <c>...WithHttpInfoAsync</c> variants. A plain <c>await vrchat.Groups.GetGroupAsync(id)</c>
/// anywhere in the codebase is a review failure, not a shortcut (spec 4.1.1): the convenience
/// overloads discard the status code, and the status code is what the gate exists to react to.
/// </para>
/// </remarks>
public interface IVRChatGate
{
    /// <summary>What the gate would tell an operator about itself right now.</summary>
    VRChatSessionState State { get; }

    /// <summary>
    /// Issues one call, paced, prioritised and authenticated.
    /// </summary>
    /// <param name="endpoint">
    /// Which budget this call is drawn from. Named explicitly because a callback is an opaque
    /// delegate — the limiter cannot see which URL it will reach — and because naming it is the
    /// point at which spec 4.3.4's question has to have been asked.
    /// </param>
    /// <param name="call">The SDK call, which must be a <c>...WithHttpInfoAsync</c> variant.</param>
    /// <param name="priority">Interactive work preempts queued background sync.</param>
    Task<VRChatResult<T>> ExecuteAsync<T>(
        VRChatEndpoint endpoint,
        Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default);

    /// <summary>
    /// Establishes the session, or reports precisely why it could not be.
    /// </summary>
    /// <remarks>
    /// Used by onboarding's connection check (spec 7.1.1), which has to tell a WAF block apart
    /// from a timeout apart from bad credentials — a proxy fixes the first and none of the others.
    /// </remarks>
    /// <remarks>
    /// Checks a stored session before anything else, and signs in with the password only when
    /// VRChat has really rejected it -- within the sign-in limit and never during a wait
    /// (spec 4.1.2). An operator's deliberate change does not skip either.
    /// </remarks>
    Task<VRChatResult<CurrentUserLoginResponse>> SignInAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether Modbot is waiting to sign in, when it last signed in, and how much of the hour's
    /// sign-in limit is used (spec 4.1.2).
    /// </summary>
    /// <remarks>
    /// Reads the stored wait on first use, so a health read straight after a restart already shows
    /// a wait the previous process started. Never waits behind a sign-in in progress.
    /// </remarks>
    Task<SignInStatus> DescribeSignInAsync(CancellationToken ct = default);

    /// <summary>
    /// Makes the one attempt that follows a wait, once the wait has ended. Does nothing otherwise.
    /// </summary>
    /// <remarks>
    /// Called on a timer, so the banner clears on its own when a deployment has nothing else asking
    /// VRChat for anything -- during setup, say.
    /// </remarks>
    Task ResumeAfterWaitAsync(CancellationToken ct = default);

    /// <summary>Per-bucket health, for the UI (spec 4.3.3).</summary>
    Task<IReadOnlyList<RateLimitBucketHealth>> DescribeBucketsAsync(CancellationToken ct = default);

    /// <summary>
    /// Forwards one request to VRChat as it was written, paced and -- on the service account --
    /// authenticated, and returns whatever VRChat answered (VRChat proxy design).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one call on the gate that is not an SDK call, and the reason it is on the gate at all:
    /// the request goes out on the same session, through the same limiter, with the same
    /// User-Agent, and a 429 cold stops its bucket like any other. Nothing outside the gate holds
    /// the session cookie, so nothing outside the gate can send it.
    /// </para>
    /// <para>
    /// A successful result holds VRChat's answer whatever its status: a 404 from VRChat is a
    /// response, not a failure. A failure is the gate declining to send -- a cold stop, no
    /// session, a wait to sign in -- or the transport failing, and carries no response.
    /// </para>
    /// </remarks>
    /// <param name="endpoint">The proxy class the request is paced on: <c>proxy</c> or <c>proxy.passthrough</c>.</param>
    /// <param name="request">What the caller sent, already stripped of anything that is theirs alone.</param>
    /// <param name="account">Whose session it goes out on.</param>
    Task<VRChatResult<Proxy.VRChatProxyResponse>> ForwardAsync(
        VRChatEndpoint endpoint,
        Proxy.VRChatProxyRequest request,
        Proxy.VRChatProxyAccount account,
        VRChatCallPriority priority = VRChatCallPriority.Interactive,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches one picture or video from VRChat, on the session, and follows VRChat's redirect to
    /// whichever delivery host it names (VRChat files design).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not paced, and not on a bucket.</strong> Every other call here draws from a budget
    /// because spec 4.3.4 says a new endpoint gets its rate limit asked about first; the answer
    /// for the file and image addresses, on 2026-09-17, was that VRChat does not limit them. So
    /// there is no endpoint class, no lane and no cold stop for these, and one should not be
    /// added by analogy with the calls that do have one: a member list of forty faces would then
    /// queue behind itself for nothing.
    /// </para>
    /// <para>
    /// It is on the gate all the same, and for the rule's real reason: the fetch needs the
    /// session cookie, the gate is the only thing that holds one, and the request has to leave
    /// through the operator's egress proxy like everything else Modbot sends VRChat.
    /// </para>
    /// <para>
    /// The address must be one of VRChat's (<see cref="Files.VRChatFiles.IsVRChatAddress(Uri)"/>)
    /// and so must every address it redirects to; anything else fails rather than being fetched.
    /// </para>
    /// </remarks>
    /// <param name="url">An absolute VRChat file address.</param>
    Task<Files.VRChatFileResult> FetchFileAsync(Uri url, CancellationToken ct = default);
}
