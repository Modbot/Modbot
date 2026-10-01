using Modbot.Companion.Crash;
using Modbot.Companion.Instances;
using Modbot.Companion.LogReading;

namespace Modbot.Companion.Tests.Crash;

/// <summary>
/// Copy crash details: the instance, who was in it and what they wore, and the last ten minutes of
/// VRChat's own <c>[Behaviour]</c> lines, as one block of text — with the instance secret taken out.
/// </summary>
public class CrashDetailsTests
{
    private const string Location = "wrld_cat:98874~group(grp_cats)~groupAccessType(public)~region(use)";

    private static InstanceLocation Instance(string raw = Location)
    {
        Assert.True(InstanceLocation.TryParse(raw, out var location));
        return location;
    }

    private static string Line(string time, string message, string tag = "Behaviour")
        => $"2026.10.01 {time} Debug      -  [{tag}] {message}";

    [Fact]
    public void OnlyBehaviourLinesFromTheLastTenMinutesBeforeTheLastLineAreKept()
    {
        var picked = RecentBehaviourLines.Pick(
            [
                Line("20:50:00", "OnPlayerJoined Old (usr_old)"),
                Line("21:04:59", "too early"),
                Line("21:05:00", "OnPlayerJoined Rin (usr_rin)"),
                Line("21:06:00", "frame time", tag: "VRCTrackingManager"),
                "a continuation line with no timestamp",
                Line("21:14:00", "Switching Rin to avatar Crasher"),
                Line("21:15:00", "the last line", tag: "Always"),
            ],
            CrashDetails.Window);

        Assert.Equal(
            [Line("21:05:00", "OnPlayerJoined Rin (usr_rin)"), Line("21:14:00", "Switching Rin to avatar Crasher")],
            picked);
    }

    [Fact]
    public void ALogWithNoTimestampedLinesGivesNothing()
        => Assert.Empty(RecentBehaviourLines.Pick(["", "not a log line"], CrashDetails.Window));

    [Fact]
    public void TheBlockNamesTheInstanceThePeopleAndTheirAvatars()
    {
        var text = CrashDetails.Write(
            new CrashScene(
                "The Black Cat",
                Instance(),
                [new PersonHere("usr_rin", "Rin", "Crasher"), new PersonHere("usr_sam", "Sam", null)]),
            [Line("21:14:00", "Switching Rin to avatar Crasher")],
            TimeSpan.FromHours(-5));

        Assert.NotNull(text);
        Assert.Contains("World: The Black Cat (wrld_cat)", text, StringComparison.Ordinal);
        Assert.Contains("Instance: " + Location, text, StringComparison.Ordinal);
        Assert.Contains("UTC-05:00", text, StringComparison.Ordinal);
        Assert.Contains("People in the instance (2):", text, StringComparison.Ordinal);
        Assert.Contains("Rin (usr_rin), avatar: Crasher", text, StringComparison.Ordinal);
        Assert.Contains("Sam (usr_sam), avatar: not in the log", text, StringComparison.Ordinal);
        Assert.Contains("Switching Rin to avatar Crasher", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstanceSecretIsTakenOutOfEveryLine()
    {
        var text = CrashDetails.Write(
            new CrashScene(null, Instance("wrld_cat:98874~private(usr_x)~nonce(SECRET-1234)"), []),
            [Line("21:00:00", "Joining wrld_cat:98874~private(usr_x)~nonce(SECRET-1234)")],
            TimeSpan.Zero);

        Assert.NotNull(text);
        Assert.DoesNotContain("SECRET-1234", text, StringComparison.Ordinal);
        Assert.Contains("~nonce(removed)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoInstanceAndNoLinesIsNothingToCopy()
        => Assert.Null(CrashDetails.Write(new CrashScene(null, null, []), [], TimeSpan.Zero));

    [Fact]
    public void AtMostTheNewestLinesAreKept()
    {
        var lines = Enumerable.Range(0, CrashDetails.MaxLines + 50)
            .Select(i => Line("21:00:00", $"line {i}"))
            .ToList();

        var text = CrashDetails.Write(new CrashScene(null, Instance(), []), lines, TimeSpan.Zero)!;

        Assert.DoesNotContain("line 49\n", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains($"line {CrashDetails.MaxLines + 49}", text, StringComparison.Ordinal);
        Assert.Contains($"({CrashDetails.MaxLines}):", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrackerKnowsWhatEverybodyWasWearingWhenTheModeratorArrived()
    {
        // VRChat states everybody's avatar during the arrival burst. Those are not changes and are
        // not reported, but they are exactly what a crash report needs.
        var tracker = new InstanceSessionTracker();
        var at = new DateTime(2026, 10, 1, 21, 0, 0);
        DateTime Tick() => at = at.AddSeconds(1);

        foreach (var e in new VRChatLogEvent[]
        {
            new JoiningInstanceEvent(Tick(), Location),
            new PlayerJoinedEvent(Tick(), "Rin", "usr_rin"),
            new AvatarSwitchedEvent(Tick(), "Rin to avatar Crasher"),
            new PlayerJoinedEvent(Tick(), "me", "usr_me"),
            new LocalPlayerIdentifiedEvent(Tick(), "me"),
        })
        {
            _ = tracker.Observe(e).ToList();
        }

        var rin = Assert.Single(tracker.People, p => p.UserId == "usr_rin");
        Assert.Equal("Crasher", rin.AvatarName);

        // Remembering it changes nothing about what is reported: the next avatar line after the
        // burst is reported exactly as it was before this list existed.
        var changed = tracker.Observe(new AvatarSwitchedEvent(Tick(), "Rin to avatar Crasher")).ToList();
        Assert.Single(changed, o => o.Kind == PresenceKind.AvatarChanged);
    }
}
