using Modbot.Companion.Overlay;
using Modbot.Overlay.Rendering;
using SharpGen.Runtime;

namespace Modbot.Overlay.Tests.Rendering;

/// <summary>
/// Which graphics card the overlay's device is made on: SteamVR's when it says, else the real card
/// with the most memory of its own, never a pretend one, and Windows' own choice last.
/// </summary>
/// <remarks>
/// The cards are the ones on the PC where the companion stopped at start with "out of memory": a
/// headset card, a processor's built-in one with a small slice of memory, and Parsec's pretend
/// display card. Windows' default card on that PC was not reliably the headset's.
/// </remarks>
public class GraphicsCardChoiceTests
{
    private static readonly GraphicsCard Parsec = new(0, "Parsec Virtual Display Adapter", 0, 300);
    private static readonly GraphicsCard BuiltIn = new(1, "AMD Radeon(TM) Graphics", 2UL << 30, 100);
    private static readonly GraphicsCard Headset = new(2, "NVIDIA GeForce RTX 5090", 32UL << 30, 200);
    private static readonly GraphicsCard BasicRender = new(3, "Microsoft Basic Render Driver", 0, 400, Software: true);

    private static readonly GraphicsCard[] OwnersPc = [Parsec, BuiltIn, Headset, BasicRender];

    [Fact]
    public void SteamVrsCardComesFirst()
    {
        var order = GraphicsCardChoice.Order(OwnersPc, steamVrCard: BuiltIn.Id);

        Assert.Equal([BuiltIn, Headset, null], order);
    }

    [Fact]
    public void WithoutSteamVrTheCardWithTheMostMemoryComesFirst()
    {
        var order = GraphicsCardChoice.Order(OwnersPc, steamVrCard: null);

        Assert.Equal([Headset, BuiltIn, null], order);
    }

    [Fact]
    public void ACardSteamVrNamesThatWindowsDidNotListIsPassedOver()
    {
        var order = GraphicsCardChoice.Order(OwnersPc, steamVrCard: 999);

        Assert.Equal([Headset, BuiltIn, null], order);
    }

    [Fact]
    public void PretendAndSoftwareCardsAreSkipped()
    {
        Assert.False(GraphicsCardChoice.IsReal(Parsec));
        Assert.False(GraphicsCardChoice.IsReal(BasicRender));
        Assert.False(GraphicsCardChoice.IsReal(new GraphicsCard(4, "Microsoft Remote Display Adapter", 0, 500)));
        Assert.False(GraphicsCardChoice.IsReal(new GraphicsCard(5, "Some Card", 1UL << 30, 600, Remote: true)));
        Assert.False(GraphicsCardChoice.IsReal(new GraphicsCard(6, "Virtual Display Device", 1UL << 30, 700)));
        Assert.True(GraphicsCardChoice.IsReal(Headset));
        Assert.True(GraphicsCardChoice.IsReal(BuiltIn));
    }

    [Fact]
    public void WithNoCardListedOnlyWindowsChoiceIsTried()
    {
        Assert.Equal([null], GraphicsCardChoice.Order([], steamVrCard: 200));
    }

    [Fact]
    public void ASingleCardPcTriesThatCardThenWindowsChoice()
    {
        var only = new GraphicsCard(0, "NVIDIA GeForce RTX 3060", 12UL << 30, 1);

        Assert.Equal([only, null], GraphicsCardChoice.Order([only], steamVrCard: null));
    }

    [Fact]
    public void EqualMemoryKeepsWindowsOrder()
    {
        var first = new GraphicsCard(0, "Card A", 8UL << 30, 1);
        var second = new GraphicsCard(1, "Card B", 8UL << 30, 2);

        Assert.Equal([first, second, null], GraphicsCardChoice.Order([second, first], steamVrCard: null));
    }

    [Fact]
    public void OnlyOneWaitAndOnlyAfterOutOfMemory()
    {
        Assert.True(GraphicsCardChoice.WaitBeforeNext(outOfMemory: true, waitedAlready: false));
        Assert.False(GraphicsCardChoice.WaitBeforeNext(outOfMemory: true, waitedAlready: true));
        Assert.False(GraphicsCardChoice.WaitBeforeNext(outOfMemory: false, waitedAlready: false));
    }

    /// <summary>
    /// The exact exception the companion stopped on, read as the panel's plain reason. SharpGen
    /// carries the HRESULT on the exception, which is what the reason is read from.
    /// </summary>
    [Fact]
    public void SharpGensOutOfMemoryReadsAsThePlainReason()
    {
        var thrown = new SharpGenException(new Result(unchecked((int)0x8007000E)));

        Assert.True(PanelRetries.IsPanelFailure(thrown));
        Assert.Equal("the graphics card ran out of memory", PanelRetries.ShortReason(thrown));
    }
}
