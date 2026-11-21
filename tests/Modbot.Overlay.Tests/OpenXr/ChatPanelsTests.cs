using Modbot.Overlay.OpenVr;
using Modbot.Overlay.OpenXr;
using Modbot.Overlay.Rendering;
using Modbot.Overlay.Tests.OpenVr;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// Pop-up chats past the first over one OpenXR session: each is a panel of its own, made when it
/// is asked for, with no limit on how many.
/// </summary>
/// <remarks>
/// These run on a machine with no OpenXR loader, which is where CI is. The swapchains themselves
/// are made by the frame thread on a headset and are not covered here.
/// </remarks>
[Collection(OpenVrCollection.Name)]
public class ChatPanelsTests
{
    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    [InlineData(40, 42)]
    public void EachChatNumberSitsAfterTheFourFixedPanels(int number, int index)
        => Assert.Equal(index, OpenXrOverlayRuntime.ChatPanelIndexFor(number));

    [Fact]
    public void ChatOneIsTheChatPanelAndTheOthersAreNew()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8, buttonResolution: 4, chatResolution: 6);

        Assert.Same(runtime.ChatPanel, runtime.ChatPanelFor(1));
        Assert.NotSame(runtime.ChatPanel, runtime.ChatPanelFor(2));
        Assert.Equal(5, runtime.PanelsMade);
    }

    [Fact]
    public void AskingForAHighNumberFirstMakesRoomForEveryOneBelowIt()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8, buttonResolution: 4, chatResolution: 6);
        using var picture = new FakeSurface(6);

        var five = runtime.ChatPanelFor(5);
        var three = runtime.ChatPanelFor(3);

        Assert.Equal(OpenXrOverlayRuntime.ChatPanelIndexFor(5) + 1, runtime.PanelsMade);
        Assert.True(five.Submit(picture));
        Assert.True(three.Submit(picture));
    }

    [Fact]
    public void ANewChatPanelKeepsTheChatTextureSizeAndTheOthersKeepTheirs()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8, buttonResolution: 4, chatResolution: 6);
        using var chatSize = new FakeSurface(6);
        using var mainSize = new FakeSurface(16);

        var two = runtime.ChatPanelFor(2);

        Assert.True(two.Submit(chatSize));
        Assert.False(two.Submit(mainSize));
        Assert.True(runtime.Submit(mainSize));
        Assert.True(runtime.ChatPanel.Submit(chatSize));
    }

    [Fact]
    public void AChatPanelPastTheFirstGetsAStateRatherThanAnExceptionWithNoOpenXr()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8, buttonResolution: 4, chatResolution: 6);

        var status = runtime.ChatPanelFor(3).Start();

        Assert.NotEqual(OverlayRuntimeState.Running, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Detail));
    }

    [Fact]
    public void ShowingAndHidingOneChatDoesNothingToTheOther()
    {
        using var runtime = new OpenXrOverlayRuntime(16, notificationResolution: 8, buttonResolution: 4, chatResolution: 6);

        var two = runtime.ChatPanelFor(2);
        var three = runtime.ChatPanelFor(3);

        two.Hide();
        three.Show();
        two.Show();
    }

    private sealed class FakeSurface(int size) : IOverlaySurface
    {
        private readonly byte[] _pixels = new byte[size * size * 4];

        public int Width => size;

        public int Height => size;

        public nint TextureHandle => 0;

        public ReadOnlyMemory<byte> Pixels => _pixels;

        public void Upload(ReadOnlySpan<byte> bgra)
        {
        }

        public void Dispose()
        {
        }
    }
}
