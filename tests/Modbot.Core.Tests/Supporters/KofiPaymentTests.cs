using Modbot.Core.Supporters;

namespace Modbot.Core.Tests.Supporters;

/// <summary>Reading Ko-fi's message: fields may be missing, and the Discord account may be text or a number.</summary>
public class KofiPaymentTests
{
    [Fact]
    public void AMonthlyPaymentIsRead()
    {
        var payment = KofiPayment.Parse("""
            {"verification_token":"t","type":"Subscription","from_name":"Sam","email":"sam@example.com",
             "is_subscription_payment":true,"kofi_transaction_id":"abc-1","tier_name":"Gold",
             "timestamp":"2026-10-01T12:00:00Z","discord_userid":"1234"}
            """);

        Assert.NotNull(payment);
        Assert.Equal("t", payment.VerificationToken);
        Assert.True(payment.IsSubscriptionPayment);
        Assert.Equal("abc-1", payment.TransactionId);
        Assert.Equal("Gold", payment.TierName);
        Assert.Equal("1234", payment.DiscordUserId);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), payment.PaidAt());
    }

    [Fact]
    public void ADiscordAccountSentAsANumberIsStillRead()
        => Assert.Equal("1234567890123456789", KofiPayment.Parse("""{"discord_userid":1234567890123456789}""")!.DiscordUserId);

    [Fact]
    public void TheMessageIsRead_AndAMissingOrNullOneIsNull()
    {
        Assert.Equal("my code K7Q2MX", KofiPayment.Parse("""{"message":"my code K7Q2MX"}""")!.Message);
        Assert.Null(KofiPayment.Parse("""{"message":null}""")!.Message);
        Assert.Null(KofiPayment.Parse("""{}""")!.Message);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"discord_userid":null}""")]
    [InlineData("""{"discord_userid":""}""")]
    [InlineData("""{"discord_userid":"  "}""")]
    [InlineData("""{"discord_userid":true}""")]
    public void NoUsableDiscordAccountIsNull(string json)
        => Assert.Null(KofiPayment.Parse(json)!.DiscordUserId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void SomethingThatIsNotAMessageIsNull(string? json)
        => Assert.Null(KofiPayment.Parse(json));

    [Fact]
    public void AMissingOrUnreadableTimeIsNull()
    {
        Assert.Null(KofiPayment.Parse("""{}""")!.PaidAt());
        Assert.Null(KofiPayment.Parse("""{"timestamp":"yesterday-ish"}""")!.PaidAt());
    }
}
