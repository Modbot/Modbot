using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.PossibleCrash;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Tests.Features.PossibleCrash;

/// <summary>
/// The possible crash watch: enough people leaving one instance within the window raises one alert
/// that names who joined or changed avatar just before, and nothing below the count does.
/// </summary>
public class PossibleCrashWatchTests
{
    private const string Instance = "39911";

    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static FactRecord Fact(string type, string subject, DateTimeOffset at, string? name = null, string instance = Instance, string world = "wrld_4b34")
        => new()
        {
            Type = type,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            WorldId = world,
            InstanceId = instance,
            Source = FactSource.Companion,
            Data = name is null ? null : new JsonObject { ["displayName"] = name },
        };

    private static List<FactRecord> Leaves(int count, DateTimeOffset first, double gapSeconds = 0.5, string instance = Instance)
        => Enumerable.Range(0, count)
            .Select(i => Fact(FactType.InstanceLeft, $"usr_leaver{i}", first.AddSeconds(i * gapSeconds), instance: instance))
            .ToList();

    [Fact]
    public void FiresAtTheCount_AndNotBelowIt()
    {
        var watch = new PossibleCrashWatch();

        Assert.Empty(watch.Observe(Leaves(4, Noon), leaves: 5, seconds: 5, Noon.AddSeconds(3)));

        var found = Assert.Single(watch.Observe([Fact(FactType.InstanceLeft, "usr_fifth", Noon.AddSeconds(2))], 5, 5, Noon.AddSeconds(3)));
        Assert.Equal(5, found.Left);
        Assert.Equal(Instance, found.InstanceId);
        Assert.Equal("wrld_4b34", found.WorldId);
    }

    [Fact]
    public void LeavesSpreadWiderThanTheWindow_AreNotACrash()
    {
        var watch = new PossibleCrashWatch();

        // Five people, six seconds apart: never five inside five seconds.
        var slow = Leaves(5, Noon, gapSeconds: 6);

        Assert.Empty(watch.Observe(slow, 5, 5, Noon.AddMinutes(1)));
    }

    [Fact]
    public void TheNumbersAreTheCallersToChoose()
    {
        var watch = new PossibleCrashWatch();

        Assert.Single(watch.Observe(Leaves(3, Noon), leaves: 3, seconds: 2, Noon.AddSeconds(3)));
    }

    [Fact]
    public void TheSamePersonReportedTwice_CountsOnce()
    {
        var watch = new PossibleCrashWatch();

        var twice = Leaves(4, Noon);
        twice.AddRange(Leaves(4, Noon.AddMilliseconds(40)));

        Assert.Empty(watch.Observe(twice, 5, 5, Noon.AddSeconds(3)));
    }

    [Fact]
    public void NamesWhoJoinedOrChangedAvatarJustBefore_NewestFirst_AndNobodyEarlier()
    {
        var watch = new PossibleCrashWatch();

        watch.Observe(
            [
                Fact(FactType.InstanceJoined, "usr_early", Noon.AddSeconds(-60), "Early"),
                Fact(FactType.InstanceJoined, "usr_joiner", Noon.AddSeconds(-8), "Joiner"),
                Fact(FactType.AvatarChanged, "usr_changer", Noon.AddSeconds(-3), "Changer"),
            ],
            5, 5, Noon.AddSeconds(-2));

        var found = Assert.Single(watch.Observe(Leaves(5, Noon), 5, 5, Noon.AddSeconds(3)));

        Assert.Equal(["usr_changer", "usr_joiner"], found.People.Select(p => p.SubjectId));
        Assert.Equal(["Changer", "Joiner"], found.People.Select(p => p.DisplayName));
        Assert.Equal(["changed avatar", "joined"], found.People.Select(p => p.What));
    }

    [Fact]
    public void ANewAlertIsNotRaisedForTheTrickleAfterIt()
    {
        var watch = new PossibleCrashWatch();

        Assert.Single(watch.Observe(Leaves(5, Noon), 5, 5, Noon.AddSeconds(3)));

        // Five more people in the next few seconds: still the same crash.
        Assert.Empty(watch.Observe(Leaves(5, Noon.AddSeconds(4)).Select(f => f with { SubjectId = "late" + f.SubjectId }), 5, 5, Noon.AddSeconds(8)));

        // Well after the quiet window, a second burst is a second crash.
        var again = Leaves(5, Noon.AddSeconds(30)).Select(f => f with { SubjectId = "again" + f.SubjectId });
        Assert.Single(watch.Observe(again, 5, 5, Noon.AddSeconds(33)));
    }

    [Fact]
    public void EachInstanceIsCountedOnItsOwn()
    {
        var watch = new PossibleCrashWatch();

        var mixed = Leaves(3, Noon, instance: "1").Concat(Leaves(3, Noon, instance: "2")).ToList();

        Assert.Empty(watch.Observe(mixed, 5, 5, Noon.AddSeconds(3)));
    }

    [Fact]
    public void ABatchBackdatedPastTheFreshLimit_RaisesNothing()
    {
        var watch = new PossibleCrashWatch();

        var old = Leaves(5, Noon.AddDays(-1));

        Assert.Empty(watch.Observe(old, 5, 5, Noon));
    }
}
