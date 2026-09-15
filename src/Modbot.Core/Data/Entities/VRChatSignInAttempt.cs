namespace Modbot.Core.Data.Entities;

/// <summary>
/// One request that could count as a sign-in to VRChat, and when it was sent (foundation spec 4.1.2).
/// </summary>
/// <remarks>
/// <para>
/// VRChat allows about four or five sign-ins an hour and answers the next with an hour-long block.
/// Modbot keeps to at most four per rolling hour, and it counts them here rather than in memory so
/// that a redeploy, a crash or a crash loop cannot hand the allowance back.
/// </para>
/// <para>
/// Written <em>before</em> the request is sent. A process that dies mid-request has still spent
/// the attempt as far as VRChat is concerned, so it has to be spent here too.
/// </para>
/// </remarks>
public class VRChatSignInAttempt
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>Which request it was: <c>GetCurrentUser</c> or <c>Verify2FA</c>. For people reading the table.</summary>
    public string Operation { get; set; } = string.Empty;
}
