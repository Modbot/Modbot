using System.Text.Json.Nodes;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Instances;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.CloudBackup;

/// <summary>
/// What the Cloud Server page chooses: five kinds of event and four details for group instances,
/// and the same without the group for the rest. Everything is on until somebody turns it off.
/// </summary>
public sealed class CloudChoicesTests
{
    [Fact]
    public void EverythingIsOnByDefault()
    {
        var choices = CloudChoices.Default;

        Assert.True(choices.AnyEventOn);
        Assert.Equal(5, choices.Group.EventCount);
        Assert.Equal(5, choices.NonGroup.EventCount);
        Assert.All(Enum.GetValues<PresenceKind>(), kind =>
        {
            Assert.True(choices.Sends(kind, inGroup: true));
            Assert.True(choices.Sends(kind, inGroup: false));
        });

        foreach (var detail in Enum.GetValues<CloudDetail>())
            Assert.True(choices.Group.Has(detail));
    }

    [Fact]
    public void EachPresenceKindIsOneOfTheFiveKinds()
    {
        Assert.Equal(CloudEventKinds.Joined, CloudEventKindNames.Of(PresenceKind.Joined));
        Assert.Equal(CloudEventKinds.AlreadyHere, CloudEventKindNames.Of(PresenceKind.PresenceObserved));
        Assert.Equal(CloudEventKinds.Left, CloudEventKindNames.Of(PresenceKind.Left));
        Assert.Equal(CloudEventKinds.AvatarChanged, CloudEventKindNames.Of(PresenceKind.AvatarChanged));
        Assert.Equal(CloudEventKinds.StoppedLogging, CloudEventKindNames.Of(PresenceKind.LogStopped));
    }

    [Fact]
    public void AKindIsSwitchedOffOnlyInTheSectionItWasSwitchedOffIn()
    {
        var (choices, refused) = CloudChoices.Default.WithEvent(inGroup: true, CloudEventKinds.Left, on: false);

        Assert.False(refused);
        Assert.False(choices.Sends(PresenceKind.Left, inGroup: true));
        Assert.True(choices.Sends(PresenceKind.Left, inGroup: false));
        Assert.Equal(4, choices.Group.EventCount);
        Assert.Equal(5, choices.NonGroup.EventCount);
    }

    [Fact]
    public void TheLastEventThatIsOnCannotBeSwitchedOff()
    {
        var choices = CloudChoices.Default;

        // One section may be switched off entirely, one event at a time.
        foreach (var (kind, _, _) in CloudEventKindNames.All)
        {
            var result = choices.WithEvent(inGroup: false, kind, on: false);
            Assert.False(result.Refused);
            choices = result.Choices;
        }

        Assert.Equal(0, choices.NonGroup.EventCount);
        Assert.True(choices.AnyEventOn);

        // The other section then cannot lose its last: four go, the fifth is refused and stays on.
        var kinds = CloudEventKindNames.All.Select(k => k.Kind).ToList();
        foreach (var kind in kinds.Take(4))
            choices = choices.WithEvent(inGroup: true, kind, on: false).Choices;

        var last = choices.WithEvent(inGroup: true, kinds[4], on: false);

        Assert.True(last.Refused);
        Assert.Equal(choices, last.Choices);
        Assert.True(last.Choices.Group.Sends(kinds[4]));
        Assert.True(last.Choices.AnyEventOn);
    }

    [Fact]
    public void AnEventSwitchedBackOnIsNeverRefused()
    {
        var choices = CloudChoices.Default.WithEvent(inGroup: false, CloudEventKinds.Joined, on: false).Choices;
        foreach (var (kind, _, _) in CloudEventKindNames.All.Where(k => k.Kind != CloudEventKinds.Joined))
            choices = choices.WithEvent(inGroup: false, kind, on: false).Choices;

        // Only the group section has events now.
        Assert.Equal(0, choices.NonGroup.EventCount);

        // Switching one back on is never refused.
        var back = choices.WithEvent(inGroup: false, CloudEventKinds.Left, on: true);
        Assert.False(back.Refused);
        Assert.Equal(1, back.Choices.NonGroup.EventCount);
    }

    [Fact]
    public void AGroupInstanceHasAGroupToLeaveOutAndAnotherDoesNot()
    {
        var changed = CloudChoices.Default
            .WithDetail(inGroup: true, CloudDetail.GroupId, on: false)
            .WithDetail(inGroup: false, CloudDetail.GroupId, on: false);

        Assert.False(changed.Group.GroupId);
        Assert.True(changed.NonGroup.GroupId);
    }

    [Fact]
    public void DetailsAreChosenSeparatelyForEachSection()
    {
        var changed = CloudChoices.Default
            .WithDetail(inGroup: true, CloudDetail.WorldId, on: false)
            .WithDetail(inGroup: false, CloudDetail.InstanceId, on: false)
            .WithDetail(inGroup: false, CloudDetail.AvatarName, on: false);

        Assert.False(changed.Group.WorldId);
        Assert.True(changed.Group.InstanceId);
        Assert.True(changed.NonGroup.WorldId);
        Assert.False(changed.NonGroup.InstanceId);
        Assert.False(changed.NonGroup.AvatarName);
        Assert.True(changed.Group.AvatarName);
    }

    [Fact]
    public void ASectionWithNoEventsIsNothingOnForThatSection()
    {
        var none = new CloudChoices(new CloudSection(CloudEventKinds.None), CloudSection.Default);

        Assert.True(none.AnyEventOn);
        Assert.False(none.Sends(PresenceKind.Joined, inGroup: true));
        Assert.True(none.Sends(PresenceKind.Joined, inGroup: false));
    }

    [Fact]
    public void ChoicesSurviveBeingWrittenAndRead()
    {
        var choices = new CloudChoices(
            new CloudSection(CloudEventKinds.Joined | CloudEventKinds.Left, WorldId: false, GroupId: false),
            new CloudSection(CloudEventKinds.AvatarChanged | CloudEventKinds.StoppedLogging, InstanceId: false, AvatarName: false));

        var read = CloudChoices.FromJson(choices.GroupToJson(), choices.NonGroupToJson());

        Assert.Equal(choices, read);
    }

    [Fact]
    public void TheFileNamesEventsAndDetailsInPlainWords()
    {
        var json = CloudChoices.Default.GroupToJson();

        Assert.Equal(
            ["joined", "alreadyHere", "left", "avatarChanged", "stoppedLogging"],
            json["events"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.True(json["worldId"]!.GetValue<bool>());
        Assert.True(json["instanceId"]!.GetValue<bool>());
        Assert.True(json["groupId"]!.GetValue<bool>());
        Assert.True(json["avatarName"]!.GetValue<bool>());

        // A non-group instance has no group, so its object does not mention one.
        Assert.False(CloudChoices.Default.NonGroupToJson().ContainsKey("groupId"));
    }

    [Fact]
    public void AnythingMissingFromTheFileIsOn()
    {
        Assert.Equal(CloudChoices.Default, CloudChoices.FromJson(null, null));
        Assert.Equal(CloudChoices.Default, CloudChoices.FromJson([], []));

        var partial = CloudChoices.FromJson(JsonNode.Parse("""{ "worldId": false }""")!.AsObject(), null);

        Assert.False(partial.Group.WorldId);
        Assert.True(partial.Group.InstanceId);
        Assert.Equal(5, partial.Group.EventCount);
    }

    [Fact]
    public void AnEventsListThatNamesNothingMeansNothingIsOn()
    {
        var empty = JsonNode.Parse("""{ "events": [] }""")!.AsObject();
        var nonsense = JsonNode.Parse("""{ "events": ["dance", 7, null] }""")!.AsObject();

        Assert.Equal(0, CloudChoices.FromJson(empty, empty).Group.EventCount);
        Assert.False(CloudChoices.FromJson(empty, empty).AnyEventOn);
        Assert.False(CloudChoices.FromJson(nonsense, nonsense).AnyEventOn);
    }

    [Fact]
    public void KindNamesAreReadWhateverTheirCase()
    {
        var shape = JsonNode.Parse("""{ "events": ["JOINED", " alreadyhere "] }""")!.AsObject();

        var read = CloudChoices.FromJson(shape, null).Group;

        Assert.Equal(CloudEventKinds.Joined | CloudEventKinds.AlreadyHere, read.Events);
    }

    [Fact]
    public void TheFoldsFollowTheBoxAndOpenAgainAsTheyWere()
    {
        var folds = new CloudFolds(cloudOn: true);
        Assert.All(Enum.GetValues<CloudFold>(), s => Assert.True(folds.IsOpen(s)));

        folds.Toggle(CloudFold.NonGroupInstances);
        folds.CloudChanged(false);
        Assert.All(Enum.GetValues<CloudFold>(), s => Assert.False(folds.IsOpen(s)));

        // Every section can still be opened and read while Modbot Cloud is off.
        folds.Toggle(CloudFold.AlwaysSent);
        Assert.True(folds.IsOpen(CloudFold.AlwaysSent));

        // Switching it on restores which were open before it went off, not what was opened since.
        folds.CloudChanged(true);
        Assert.True(folds.IsOpen(CloudFold.GroupInstances));
        Assert.False(folds.IsOpen(CloudFold.NonGroupInstances));
        Assert.True(folds.IsOpen(CloudFold.AlwaysSent));
    }

    [Fact]
    public void AClientStartedWithCloudOffStartsFoldedAndOpensEverythingWhenItIsSwitchedOn()
    {
        var folds = new CloudFolds(cloudOn: false);
        Assert.All(Enum.GetValues<CloudFold>(), s => Assert.False(folds.IsOpen(s)));

        folds.CloudChanged(true);

        Assert.All(Enum.GetValues<CloudFold>(), s => Assert.True(folds.IsOpen(s)));
    }
}
