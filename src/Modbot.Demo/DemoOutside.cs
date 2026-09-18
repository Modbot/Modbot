using Modbot.Core.Discord;
using Modbot.Core.Email;
using Modbot.Core.Time;
using Modbot.VRChat;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Session;
using VRChat.API.Client;
using VRChat.API.Model;

// The demo's stand-ins for everything outside Modbot: the VRChat gate, the mail relay and the
// Discord messenger. See DemoVRChatGate's remarks for why they are replaced rather than checked for.

namespace Modbot.Demo;

/// <summary>
/// The gate a demo gets: it reaches VRChat for nothing.
/// </summary>
/// <remarks>
/// <para>
/// A demo talks to nothing (demo mode design §3.2). Rather than dot the codebase with "unless this
/// is a demo" checks, the three ways out are replaced at the door: the VRChat gate, the mail relay
/// and the Discord messenger. Every caller already handles "not set up", because an unconfigured
/// deployment is the ordinary first state of a real one — so nothing else has to change, and no
/// future caller can find a way round.
/// </para>
/// <para>
/// The Discord bot is not replaced but simply never registered by the host, because it connects on
/// its own schedule rather than through an interface anybody calls.
/// </para>
/// </remarks>
public sealed class DemoVRChatGate : IVRChatGate
{
    /// <summary>What every refusal says.</summary>
    public const string Message = "VRChat is off in the demo.";

    private readonly IModbotClock _clock;

    public DemoVRChatGate(IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>
    /// Unconfigured, which is the truth: a demo has no VRChat account and never signs in to one.
    /// </summary>
    public VRChatSessionState State => VRChatSessionState.Unconfigured;

    public Task<VRChatResult<T>> ExecuteAsync<T>(
        VRChatEndpoint endpoint,
        Func<IVRChat, CancellationToken, Task<ApiResponse<T>>> call,
        VRChatCallPriority priority = VRChatCallPriority.Background,
        CancellationToken ct = default)
        => Task.FromResult(VRChatResult<T>.Failure(0, Message, kind: VRChatFailureKind.NotConfigured));

    public Task<VRChatResult<CurrentUser>> SignInAsync(CancellationToken ct = default)
        => Task.FromResult(VRChatResult<CurrentUser>.Failure(0, Message, kind: VRChatFailureKind.NotConfigured));

    public Task<SignInStatus> DescribeSignInAsync(CancellationToken ct = default)
        => Task.FromResult(new SignInStatus(
            VRChatSessionState.Unconfigured, null, null, 0, 0, _clock.UtcNow));

    public Task ResumeAfterWaitAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<RateLimitBucketHealth>> DescribeBucketsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RateLimitBucketHealth>>([]);

    public Task<VRChatResult<Modbot.VRChat.Proxy.VRChatProxyResponse>> ForwardAsync(
        VRChatEndpoint endpoint,
        Modbot.VRChat.Proxy.VRChatProxyRequest request,
        Modbot.VRChat.Proxy.VRChatProxyAccount account,
        VRChatCallPriority priority = VRChatCallPriority.Interactive,
        CancellationToken ct = default)
        => Task.FromResult(VRChatResult<Modbot.VRChat.Proxy.VRChatProxyResponse>.Failure(
            0, Message, kind: VRChatFailureKind.NotConfigured));
}

/// <summary>Sends no email, and says so.</summary>
public sealed class DemoMailRelay : IMailRelay
{
    public const string Message = "Email is off in the demo.";

    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<SendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default)
        => Task.FromResult(SendOutcome.Failed(Message));
}

/// <summary>Sends no Discord message, and says so.</summary>
public sealed class DemoDiscordMessenger : IDiscordMessenger
{
    public const string Message = "Discord is off in the demo.";

    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<SendOutcome> SendDirectMessageAsync(string discordUserId, string text, CancellationToken ct = default)
        => Task.FromResult(SendOutcome.Failed(Message));
}
