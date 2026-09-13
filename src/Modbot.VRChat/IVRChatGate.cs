using Modbot.VRChat.RateLimiting;
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
    Task<VRChatResult<CurrentUser>> SignInAsync(CancellationToken ct = default);

    /// <summary>Per-bucket health, for the UI (spec 4.3.3).</summary>
    Task<IReadOnlyList<RateLimitBucketHealth>> DescribeBucketsAsync(CancellationToken ct = default);
}
