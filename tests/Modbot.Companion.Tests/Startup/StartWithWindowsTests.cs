using Modbot.Companion.Presentation;
using Modbot.Companion.Startup;

namespace Modbot.Companion.Tests.Startup;

public class StartWithWindowsTests
{
    private const string Launcher = @"C:\Users\rin\AppData\Local\Modbot\current\Modbot.exe";

    private sealed class FakeRegistry : IStartupRegistry
    {
        public string? Command { get; set; }

        public bool TurnedOff { get; set; }

        public int Reads { get; private set; }

        public int Writes { get; private set; }

        public string? ReadCommand()
        {
            Reads++;
            return Command;
        }

        public void WriteCommand(string command)
        {
            Writes++;
            Command = command;
        }

        public void DeleteCommand()
        {
            Writes++;
            Command = null;
        }

        public bool IsTurnedOffInWindows()
        {
            Reads++;
            return TurnedOff;
        }
    }

    [Fact]
    public void OnByDefaultWritesTheEntry()
    {
        var registry = new FakeRegistry();

        Assert.True(CompanionSettings.Default.StartWithWindows);
        var state = new StartWithWindows(registry).Apply(installed: true, Launcher, CompanionSettings.Default.StartWithWindows);

        Assert.Equal(new StartupState(true, true, false), state);
        Assert.Equal($"\"{Launcher}\" --autostart", registry.Command);
    }

    [Fact]
    public void OffRemovesTheEntry()
    {
        var registry = new FakeRegistry { Command = $"\"{Launcher}\" --autostart" };

        var state = new StartWithWindows(registry).Apply(installed: true, Launcher, wanted: false);

        Assert.Equal(new StartupState(true, false, false), state);
        Assert.Null(registry.Command);
    }

    [Fact]
    public void TurnedOffInWindowsIsLeftOffAndShownOff()
    {
        var registry = new FakeRegistry { TurnedOff = true };

        var state = new StartWithWindows(registry).Apply(installed: true, Launcher, wanted: true);

        Assert.Equal(new StartupState(true, false, true), state);
        Assert.Null(registry.Command);
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public void ThePathSurvivesAnUpdate()
    {
        var registry = new FakeRegistry();
        var startup = new StartWithWindows(registry);

        // Velopack replaces the files inside "current" on update; the launcher's path does not change,
        // so the next start after an update finds the entry already right and writes nothing.
        startup.Apply(installed: true, Launcher, wanted: true);
        startup.Apply(installed: true, Launcher, wanted: true);

        Assert.Equal(1, registry.Writes);
        Assert.DoesNotContain("app-", registry.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AStaleEntryIsFixedByTheInstalledClient()
    {
        var registry = new FakeRegistry { Command = "\"D:\\old\\copy\\Modbot.exe\" --autostart" };

        new StartWithWindows(registry).Apply(installed: true, Launcher, wanted: true);

        Assert.Equal($"\"{Launcher}\" --autostart", registry.Command);
    }

    [Fact]
    public void NotInstalledIsHiddenAndNeverTouchesTheRegistry()
    {
        var registry = new FakeRegistry { Command = "\"D:\\old\\copy\\Modbot.exe\" --autostart" };

        var state = new StartWithWindows(registry).Apply(installed: false, launcherPath: null, wanted: true);

        Assert.Equal(StartupState.Hidden, state);
        Assert.Equal(0, registry.Reads);
        Assert.Equal(0, registry.Writes);
        Assert.Equal("\"D:\\old\\copy\\Modbot.exe\" --autostart", registry.Command);
    }

    [Fact]
    public void TheStartupArgumentStartsHidden()
    {
        Assert.True(StartWithWindows.StartsHidden(["--autostart"]));
        Assert.False(StartWithWindows.StartsHidden([]));
        Assert.False(StartWithWindows.StartsHidden(["modbot-companion://pair?token=x"]));
    }

    [Fact]
    public void TheSettingIsSavedLikeTheOthers()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("modbot-startup-").FullName, "settings.json");

        Assert.True(CompanionSettings.Load(path).StartWithWindows);
        Assert.True(CompanionSettings.SaveSwitch(path, CompanionSettings.StartWithWindowsField, false));
        Assert.False(CompanionSettings.Load(path).StartWithWindows);
        Assert.False(CompanionSettings.Load(path).Cloud.Disabled);

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
