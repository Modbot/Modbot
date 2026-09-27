using System.Net;
using System.Runtime.CompilerServices;
using NSubstitute;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// World pages -- <c>GET /worlds/{worldId}</c> -- as the body arrives. The world head count read looks
/// only at the body, so a scripted page is its raw text and the SDK's model is left empty.
/// </summary>
public sealed class FakeWorlds
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Raw)> _pages = new(StringComparer.Ordinal);

    /// <summary>Every world asked for, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>What the world's page says: the body exactly as VRChat would send it.</summary>
    public FakeWorlds Page(string worldId, string raw)
    {
        _pages[worldId] = (HttpStatusCode.OK, raw);
        return this;
    }

    /// <summary>A status other than 200 for this world's page -- 429 for a limit, 500 for trouble.</summary>
    public FakeWorlds Status(string worldId, HttpStatusCode status)
    {
        _pages[worldId] = (status, "{}");
        return this;
    }

    public IWorldsApi Build()
    {
        var worlds = Substitute.For<IWorldsApi>();

        worlds
            .GetWorldWithHttpInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var worldId = call.ArgAt<string>(0);
                Requests.Add(worldId);

                var (status, raw) = _pages.TryGetValue(worldId, out var page)
                    ? page
                    : (HttpStatusCode.NotFound, "{\"error\":{\"message\":\"World not found\"}}");

                // Built without its constructor, which demands every required field a real body carries.
                var body = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));

                return Task.FromResult(new ApiResponse<World>(status, new Multimap<string, string>(), body, raw));
            });

        return worlds;
    }
}
