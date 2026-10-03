using Modbot.Core.Users;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// The <c>/verify</c> code as people see and type it (Discord account linking design §14): six
/// characters nobody misreads, shown with a dash, taken with or without one in either case.
/// </summary>
public class StaffDiscordCodesTests
{
    [Fact]
    public void TheAlphabet_HasNothingThatReadsAsSomethingElse()
    {
        foreach (var c in "01ILOU")
            Assert.DoesNotContain(c, StaffDiscordCodes.Alphabet);
    }

    [Fact]
    public void ACode_IsShownWithADashInTheMiddle()
    {
        Assert.Equal("K7P-42Q", StaffDiscordCodes.Show("K7P42Q"));
    }

    [Theory]
    [InlineData("K7P-42Q")]
    [InlineData("k7p-42q")]
    [InlineData("K7P42Q")]
    [InlineData("  K7P 42Q ")]
    [InlineData("K7P–42Q")]
    [InlineData("/verify K7P-42Q")]
    [InlineData("code:K7P-42Q")]
    public void WhatIsTyped_IsTakenWithOrWithoutTheDash_InEitherCase(string typed)
    {
        Assert.Equal("K7P42Q", StaffDiscordCodes.Normalize(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K7P-42")]
    [InlineData("K7P-42QQ")]
    [InlineData("K7P-4OQ")]
    [InlineData("K7P-41Q")]
    [InlineData("/lookup K7P-42Q")]
    public void WhatCannotBeACode_IsNothing(string? typed)
    {
        Assert.Null(StaffDiscordCodes.Normalize(typed));
    }
}
