using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// How many times its ordinary size a desktop window may be dragged to: never smaller than ordinary,
/// at most twice, and a number that is not a number is the ordinary size.
/// </summary>
public class DesktopPanelSizeTests
{
    [Theory]
    [InlineData(0.4, 1)]
    [InlineData(1, 1)]
    [InlineData(1.6, 1.6)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    public void ATimesNumberStaysBetweenOneAndTwo(double asked, double expected)
    {
        Assert.Equal(expected, DesktopPanelSize.Clamp(asked));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ANumberThatIsNotOneIsTheOrdinarySize(double asked)
    {
        Assert.Equal(1, DesktopPanelSize.Clamp(asked));
    }

    [Theory]
    [InlineData(520, 520, 1)]
    [InlineData(780, 520, 1.5)]
    [InlineData(300, 520, 1)]
    [InlineData(2000, 520, 2)]
    public void ASizeIsHowManyTimesTheOrdinaryOne(double size, double ordinary, double expected)
    {
        Assert.Equal(expected, DesktopPanelSize.TimesOrdinary(size, ordinary));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void AnOrdinarySizeThatIsNotASizeGivesOne(double ordinary)
    {
        Assert.Equal(1, DesktopPanelSize.TimesOrdinary(800, ordinary));
    }
}
