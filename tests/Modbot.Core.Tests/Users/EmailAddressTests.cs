using Modbot.Core.Users;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// What counts as an email address, and what it is stored as (server info and account email
/// design §4.2, §4.3). Pure: no database, no host.
/// </summary>
public class EmailAddressTests
{
    [Theory]
    [InlineData("alice@example.com")]
    [InlineData("alice+modbot@example.co.uk")]
    [InlineData("a.b-c_d@mail.example.org")]
    [InlineData("MOD@Example.Com")]
    public void RealAddressesAreAccepted(string value) => Assert.True(EmailAddress.LooksLike(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gunner24")]                 // a Discord handle in the email box
    [InlineData("@example.com")]             // nothing before the @
    [InlineData("alice@")]                   // nothing after it
    [InlineData("alice@localhost")]          // no dot: real on a network, useless for a moderator
    [InlineData("alice@example.")]           // nothing after the dot
    [InlineData("alice@.com")]               // nothing before it
    [InlineData("alice@@example.com")]       // two @
    [InlineData("alice example@example.com")]
    public void ThingsThatAreNotAddressesAreRefused(string? value)
        => Assert.False(EmailAddress.LooksLike(value));

    [Fact]
    public void AnythingLongerThanTheColumnIsRefused()
    {
        var tooLong = new string('a', 250) + "@example.com";

        Assert.True(tooLong.Length > EmailAddress.MaximumLength);
        Assert.False(EmailAddress.LooksLike(tooLong));
    }

    [Fact]
    public void TheStoredFormIsTrimmedAndLowerCased()
        => Assert.Equal("alice@example.com", EmailAddress.Normalize("  Alice@Example.COM  "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\t  ")]
    public void NothingTypedIsNull(string? value) => Assert.Null(EmailAddress.Normalize(value));

    [Fact]
    public void ReadGivesBackTheStoredFormAndNoComplaint()
    {
        var (email, problem) = EmailAddress.Read(" Alice@Example.com ");

        Assert.Equal("alice@example.com", email);
        Assert.Null(problem);
    }

    [Fact]
    public void ReadDistinguishesNothingTypedFromSomethingWrong()
    {
        Assert.Equal(EmailAddress.Required, EmailAddress.Read("  ").Problem);
        Assert.Equal(EmailAddress.NotAnAddress, EmailAddress.Read("gunner24").Problem);

        // The address is never handed back alongside a complaint, so no caller can store one it
        // was told not to.
        Assert.Null(EmailAddress.Read("gunner24").Email);
    }
}
