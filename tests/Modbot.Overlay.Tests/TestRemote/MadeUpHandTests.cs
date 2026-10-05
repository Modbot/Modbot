using System.Numerics;
using System.Text.Json.Nodes;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.TestRemote;

/// <summary>
/// The test remote's made-up controller goes through the panel's own controller handling: a tap
/// lands on what is drawn there, scrolling comes out in rows, and grabbing, carrying and letting go
/// move the panel the way a real hand does. Also what <c>state</c> says about the panel.
/// </summary>
public class MadeUpHandTests
{
    private const int Size = 512;

    private sealed class Surface : IOverlaySurface
    {
        public int Width => Size;

        public int Height => Size;

        public nint TextureHandle => 1;

        public ReadOnlyMemory<byte> Pixels => ReadOnlyMemory<byte>.Empty;

        public void Upload(ReadOnlySpan<byte> bgra)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Attached, with nobody holding a controller.</summary>
    private sealed class Runtime : IOverlayRuntime
    {
        public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

        public OverlayRuntimeStatus Start() => Status = new(OverlayRuntimeState.Running, Detail: "Attached to a test.");

        public void Poll()
        {
        }

        public bool Submit(IOverlaySurface surface) => true;

        public void Show()
        {
        }

        public void Hide()
        {
        }

        public OverlayTracking ReadTracking() => OverlayTracking.None;

        public void Place(OverlayPlacement placement)
        {
        }

        public void Dispose()
        {
        }
    }

    private static OverlayScreen Roster(int people = 3) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [.. Enumerable.Range(0, people).Select(i => new RosterMember($"{TestPeople.Prefix}p{i}", $"Person {i}", RosterStanding.Member, 0, []))]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh);

    private static (OverlayHost Host, MadeUpHand Hand) Build(OverlayScreen? screen = null)
    {
        var runtime = new Runtime();
        var host = new OverlayHost(runtime, new Surface(), new AvaloniaFrameRenderer(Size, Size));
        host.Start();
        host.Update(screen ?? Roster());
        var hand = new MadeUpHand();
        host.MadeUp = hand;
        return (host, hand);
    }

    private static void PollUntilDone(OverlayHost host, MadeUpHand hand, ref TimeSpan now)
    {
        for (var i = 0; i < 1000 && hand.Pending > 0; i++)
        {
            now += TimeSpan.FromMilliseconds(33);
            host.PollInput(now);
        }

        Assert.Equal(0, hand.Pending);
    }

    /// <summary>The first point on the panel, scanning down its middle, where a row for this person is drawn.</summary>
    private static (float Across, float Down) Row(OverlayHost host, string subject)
    {
        for (var y = 0; y < Size; y += 4)
        {
            for (var x = Size / 4; x < Size; x += Size / 4)
            {
                if (host.TargetAt(x / (float)Size, y / (float)Size) is OverlayTarget.Person p && p.SubjectId == subject)
                    return (x / (float)Size, y / (float)Size);
            }
        }

        throw new InvalidOperationException($"No row for {subject}");
    }

    [Fact]
    public void ATapLandsOnTheRowUnderItThroughTheControllerPath()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, hand) = Build();
            var taps = new List<OverlayTarget?>();
            host.Tapped += taps.Add;

            var (across, down) = Row(host, $"{TestPeople.Prefix}p1");
            hand.Tap(across, down);
            Assert.Equal(3, hand.Pending);

            var now = TimeSpan.FromSeconds(10);
            PollUntilDone(host, hand, ref now);

            Assert.Equal([new OverlayTarget.Person($"{TestPeople.Prefix}p1")], taps);

            // Done: the real hand is read again, so nothing more is tapped.
            host.PollInput(now + TimeSpan.FromSeconds(1));
            Assert.Single(taps);
        });
    }

    [Fact]
    public void ScrollingComesOutInTheRowsAskedFor()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, hand) = Build(Roster(30));
            var rows = new List<int>();
            host.RosterScrolled += rows.Add;

            var (across, down) = Row(host, $"{TestPeople.Prefix}p1");
            hand.Scroll(across, down, 2);

            var now = TimeSpan.FromSeconds(10);
            PollUntilDone(host, hand, ref now);

            Assert.Equal(2, rows.Sum());
        });
    }

    [Fact]
    public void GrabbingCarryingAndLettingGoMoveThePanelLikeAHand()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, hand) = Build(Roster(30));
            var now = TimeSpan.FromSeconds(10);

            hand.Grab();
            PollUntilDone(host, hand, ref now);

            Assert.True(hand.Holding);
            Assert.Equal(Hand.Right, host.Holding);

            hand.Move(new Vector3(0.3f, 0.1f, -1.2f));
            PollUntilDone(host, hand, ref now);

            hand.Release();
            PollUntilDone(host, hand, ref now);

            Assert.False(hand.Holding);
            Assert.Null(host.Holding);

            // Let go in the room, where it was carried to: in front of the head (at the room's
            // origin here, with no headset tracked).
            Assert.Equal(OverlayAnchor.World, host.Placement.Anchor);
            Assert.Equal(0.3f, host.Placement.Offset.X, 2);
            Assert.Equal(0.1f, host.Placement.Offset.Y, 2);
            Assert.Equal(-1.2f, host.Placement.Offset.Z, 2);
        });
    }

    [Fact]
    public void WithNothingToDoItStandsInForNothing()
    {
        var hand = new MadeUpHand();
        var real = OverlayTracking.None;

        Assert.Equal(real, hand.Apply(real, OverlayPlacement.Default));
    }

    [Fact]
    public void ThePanelsStateSaysWhatItShowsAndWhereItIs()
    {
        AvaloniaTestHost.Run(() =>
        {
            var (host, _) = Build();
            var json = TestRemoteState.MainPanel(host, on: true);

            Assert.True(json["attached"]!.GetValue<bool>());
            Assert.True(json["framesDrawn"]!.GetValue<int>() > 0);
            Assert.Equal("instance", json["screen"]!["page"]!.GetValue<string>());
            Assert.False(json["screen"]!["idle"]!.GetValue<bool>());
            Assert.Equal("Cat Lounge", json["screen"]!["group"]!.GetValue<string>());
            Assert.Equal(3, json["screen"]!["roster"]!.GetValue<int>());
            Assert.IsType<JsonArray>(json["screen"]!["cards"]);
            Assert.Equal("head", json["placement"]!["anchor"]!.GetValue<string>());

            foreach (var key in new[] { "x", "y", "z" })
                Assert.NotNull(json["placement"]!["offset"]![key]);

            foreach (var key in new[] { "width", "opacity", "curve", "locked", "clickThrough" })
                Assert.NotNull(json["placement"]![key]);

            var off = TestRemoteState.MainPanel(null, on: false);
            Assert.False(off["on"]!.GetValue<bool>());
            Assert.False(off["attached"]!.GetValue<bool>());
        });
    }

    [Fact]
    public void ACardsStateCarriesItsKindNameRankMarkDetailAndTimeLeft()
    {
        var cards = TestRemoteState.Cards(
            [new PopUp("a", "Flagged user joined", "Rin", "Was rude", PopUpTone.Flagged, Modbot.Core.Users.TrustRank.User, true, TestPeople.Prefix + "rin")],
            _ => TimeSpan.FromSeconds(4.25));

        var card = Assert.Single(cards)!.AsObject();
        Assert.Equal("flagged", card["kind"]!.GetValue<string>());
        Assert.Equal("Rin", card["name"]!.GetValue<string>());
        Assert.Equal("User", card["rank"]!.GetValue<string>());
        Assert.True(card["eighteenPlus"]!.GetValue<bool>());
        Assert.True(card["test"]!.GetValue<bool>());
        Assert.Equal("Was rude", card["detail"]!.GetValue<string>());
        Assert.Equal(4.2, card["secondsLeft"]!.GetValue<double>(), 1);
    }

    [Theory]
    [InlineData("usr_1", true)]
    [InlineData(TestPeople.Prefix + "rin", false)]
    public void ARealPersonsRowIsNotTapped(string subject, bool refused)
    {
        Assert.Equal(refused, RemoteTaps.Refusal(new OverlayTarget.Person(subject), null) is not null);
        Assert.Equal(refused, RemoteTaps.Refusal(new OverlayTarget.AddHeadsUp(subject), null) is not null);
        Assert.Equal(refused, RemoteTaps.Refusal(new OverlayTarget.RefreshPerson(), subject) is not null);
    }

    [Fact]
    public void NothingThatSendsAHeadsUpIsTapped()
    {
        Assert.NotNull(RemoteTaps.Refusal(new OverlayTarget.PlaceHeadsUp(), null));
        Assert.NotNull(RemoteTaps.Refusal(new OverlayTarget.ClearHeadsUp("x"), null));
        Assert.Null(RemoteTaps.Refusal(new OverlayTarget.GoTo(OverlayPage.Events), null));
        Assert.Null(RemoteTaps.Refusal(null, null));
    }
}
