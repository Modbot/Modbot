using Modbot.Companion.Chat;
using Modbot.Companion.Ingest;
using Modbot.Companion.Overlay;

namespace Modbot.Companion.Tests.Chat;

/// <summary>The open pop-up chats: a new conversation each, numbered from 1, closed one at a time.</summary>
public class ChatPopupsTests
{
    private static readonly ServerPairing Pairing = new("cats", new Uri("https://modbot.example"), "token", "grp_cats");

    private static ChatPopups Popups(List<ChatSession>? made = null)
        => new(() =>
        {
            var session = new ChatSession(new EchoClient(), () => Pairing);
            made?.Add(session);
            return session;
        });

    [Fact]
    public void EachOpenIsANewPopupWithANewConversation()
    {
        var made = new List<ChatSession>();
        var popups = Popups(made);

        var first = popups.Open();
        var second = popups.Open();

        Assert.NotSame(first.Session, second.Session);
        Assert.Equal(2, made.Count);
        Assert.Same(made[0], first.Session);
        Assert.Same(made[1], second.Session);
    }

    [Fact]
    public void NumbersCountUpFromOne()
    {
        var popups = Popups();

        Assert.Equal([1, 2, 3], new[] { popups.Open().Number, popups.Open().Number, popups.Open().Number });
        Assert.Equal([1, 2, 3], popups.All.Select(p => p.Number));
    }

    [Fact]
    public void ANumberFreedByClosingIsTheNextOneUsed()
    {
        var popups = Popups();
        popups.Open();
        popups.Open();
        popups.Open();

        Assert.True(popups.Close(2));
        Assert.Equal(2, popups.Open().Number);

        Assert.True(popups.Close(1));
        Assert.True(popups.Close(2));
        Assert.Equal(1, popups.Open().Number);
        Assert.Equal(2, popups.Open().Number);
        Assert.Equal(4, popups.Open().Number);
    }

    [Fact]
    public async Task ClosingOneEndsOnlyItsConversation()
    {
        var popups = Popups();
        var first = popups.Open();
        var second = popups.Open();
        await first.Session.AskAsync("one");
        await second.Session.AskAsync("two");

        var closed = new List<int>();
        popups.Closed += popup => closed.Add(popup.Number);

        Assert.True(popups.Close(1));

        Assert.Empty(first.Session.Lines);
        Assert.NotEmpty(second.Session.Lines);
        Assert.Equal([1], closed);
        Assert.Null(popups.Find(1));
        Assert.Same(second, popups.Find(2));
        Assert.Equal([2], popups.All.Select(p => p.Number));
    }

    [Fact]
    public void ClosingANumberThatIsNotOpenDoesNothing()
    {
        var popups = Popups();
        popups.Open();
        var closed = 0;
        popups.Closed += _ => closed++;

        Assert.False(popups.Close(5));
        Assert.Equal(0, closed);
        Assert.Single(popups.All);
    }

    [Fact]
    public async Task CloseAllEndsEveryConversation()
    {
        var popups = Popups();
        var all = new[] { popups.Open(), popups.Open(), popups.Open() };
        foreach (var popup in all)
            await popup.Session.AskAsync("hello");

        var closed = new List<int>();
        popups.Closed += popup => closed.Add(popup.Number);

        popups.CloseAll();

        Assert.Empty(popups.All);
        Assert.Equal([1, 2, 3], closed);
        Assert.All(all, popup => Assert.Empty(popup.Session.Lines));
    }

    [Fact]
    public void ThereIsNoLimit()
    {
        var popups = Popups();

        for (var i = 0; i < 50; i++)
            popups.Open();

        Assert.Equal(50, popups.All.Count);
        Assert.Equal(Enumerable.Range(1, 50), popups.All.Select(p => p.Number));
        Assert.Equal(51, popups.Open().Number);
    }

    [Fact]
    public void ANewPopupAfterCloseAllStartsAgainAtOne()
    {
        var popups = Popups();
        popups.Open();
        popups.Open();
        popups.CloseAll();

        Assert.Equal(1, popups.Open().Number);
    }

    [Fact]
    public void NumberOneOpensWhereThePanelAlwaysDid()
    {
        var baseline = OverlayPlacement.Default;

        Assert.Equal(baseline, ChatCascade.ForHeadset(baseline, 1));
        Assert.Equal((0, 0), ChatCascade.ForDesktop(1));
    }

    [Fact]
    public void EachNumberInAStaircaseSitsFurtherRightAndDownAndNearer()
    {
        var baseline = OverlayPlacement.Default;
        var spots = Enumerable.Range(1, ChatCascade.Steps).Select(n => ChatCascade.ForHeadset(baseline, n).Offset).ToList();

        for (var i = 1; i < spots.Count; i++)
        {
            Assert.True(spots[i].X > spots[i - 1].X);
            Assert.True(spots[i].Y < spots[i - 1].Y);
            Assert.True(spots[i].Z > spots[i - 1].Z);
        }

        Assert.Equal(baseline.Offset.X + ChatCascade.HeadsetStep, spots[1].X, 4);
    }

    [Fact]
    public void ANinthDoesNotStackOnTheFirst()
    {
        var baseline = OverlayPlacement.Default;
        var first = ChatCascade.ForHeadset(baseline, 1).Offset;
        var ninth = ChatCascade.ForHeadset(baseline, ChatCascade.Steps + 1).Offset;

        Assert.NotEqual(first, ninth);
        Assert.True(ninth.X > first.X);
        Assert.True(ninth.Y < first.Y);
        Assert.NotEqual((0, 0), ChatCascade.ForDesktop(ChatCascade.Steps + 1));
    }

    [Fact]
    public void ADesktopWindowStepsLeftAndDown()
    {
        Assert.Equal((-ChatCascade.DesktopStep, ChatCascade.DesktopStep), ChatCascade.ForDesktop(2));
        Assert.Equal((-2 * ChatCascade.DesktopStep, 2 * ChatCascade.DesktopStep), ChatCascade.ForDesktop(3));
    }

    [Fact]
    public void ThePlacementKeepsEverythingButTheOffset()
    {
        var baseline = OverlayPlacement.Default with { Width = 0.55f, Locked = true, Wide = 1.5f };
        var moved = ChatCascade.ForHeadset(baseline, 4);

        Assert.Equal(baseline.Anchor, moved.Anchor);
        Assert.Equal(baseline.Width, moved.Width);
        Assert.True(moved.Locked);
        Assert.Equal(1.5f, moved.Wide);
    }

    private sealed class EchoClient : IChatClient
    {
        public Task<bool?> GetAvailableAsync(ServerPairing pairing, CancellationToken cancellationToken)
            => Task.FromResult<bool?>(true);

        public Task<ChatSendResult> SendAsync(
            ServerPairing pairing, string text, Guid? conversationId, Action<string> onAnswer, CancellationToken cancellationToken)
        {
            onAnswer("ok");
            return Task.FromResult(new ChatSendResult(null, Guid.NewGuid()));
        }
    }
}
