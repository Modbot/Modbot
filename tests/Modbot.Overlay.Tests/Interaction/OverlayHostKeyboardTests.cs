using System.Numerics;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Views;

namespace Modbot.Overlay.Tests.Interaction;

/// <summary>
/// Typing a name in a headset: the Name filter puts the runtime's keyboard up, what is typed goes
/// to the list it was put up for, and a runtime with no keyboard offers no Name filter at all.
/// </summary>
public class OverlayHostKeyboardTests
{
    private const int Size = 512;

    private static readonly Vector3 Centre = new(0.35f, -0.28f, -1.0f);

    private sealed class FakeSurface : IOverlaySurface
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

    /// <summary>Attached, with a keyboard a test can type on, or without one.</summary>
    private class AttachedRuntime : IOverlayRuntime
    {
        public OverlayRuntimeStatus Status { get; private set; } = new(OverlayRuntimeState.NotStarted);

        public OverlayTracking Tracking { get; set; } = OverlayTracking.None;

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

        public OverlayTracking ReadTracking() => Tracking;

        public void Place(OverlayPlacement placement)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class TypingRuntime : AttachedRuntime, IOverlayKeyboard
    {
        public List<string> Shown { get; } = [];

        public string? Typed { get; set; }

        public bool CanType => true;

        public bool ShowKeyboard(string text)
        {
            Shown.Add(text);
            return true;
        }

        public string? TakeTyped()
        {
            var typed = Typed;
            Typed = null;
            return typed;
        }
    }

    private static OverlayScreen Roster(ListFilters? filters = null) => new(
        "Cat Lounge",
        new Cached<InstanceContext>(
            new InstanceContext("39911", [.. Enumerable.Range(0, 3).Select(i => new RosterMember($"usr_{i}", $"Person {i}", RosterStanding.Member, 0, []))]),
            Freshness.Fresh,
            TimeSpan.Zero),
        Freshness.Fresh,
        RosterFilters: filters);

    private static OverlayHost Build(AttachedRuntime runtime, OverlayScreen screen)
    {
        var host = new OverlayHost(runtime, new FakeSurface(), new AvaloniaFrameRenderer(Size, Size));
        runtime.Start();
        host.Start();
        host.Update(screen);
        return host;
    }

    [Fact]
    public void TappingTheNameFilterPutsTheKeyboardUpAndDoneSearchesThatList()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new TypingRuntime();
            var host = Build(runtime, Roster(new ListFilters(Name: "ri")));
            var typed = new List<(OverlayPage List, string Name)>();
            host.NameTyped += (list, name) => typed.Add((list, name));

            var chip = Find(host, t => t is OverlayTarget.Filter { Part: FilterPart.Name });
            Tap(host, runtime, chip);

            // Put up with the name the list already holds, so typing carries on from it.
            Assert.Equal(["ri"], runtime.Shown);

            runtime.Typed = "rin";
            host.Poll();

            Assert.Equal([(OverlayPage.Instance, "rin")], typed);
        });
    }

    [Fact]
    public void TappingTheOpenNameBoxPutsTheKeyboardUpAgain()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new TypingRuntime();
            var host = Build(runtime, Roster(new ListFilters(Name: "ri", Open: FilterPart.Name)));

            var box = Find(host, t => t is OverlayTarget.TypeName);
            Tap(host, runtime, box);

            Assert.Equal(["ri"], runtime.Shown);
        });
    }

    [Fact]
    public void ClosingAnOpenNameFilterDoesNotPutTheKeyboardUp()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new TypingRuntime();
            var host = Build(runtime, Roster(new ListFilters(Open: FilterPart.Name)));

            var chip = Find(host, t => t is OverlayTarget.Filter { Part: FilterPart.Name });
            Tap(host, runtime, chip);

            Assert.Empty(runtime.Shown);
        });
    }

    [Fact]
    public void NothingTypedIsNotANameAndIsNotPassedOn()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new TypingRuntime();
            var host = Build(runtime, Roster());
            var typed = 0;
            host.NameTyped += (_, _) => typed++;

            host.Poll();

            Assert.Equal(0, typed);
        });
    }

    [Fact]
    public void ARuntimeWithNoKeyboardOffersNoNameFilter()
    {
        AvaloniaTestHost.Run(() =>
        {
            var runtime = new AttachedRuntime();
            var host = Build(runtime, Roster());

            Assert.False(host.CanType);
            Assert.True(host.Showing.NoKeyboard);
            Assert.False(Has(host, t => t is OverlayTarget.Filter { Part: FilterPart.Name }));
            Assert.True(Has(host, t => t is OverlayTarget.Filter { Part: FilterPart.Rank }));
        });
    }

    [Fact]
    public void ANameTypedElsewhereStillShowsWhereThereIsNoKeyboard()
    {
        // A name typed on the desktop window is hiding people on the headset too, so the headset
        // has to say so, keyboard or not.
        AvaloniaTestHost.Run(() =>
        {
            var host = Build(new AttachedRuntime(), Roster(new ListFilters(Name: "ri")));

            Assert.True(Has(host, t => t is OverlayTarget.Filter { Part: FilterPart.Name }));
        });
    }

    private static bool Has(OverlayHost host, Func<OverlayTarget, bool> match)
    {
        try
        {
            Find(host, match);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static (float Across, float Down) Find(OverlayHost host, Func<OverlayTarget, bool> match)
    {
        // Sweep the panel with the host's own hit testing.
        for (var down = 0.01f; down < 1f; down += 0.01f)
        {
            for (var across = 0.01f; across < 1f; across += 0.01f)
            {
                if (host.TargetAt(across, down) is { } target && match(target))
                    return (across, down);
            }
        }

        throw new InvalidOperationException("Not on the panel.");
    }

    private static void Tap(OverlayHost host, AttachedRuntime runtime, (float Across, float Down) at)
    {
        runtime.Tracking = Hands.RightOnly(Hands.Hand(AimAtPanel(at.Across, at.Down)));
        host.PollInput(TimeSpan.Zero);
        runtime.Tracking = Hands.RightOnly(Hands.Hand(AimAtPanel(at.Across, at.Down), click: true));
        host.PollInput(TimeSpan.FromMilliseconds(50));
    }

    private static Pose AimAtPanel(float across, float down)
    {
        var width = OverlayPlacement.Default.Width;
        var target = Centre + new Vector3((across - 0.5f) * width, (0.5f - down) * width, 0);
        return Hands.AimingAt(Vector3.Zero, target);
    }
}
