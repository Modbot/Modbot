using System.Text.Json;
using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// What the live feed's JSON becomes: a moderation event keeps its sentence and origin, and an
/// event from a server that sends neither is the presence event it always was.
/// </summary>
public class LiveEventWireTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AModerationEventKeepsItsSentenceAndOrigin()
    {
        const string wire = """
            {"kind":"event","event":{"id":"81","cursor":"81","kind":"fact","type":"vrchat.group.member.ban",
             "at":"2026-10-18T18:00:00+00:00","instanceId":null,"worldId":null,"person":null,"flagged":false,
             "reason":null,"byThisDevice":false,
             "sentence":"Mira banned Ada from Cat Lounge.","origin":"VRChat"}}
            """;

        var message = JsonSerializer.Deserialize<LiveSocketMessage>(wire, Json);

        var @event = Assert.IsType<LiveEvent>(message?.Event);
        Assert.Equal("Mira banned Ada from Cat Lounge.", @event.Sentence);
        Assert.Equal("VRChat", @event.Origin);
        Assert.True(@event.IsModeration);
        Assert.Equal(LiveEventKinds.Moderation, @event.FilterKind);
        Assert.False(LiveEventKinds.ChangesRoster(@event.Kind));
    }

    [Fact]
    public void AnEventWithNoSentenceIsAPresenceEventAsBefore()
    {
        const string wire = """
            {"kind":"event","event":{"id":"82","cursor":"82","kind":"person_joined",
             "at":"2026-10-18T18:00:00+00:00","instanceId":"39911","worldId":"wrld_a",
             "person":{"id":"usr_a","displayName":"Ada","trustRank":null,"standing":"Ordinary","priorActions":0,"flags":[]},
             "flagged":false,"reason":null,"byThisDevice":false}}
            """;

        var message = JsonSerializer.Deserialize<LiveSocketMessage>(wire, Json);

        var @event = Assert.IsType<LiveEvent>(message?.Event);
        Assert.Null(@event.Sentence);
        Assert.Null(@event.Origin);
        Assert.False(@event.IsModeration);
        Assert.Equal(LiveEventKinds.PersonJoined, @event.FilterKind);
        Assert.True(LiveEventKinds.ChangesRoster(@event.Kind));
    }

    [Fact]
    public void AnAvatarChangeIsAModerationRowWithItsOwnFilterKind()
    {
        const string wire = """
            {"kind":"event","event":{"id":"83","cursor":"83","kind":"fact","type":"vrchat.avatar.change",
             "at":"2026-10-18T18:00:00+00:00","instanceId":"39911","worldId":"wrld_a","person":null,"flagged":false,
             "reason":null,"byThisDevice":false,
             "sentence":"Changed avatar - Ada in Cat Lounge.","origin":"VRChat"}}
            """;

        var message = JsonSerializer.Deserialize<LiveSocketMessage>(wire, Json);

        var @event = Assert.IsType<LiveEvent>(message?.Event);
        Assert.Equal(LiveEventKinds.AvatarChangeType, @event.Type);
        Assert.True(@event.IsModeration);
        Assert.True(@event.IsAvatarChange);
        Assert.Equal(LiveEventKinds.ChangedAvatar, @event.FilterKind);
    }

    [Fact]
    public void AnAvatarChangeTypeWithNoSentenceIsNotARow()
    {
        var @event = new LiveEvent(
            "84", "84", "fact", DateTimeOffset.UnixEpoch, null, null, false, null, false,
            Type: LiveEventKinds.AvatarChangeType);

        Assert.False(@event.IsAvatarChange);
        Assert.Equal("fact", @event.FilterKind);
    }

    [Fact]
    public void AnotherModerationTypeStaysInActions()
    {
        const string wire = """
            {"kind":"event","event":{"id":"85","cursor":"85","kind":"fact","type":"vrchat.group.member.ban",
             "at":"2026-10-18T18:00:00+00:00","instanceId":null,"worldId":null,"person":null,"flagged":false,
             "reason":null,"byThisDevice":false,"sentence":"Mira banned Ada from Cat Lounge.","origin":"VRChat"}}
            """;

        var message = JsonSerializer.Deserialize<LiveSocketMessage>(wire, Json);

        var @event = Assert.IsType<LiveEvent>(message?.Event);
        Assert.False(@event.IsAvatarChange);
        Assert.Equal(LiveEventKinds.Moderation, @event.FilterKind);
    }
}
