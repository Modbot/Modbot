using System.Net;
using NSubstitute;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// Invites to an instance -- <c>POST /invite/{userId}</c> -- answered the way VRChat answers them:
/// accepted for a friend, 403 "You need to be friends with that user first" for anybody else.
/// </summary>
public sealed class FakeInvites
{
    /// <summary>Every invite asked for, in order: who, and the location sent as its "instanceId".</summary>
    public List<(string UserId, string Location)> Sent { get; } = [];

    /// <summary>People the account is friends with. Everybody else is refused with a 403.</summary>
    public HashSet<string> Friends { get; } = new(StringComparer.Ordinal);

    /// <summary>A status to answer for one person instead, such as 429.</summary>
    public Dictionary<string, HttpStatusCode> Status { get; } = new(StringComparer.Ordinal);

    public FakeInvites FriendsWith(params string[] userIds)
    {
        Friends.UnionWith(userIds);
        return this;
    }

    public IInviteApi Build()
    {
        var invites = Substitute.For<IInviteApi>();

        invites
            .InviteUserWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<InviteRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var userId = call.ArgAt<string>(0);
                Sent.Add((userId, call.ArgAt<InviteRequest>(1).InstanceId));

                var status = Status.TryGetValue(userId, out var set)
                    ? set
                    : Friends.Contains(userId) ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

                var raw = status switch
                {
                    HttpStatusCode.OK => "{}",
                    HttpStatusCode.Forbidden => "{\"error\":{\"message\":\"\\\"You need to be friends with that user first.\\\"\",\"status_code\":403}}",
                    _ => "{\"error\":{\"message\":\"no\"}}",
                };

                return Task.FromResult(new ApiResponse<SentNotification>(status, new Multimap<string, string>(), null!, raw));
            });

        return invites;
    }
}
