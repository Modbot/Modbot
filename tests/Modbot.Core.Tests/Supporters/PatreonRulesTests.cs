using System.Security.Cryptography;
using System.Text;
using Modbot.Core.Supporters;

namespace Modbot.Core.Tests.Supporters;

/// <summary>
/// Patreon's messages: the signature (good, wrong, missing), where each kind of message leaves the
/// patron, which tier is counted when several are entitled, and how a patron and a charge are named.
/// </summary>
public class PatreonRulesTests
{
    private const string Secret = "webhook-secret";

    private static byte[] Body(string text) => Encoding.UTF8.GetBytes(text);

    private static string Sign(byte[] body, string secret)
        => Convert.ToHexStringLower(HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), body));

    [Fact]
    public void ASignatureMadeWithTheSecret_IsGood_InLowerOrUpperCase()
    {
        var body = Body("""{"data":{"id":"m1"}}""");
        var signature = Sign(body, Secret);

        Assert.True(PatreonRules.IsSignedBy(body, signature, Secret));
        Assert.True(PatreonRules.IsSignedBy(body, signature.ToUpperInvariant(), Secret));
    }

    [Fact]
    public void ASignatureMadeWithAnotherSecretOrForAnotherBody_IsBad()
    {
        var body = Body("""{"data":{"id":"m1"}}""");

        Assert.False(PatreonRules.IsSignedBy(body, Sign(body, "another-secret"), Secret));
        Assert.False(PatreonRules.IsSignedBy(Body("""{"data":{"id":"m2"}}"""), Sign(body, Secret), Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not hex at all")]
    [InlineData("abcd")]
    public void ASignatureThatIsMissingOrIsNotTheRightHex_IsBad(string? signature)
    {
        Assert.False(PatreonRules.IsSignedBy(Body("{}"), signature, Secret));
    }

    [Fact]
    public void WithNoSecretSaved_NothingIsGood()
    {
        var body = Body("{}");

        Assert.False(PatreonRules.IsSignedBy(body, Sign(body, ""), ""));
        Assert.False(PatreonRules.IsSignedBy(body, Sign(body, Secret), null));
    }

    [Theory]
    [InlineData("members:pledge:create", "active_patron", PatreonStanding.Active)]
    [InlineData("members:pledge:update", "active_patron", PatreonStanding.Active)]
    [InlineData("members:create", "active_patron", PatreonStanding.Active)]
    [InlineData("members:update", "active_patron", PatreonStanding.Active)]
    [InlineData("members:update", "declined_patron", PatreonStanding.Declined)]
    [InlineData("members:pledge:update", "declined_patron", PatreonStanding.Declined)]
    [InlineData("members:pledge:delete", "former_patron", PatreonStanding.Ended)]
    [InlineData("members:update", "former_patron", PatreonStanding.Ended)]
    [InlineData(null, "active_patron", PatreonStanding.Active)]
    [InlineData("members:create", null, PatreonStanding.None)]
    [InlineData("members:update", "something_new", PatreonStanding.None)]
    public void EachMessageLeavesThePatronWhereTheStatusSays(string? trigger, string? status, PatreonStanding expected)
    {
        Assert.Equal(expected, PatreonRules.StandingOf(trigger, status));
    }

    [Theory]
    [InlineData("members:pledge:delete")]
    [InlineData("members:delete")]
    [InlineData(" Members:Delete ")]
    public void ADeleteEndsThePledge_WhateverTheStatusStillSays(string trigger)
    {
        Assert.Equal(PatreonStanding.Ended, PatreonRules.StandingOf(trigger, "active_patron"));
        Assert.Equal(PatreonStanding.Ended, PatreonRules.StandingOf(trigger, null));
    }

    [Fact]
    public void OfSeveralEntitledTiers_TheOneThatCostsTheMostIsCounted()
    {
        var message = Message("""
            {"data":{"id":"m1","relationships":{"currently_entitled_tiers":{"data":[{"id":"t1"},{"id":"t3"},{"id":"t2"}]}}},
             "included":[
               {"id":"t1","type":"tier","attributes":{"title":"Bronze","amount_cents":300}},
               {"id":"t2","type":"tier","attributes":{"title":"Silver","amount_cents":500}},
               {"id":"t3","type":"tier","attributes":{"title":"Gold","amount_cents":1500}},
               {"id":"t4","type":"tier","attributes":{"title":"Not entitled","amount_cents":9900}}]}
            """);

        var tier = message.EntitledTier();

        Assert.NotNull(tier);
        Assert.Equal("t3", tier.Id);
        Assert.Equal("Gold", tier.Title);
    }

    [Fact]
    public void WhenTheMessageDoesNotCarryTheTiers_TheFirstEntitledOneIsCountedWithNoTitle()
    {
        var message = Message("""{"data":{"id":"m1","relationships":{"currently_entitled_tiers":{"data":[{"id":"t9"},{"id":"t8"}]}}}}""");

        var tier = message.EntitledTier();

        Assert.NotNull(tier);
        Assert.Equal("t9", tier.Id);
        Assert.Null(tier.Title);
    }

    [Theory]
    [InlineData("""{"data":{"id":"m1","relationships":{"currently_entitled_tiers":{"data":[]}}}}""")]
    [InlineData("""{"data":{"id":"m1"}}""")]
    [InlineData("""{}""")]
    public void WithNoEntitledTier_NoneIsCounted(string json)
    {
        Assert.Null(Message(json).EntitledTier());
    }

    [Fact]
    public void TheMessageIsReadForTheMemberTheChargeAndTheName()
    {
        var message = Message("""
            {"data":{"id":"m1","attributes":{"patron_status":"Active_Patron","last_charge_date":"2026-10-01T04:00:00.000+00:00","last_charge_status":"Paid"},
              "relationships":{"user":{"data":{"id":"u1"}}}},
             "included":[{"id":"u1","type":"user","attributes":{"full_name":"  Sam Patron "}}]}
            """);

        Assert.Equal("m1", message.MemberId);
        Assert.Equal("active_patron", message.PatronStatus);
        Assert.Equal("Sam Patron", message.FullName);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 4, 0, 0, TimeSpan.Zero), message.LastPaidCharge);
    }

    [Theory]
    [InlineData("Declined")]
    [InlineData("Pending")]
    [InlineData("Refunded")]
    [InlineData(null)]
    public void AChargeThatWasNotPaid_IsNotAPaidCharge_ButItsDateIsKnown(string? status)
    {
        var statusJson = status is null ? "" : $",\"last_charge_status\":\"{status}\"";
        var message = Message("""{"data":{"id":"m1","attributes":{"last_charge_date":"2026-10-01T04:00:00Z"@STATUS@}}}""".Replace("@STATUS@", statusJson, StringComparison.Ordinal));

        Assert.Null(message.LastPaidCharge);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 4, 0, 0, TimeSpan.Zero), message.LastChargeDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void SomethingThatIsNotAPatreonMessage_IsNotRead(string json)
    {
        Assert.Null(PatreonMessage.Parse(Body(json)));
    }

    [Fact]
    public void APatronIsToldByTheMemberId_ApartFromAKofiPayerAndFromAnotherPatron()
    {
        Assert.Equal(PatreonRules.PayerIdOf("m1"), PatreonRules.PayerIdOf("m1"));
        Assert.NotEqual(PatreonRules.PayerIdOf("m1"), PatreonRules.PayerIdOf("m2"));
        Assert.NotEqual(PatreonRules.PayerIdOf("m1"), SupporterRules.PayerIdOf("kofi", null, null, "member:m1"));
    }

    [Fact]
    public void AChargeIsNamedByTheMemberAndTheMomentItWasMade()
    {
        var at = new DateTimeOffset(2026, 10, 1, 4, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal("m1|2026-10-01T02:00:00Z", PatreonRules.PaymentCode("m1", at));
        Assert.Equal(PatreonRules.PaymentCode("m1", at), PatreonRules.PaymentCode("m1", at.ToUniversalTime()));
        Assert.NotEqual(PatreonRules.PaymentCode("m1", at), PatreonRules.PaymentCode("m2", at));
    }

    private static PatreonMessage Message(string json)
        => PatreonMessage.Parse(Body(json)) ?? throw new InvalidOperationException("Not a message.");
}
