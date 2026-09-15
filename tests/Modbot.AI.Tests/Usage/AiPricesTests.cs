using Modbot.AI.Usage;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Tests.Usage;

/// <summary>What a call costs from the saved price (AI chat design §10).</summary>
public class AiPricesTests
{
    [Fact]
    public void WithNoPrice_TheCostIsUnknown_NotZero()
    {
        Assert.Null(AiPrices.CostOf(null, 1000, 0, 1000));
    }

    [Fact]
    public void CachedInput_IsTakenOutOfTheInputCount_AndChargedAtItsOwnPrice()
    {
        var price = new AiModelPrice { Model = "m", InputPerMillion = 2m, CachedInputPerMillion = 0.5m, OutputPerMillion = 8m };

        // 3M input of which 1M cached, 0.5M output: 2M x $2 + 1M x $0.50 + 0.5M x $8.
        Assert.Equal(8.5m, AiPrices.CostOf(price, 3_000_000, 1_000_000, 500_000));
    }

    [Fact]
    public void WithNoCachedPrice_CachedInputCostsTheSameAsOtherInput()
    {
        var price = new AiModelPrice { Model = "m", InputPerMillion = 2m, OutputPerMillion = 0m };

        Assert.Equal(6m, AiPrices.CostOf(price, 3_000_000, 1_000_000, 0));
    }

    [Theory]
    [InlineData(2026, 9, 15, 23, 59, 2026, 9, 15, 2026, 9, 1)]
    [InlineData(2026, 10, 1, 0, 0, 2026, 10, 1, 2026, 10, 1)]
    public void DaysAndMonthsStartAtMidnightUtc(int y, int mo, int d, int h, int mi, int dy, int dmo, int dd, int my, int mmo, int md)
    {
        var (day, month) = AiSpendLimits.PeriodsAt(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(dy, dmo, dd, 0, 0, 0, TimeSpan.Zero), day);
        Assert.Equal(new DateTimeOffset(my, mmo, md, 0, 0, 0, TimeSpan.Zero), month);
    }
}
