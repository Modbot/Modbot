using System.Net;
using System.Runtime.CompilerServices;
using NSubstitute;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// Rooms' own pages -- <c>GET /instances/{worldId}:{instanceId}</c> -- served the way VRChat serves
/// them, including the trap: a room nobody scripted answers <c>200</c> with <c>active: false</c>,
/// exactly as a room that never existed does (research: vrchat-instance-findings.md section 1).
/// </summary>
public sealed class FakeInstances
{
    private readonly Dictionary<string, (HttpStatusCode Status, Instance? Body)> _pages = new(StringComparer.Ordinal);

    /// <summary>Every location asked for, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>What a live room's page says.</summary>
    public FakeInstances Page(string location, int nUsers, int userCount, bool active = true)
    {
        _pages[location] = (HttpStatusCode.OK, Body(active, nUsers, userCount));
        return this;
    }

    /// <summary>A status other than 200 for this room's page -- 429 for a limit, 500 for trouble.</summary>
    public FakeInstances Status(string location, HttpStatusCode status)
    {
        _pages[location] = (status, null);
        return this;
    }

    /// <summary>
    /// An instance body with only the fields the head count reads. Built without its constructor,
    /// which demands every one of the fifty-odd required fields a real response carries.
    /// </summary>
    public static Instance Body(bool active, int nUsers, int userCount)
    {
        var instance = (Instance)RuntimeHelpers.GetUninitializedObject(typeof(Instance));
        instance.Active = active;
        instance.NUsers = nUsers;
        instance.UserCount = userCount;
        return instance;
    }

    public IInstancesApi Build()
    {
        var instances = Substitute.For<IInstancesApi>();

        instances
            .GetInstanceWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var location = $"{call.ArgAt<string>(0)}:{call.ArgAt<string>(1)}";
                Requests.Add(location);

                var (status, body) = _pages.TryGetValue(location, out var page)
                    ? page
                    : (HttpStatusCode.OK, Body(active: false, nUsers: 0, userCount: 0));

                return Task.FromResult(new ApiResponse<Instance>(status, new Multimap<string, string>(), body!, "{}"));
            });

        return instances;
    }
}
