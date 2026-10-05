using System.Text.Json.Nodes;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Presentation;
using Modbot.Companion.Startup;

namespace Modbot.Companion.Tests.CloudBackup;

/// <summary>
/// The <c>cloud</c> object in <c>settings.json</c>: the switch and what is sent are read from it
/// and written back to it, and nothing else in it or in the file is touched by either write.
/// </summary>
public sealed class CloudSettingsFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-cloud-settings-file-").FullName;

    private string File_ => Path.Combine(_directory, "settings.json");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string? NoEnvironment(string name) => null;

    private static Func<string, string?> Environment(string name, string value) =>
        asked => asked == name ? value : null;

    private void Write(string json) => File.WriteAllText(File_, json);

    private JsonObject Read() => JsonNode.Parse(File.ReadAllText(File_))!.AsObject();

    private CloudSettings Load(Func<string, string?>? environment = null)
        => CompanionSettings.Load(File_, environment ?? NoEnvironment).Cloud;

    [Fact]
    public void AnInstallThatNeverChoseAnythingSendsEverythingAsItAlwaysDid()
    {
        // No file, a file with no cloud object, and one with only an address: all the same choices.
        Assert.Equal(CloudChoices.Default, Load().Choices);

        Write("""{ "pairingPage": "https://cats.example/pair" }""");
        Assert.Equal(CloudChoices.Default, Load().Choices);

        Write("""{ "cloud": { "endpoint": "https://cloud.group.example" } }""");
        Assert.Equal(CloudChoices.Default, Load().Choices);
        Assert.False(Load().Disabled);
        Assert.False(Load().SwitchLocked);
    }

    [Fact]
    public void TheChoicesAreReadFromTheFile()
    {
        Write("""
            { "cloud": {
                "group": { "events": ["joined", "left"], "worldId": false, "groupId": false },
                "nonGroup": { "events": ["stoppedLogging"], "instanceId": false, "avatarName": false }
            } }
            """);

        var choices = Load().Choices;

        Assert.Equal(CloudEventKinds.Joined | CloudEventKinds.Left, choices.Group.Events);
        Assert.False(choices.Group.WorldId);
        Assert.True(choices.Group.InstanceId);
        Assert.False(choices.Group.GroupId);
        Assert.Equal(CloudEventKinds.StoppedLogging, choices.NonGroup.Events);
        Assert.False(choices.NonGroup.InstanceId);
        Assert.False(choices.NonGroup.AvatarName);
        Assert.True(choices.NonGroup.WorldId);
    }

    [Fact]
    public void AFileWithNoEventOnInEitherSectionLeavesNothingToSend()
    {
        Write("""{ "cloud": { "group": { "events": [] }, "nonGroup": { "events": [] } } }""");

        Assert.False(Load().Choices.AnyEventOn);
        Assert.False(Load().Disabled);
    }

    [Fact]
    public void TheEnvironmentVariableWinsAndLocksTheBox()
    {
        Write("""{ "cloud": { "disabled": false } }""");

        var off = Load(Environment(CloudSettings.DisabledVariable, "1"));
        Assert.True(off.Disabled);
        Assert.True(off.SwitchLocked);

        Write("""{ "cloud": { "disabled": true } }""");

        var on = Load(Environment(CloudSettings.DisabledVariable, "false"));
        Assert.False(on.Disabled);
        Assert.True(on.SwitchLocked);

        // A word that is neither leaves the file in charge, so a typo never turns sending back on.
        var typo = Load(Environment(CloudSettings.DisabledVariable, "maybe"));
        Assert.True(typo.Disabled);
        Assert.False(typo.SwitchLocked);
    }

    [Fact]
    public void ATestCopyShowsTheBoxOffAndLocked()
    {
        var test = new DataFolder(_directory, IsTestCopy: true).CloudFor(CloudSettings.Default);
        var real = new DataFolder(_directory, IsTestCopy: false).CloudFor(CloudSettings.Default);

        Assert.True(test.Disabled);
        Assert.True(test.SwitchLocked);
        Assert.False(real.Disabled);
        Assert.False(real.SwitchLocked);
    }

    [Fact]
    public void SwitchingItOffWritesOnlyTheSwitchAndKeepsTheAddress()
    {
        Write("""
            { "pairingPage": "https://cats.example/pair",
              "voice": { "on": true },
              "cloud": { "endpoint": "https://cloud.group.example", "somethingElse": 7 } }
            """);

        Assert.True(CompanionSettings.SaveCloudDisabled(File_, disabled: true));

        var root = Read();
        Assert.Equal("https://cats.example/pair", root["pairingPage"]!.GetValue<string>());
        Assert.True(root["voice"]!["on"]!.GetValue<bool>());

        var cloud = root["cloud"]!.AsObject();
        Assert.True(cloud["disabled"]!.GetValue<bool>());
        Assert.Equal("https://cloud.group.example", cloud["endpoint"]!.GetValue<string>());
        Assert.Equal(7, cloud["somethingElse"]!.GetValue<int>());
        Assert.False(cloud.ContainsKey("group"));
        Assert.False(cloud.ContainsKey("nonGroup"));
    }

    [Fact]
    public void SavingTheChoicesWritesOnlyTheTwoObjects()
    {
        Write("""
            { "overlayOn": false,
              "cloud": { "endpoint": "https://cloud.group.example", "disabled": true } }
            """);

        var choices = CloudChoices.Default
            .WithEvent(inGroup: true, CloudEventKinds.Left, on: false).Choices
            .WithDetail(inGroup: false, CloudDetail.WorldId, on: false);

        Assert.True(CompanionSettings.SaveCloudChoices(File_, choices));

        var root = Read();
        Assert.False(root["overlayOn"]!.GetValue<bool>());

        var cloud = root["cloud"]!.AsObject();
        Assert.Equal("https://cloud.group.example", cloud["endpoint"]!.GetValue<string>());
        Assert.True(cloud["disabled"]!.GetValue<bool>());

        // Read back, it is what was chosen.
        Assert.Equal(choices, Load().Choices);
        Assert.True(Load().Disabled);
        Assert.Equal(new Uri("https://cloud.group.example"), Load().Endpoint);
    }

    [Fact]
    public void ThePageCreatesTheCloudObjectWhenTheFileHasNone()
    {
        Assert.True(CompanionSettings.SaveCloudDisabled(File_, disabled: false));
        Assert.False(Read()["cloud"]!["disabled"]!.GetValue<bool>());

        File.Delete(File_);
        Write("""{ "checkForUpdates": false }""");

        Assert.True(CompanionSettings.SaveCloudChoices(File_, CloudChoices.Default));

        var root = Read();
        Assert.False(root["checkForUpdates"]!.GetValue<bool>());
        Assert.NotNull(root["cloud"]!["group"]);
        Assert.NotNull(root["cloud"]!["nonGroup"]);
    }

    [Fact]
    public void AFileThatCannotBeReadIsLeftAlone()
    {
        const string notJson = "{ this is not json";
        Write(notJson);

        Assert.False(CompanionSettings.SaveCloudDisabled(File_, disabled: true));
        Assert.False(CompanionSettings.SaveCloudChoices(File_, CloudChoices.Default));
        Assert.Equal(notJson, File.ReadAllText(File_));

        // And a file whose cloud is not an object is not made into one.
        const string odd = """{ "cloud": "https://cloud.group.example" }""";
        Write(odd);

        Assert.False(CompanionSettings.SaveCloudDisabled(File_, disabled: true));
        Assert.Equal(odd, File.ReadAllText(File_));
    }

    [Fact]
    public void AnUnreadableFileMeansDefaultsSoTheBackupDoesNotQuietlyStop()
    {
        Write("{ this is not json");

        Assert.Equal(CloudSettings.Default.Choices, Load().Choices);
        Assert.False(Load().Disabled);
    }
}
