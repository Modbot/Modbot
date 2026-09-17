using Modbot.Companion.LogReading;

namespace Modbot.Companion.Tests.LogReading;

/// <summary>
/// Where VRChat's log is looked for: the Windows profile, the Proton prefix inside each Steam
/// layout Linux has, and the folder a person named in settings, which wins over all of them.
/// </summary>
public sealed class VRChatLogFoldersTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("modbot-home-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public void OnWindowsItIsTheProfilesLocalLowFolderAndNothingElse()
    {
        var only = Assert.Single(VRChatLogFolders.Candidates(@"C:\Users\rin", windows: true));

        Assert.Equal(Path.Combine(@"C:\Users\rin", "AppData", "LocalLow", "VRChat", "VRChat"), only);
    }

    [Fact]
    public void OnLinuxTheProtonPrefixInsideEachSteamLayoutComesFirstAndWineLast()
    {
        var candidates = VRChatLogFolders.Candidates("/home/lillith", windows: false);

        var tail = Path.Combine("AppData", "LocalLow", "VRChat", "VRChat");
        var proton = Path.Combine("steamapps", "compatdata", "438100", "pfx", "drive_c", "users", "steamuser", tail);

        Assert.Equal(Path.Combine("/home/lillith", ".local", "share", "Steam", proton), candidates[0]);
        Assert.Contains(Path.Combine("/home/lillith", ".steam", "steam", proton), candidates);
        Assert.Contains(Path.Combine("/home/lillith", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", proton), candidates);
        Assert.Contains(Path.Combine("/home/lillith", "snap", "steam", "common", ".local", "share", "Steam", proton), candidates);
        Assert.Equal(Path.Combine("/home/lillith", ".wine", "drive_c", "users", "lillith", tail), candidates[^1]);
    }

    [Fact]
    public void AFolderNamedInSettingsWinsWhetherOrNotItExistsYet()
    {
        var existing = Directory.CreateDirectory(Path.Combine(_home, "exists")).FullName;
        var named = Path.Combine(_home, "not-yet");

        Assert.Equal(named, VRChatLogFolders.Resolve($"  {named}  ", [existing]));
    }

    [Fact]
    public void WithNothingNamedTheFirstFolderThatExistsIsWatched()
    {
        var missing = Path.Combine(_home, "missing");
        var existing = Directory.CreateDirectory(Path.Combine(_home, "exists")).FullName;

        Assert.Equal(existing, VRChatLogFolders.Resolve(null, [missing, existing]));
    }

    /// <summary>
    /// So the window can name the place it is waiting for VRChat to write to, rather than saying
    /// nothing while a person wonders where it is looking.
    /// </summary>
    [Fact]
    public void WithNothingExistingTheLikeliestFolderIsWatched()
    {
        var first = Path.Combine(_home, "first");
        var second = Path.Combine(_home, "second");

        Assert.Equal(first, VRChatLogFolders.Resolve("", [first, second]));
    }
}
