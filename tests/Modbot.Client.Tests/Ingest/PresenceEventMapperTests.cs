using System.Text.Json;
using Modbot.Client.Ingest;
using Modbot.Client.Instances;
using Modbot.Client.Time;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.Ingest;

public class PresenceEventMapperTests
{
    private sealed class CountingIds : IClientEventIdSource
    {
        private int _next;

        public string Next() => $"id-{++_next}";
    }

    private static readonly TimeZoneInfo Utc =
        TimeZoneInfo.CreateCustomTimeZone("Test/Utc", TimeSpan.Zero, "UTC", "UTC");

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.Zero));

    private PresenceEventMapper Mapper(ServerClock? serverClock = null)
        => new(new LogTimestampConverter(Utc), serverClock ?? new ServerClock(_clock), new CountingIds());

    private static ObservedPresence Observation(
        PresenceKind kind = PresenceKind.Joined,
        string location = "wrld_w:85019~group(grp_cats)~groupAccessType(public)~region(use)",
        string? displayName = "~ RedZu ~",
        string? avatarName = null)
    {
        Assert.True(InstanceLocation.TryParse(location, out var parsed));

        return new ObservedPresence(
            kind,
            new DateTime(2026, 9, 3, 20, 32, 18),
            "usr_subject",
            displayName,
            parsed,
            avatarName);
    }

    [Fact]
    public void CarriesTheWorldTheInstanceAndTheGroup()
    {
        var mapped = Mapper().Map(Observation())!;

        Assert.Equal("wrld_w", mapped.WorldId);
        Assert.Equal("85019", mapped.InstanceId);
        Assert.Equal("grp_cats", mapped.GroupId);
        Assert.Equal("usr_subject", mapped.SubjectId);
    }

    [Theory]
    [InlineData(PresenceKind.Joined, ClientEventType.InstanceJoined)]
    [InlineData(PresenceKind.PresenceObserved, ClientEventType.InstancePresenceObserved)]
    [InlineData(PresenceKind.Left, ClientEventType.InstanceLeft)]
    [InlineData(PresenceKind.AvatarChanged, ClientEventType.AvatarChanged)]
    public void MapsEachKindOntoItsProtocolType(PresenceKind kind, ClientEventType expected)
    {
        Assert.Equal(expected, Mapper().Map(Observation(kind))!.Type);
    }

    [Fact]
    public void PresenceObservedHasNoUpperBound()
    {
        // Its unknown bound is the lower one: this person arrived at some earlier, unknown time.
        // Filling in occurredBefore would invent precision in the wrong direction.
        Assert.Null(Mapper().Map(Observation(PresenceKind.PresenceObserved))!.OccurredBefore);
    }

    [Fact]
    public void TimestampsGoOutInServerTime()
    {
        var serverClock = new ServerClock(_clock);
        serverClock.Add(new ClockSample(
            _clock.UtcNow,
            _clock.UtcNow + TimeSpan.FromSeconds(90),
            _clock.UtcNow));

        var mapped = Mapper(serverClock).Map(Observation())!;

        Assert.Equal(
            new DateTimeOffset(2026, 9, 3, 20, 33, 48, TimeSpan.Zero),
            mapped.OccurredAt.ToUniversalTime());
    }

    [Fact]
    public void CarriesTheDisplayNameAsHistory()
    {
        var mapped = Mapper().Map(Observation())!;

        Assert.Equal("~ RedZu ~", mapped.Data["displayName"]);
    }

    [Fact]
    public void OmitsADisplayNameTheLogDidNotGive()
    {
        var mapped = Mapper().Map(Observation(displayName: null))!;

        Assert.DoesNotContain("displayName", mapped.Data.Keys);
    }

    [Fact]
    public void CarriesTheAvatarDisplayNameAndNoAvatarId()
    {
        // VRChat withholds avtr_ ids from clients on purpose. Resolving a name to an id is the
        // server's job, through a community database -- there is nothing in the log to send.
        var mapped = Mapper().Map(Observation(PresenceKind.AvatarChanged, avatarName: "Smol shark"))!;

        Assert.Equal("Smol shark", mapped.Data["avatarName"]);
        Assert.DoesNotContain(mapped.Data.Keys, k => k.Contains("avatarId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RefusesToMapAnInstanceWithNoOwningGroup()
    {
        Assert.Null(Mapper().Map(Observation(location: "wrld_w:1~private(usr_a)~nonce(secret)")));
    }

    [Fact]
    public void EachEventGetsItsOwnIdempotencyKey()
    {
        var mapper = Mapper();

        Assert.NotEqual(mapper.Map(Observation())!.ClientEventId, mapper.Map(Observation())!.ClientEventId);
    }

    [Fact]
    public void RealIdsAreRandomAndNotDerivedFromTime()
    {
        var source = new RandomClientEventIdSource();

        Assert.Equal(1000, Enumerable.Range(0, 1000).Select(_ => source.Next()).Distinct().Count());
    }

    [Fact]
    public void TheSerialisedEventContainsNothingBeyondTheAgreedFields()
    {
        // The privacy claim, as a test. If a field is added to the wire type without being thought
        // about, this fails.
        var mapped = Mapper().Map(Observation(PresenceKind.AvatarChanged, avatarName: "Smol shark"))!;

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(mapped));

        Assert.Equal(
            ["clientEventId", "type", "occurredAt", "occurredBefore", "subjectId", "worldId",
             "instanceId", "groupId", "data"],
            document.RootElement.EnumerateObject().Select(p => p.Name));

        Assert.Equal(
            ["displayName", "avatarName"],
            document.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void TheSerialisedEventNeverCarriesTheInstanceSecret()
    {
        var mapped = Mapper().Map(Observation(
            location: "wrld_w:1~group(grp_cats)~nonce(THE-SECRET)~region(use)"))!;

        Assert.DoesNotContain("THE-SECRET", JsonSerializer.Serialize(mapped));
    }

    [Fact]
    public void EventTypesGoOnTheWireByName()
    {
        var mapped = Mapper().Map(Observation(PresenceKind.PresenceObserved))!;

        Assert.Contains("\"InstancePresenceObserved\"", JsonSerializer.Serialize(mapped));
    }
}
