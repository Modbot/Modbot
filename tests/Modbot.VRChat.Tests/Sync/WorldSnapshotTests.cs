using System.Runtime.CompilerServices;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>What a world read keeps: its platforms, for the game's PC, Android and iOS badges.</summary>
public class WorldSnapshotTests
{
    [Fact]
    public void AWorldsPlatforms_AreKeptOnceEach_InASteadyOrder()
    {
        var world = World("wrld_a", "standalonewindows", "android", "standalonewindows", "ios");

        var row = new VRChatWorld { WorldId = "wrld_a" };
        WorldSnapshot.From(world).ApplyTo(row, DateTimeOffset.UnixEpoch);

        Assert.Equal("""["android","ios","standalonewindows"]""", row.Platforms);
    }

    /// <summary>A read with no builds in it says nothing about them, so it does not wipe what an earlier read kept.</summary>
    [Fact]
    public void AReadWithNoBuilds_LeavesThePlatformsAlone()
    {
        var row = new VRChatWorld { WorldId = "wrld_a", Platforms = """["standalonewindows"]""" };

        WorldSnapshot.From(World("wrld_a")).ApplyTo(row, DateTimeOffset.UnixEpoch);

        Assert.Equal("""["standalonewindows"]""", row.Platforms);
    }

    /// <summary>A world with only the fields the tests read, built without its many required ones.</summary>
    private static World World(string id, params string[] platforms)
    {
        var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
        world.Id = id;
        world.Name = "The Black Cat";
        world.UnityPackages = platforms
            .Select(p =>
            {
                var package = (UnityPackage)RuntimeHelpers.GetUninitializedObject(typeof(UnityPackage));
                package.Platform = p;
                return package;
            })
            .ToList();

        return world;
    }
}
