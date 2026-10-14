using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Core.Supporters;
using Modbot.Discord.Tests.Sync;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Supporters;

/// <summary>
/// The supporter code: six letters and digits with no look-alikes, the same code every time a person
/// asks, and found whole in a payment's message in any case.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SupporterCodesTests
{
    private readonly PostgresFixture _db;

    public SupporterCodesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ANewCode_IsSixCapitalsAndDigits_WithNoLookAlikes()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = SupporterCodes.Make();

            Assert.Equal(SupporterCodes.Length, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)));
            Assert.DoesNotContain(code, c => c is '0' or 'O' or '1' or 'I' or 'L');
        }
    }

    [Theory]
    [InlineData("K7Q2MX", "K7Q2MX")]
    [InlineData("k7q2mx", "K7Q2MX")]
    [InlineData("Thanks! (K7Q2MX) see you", "K7Q2MX")]
    [InlineData("my code:K7Q2MX.", "K7Q2MX")]
    [InlineData("line one\nK7Q2MX\nline three", "K7Q2MX")]
    public void ACodeInAMessage_IsFound_InAnyCase_AndNextToPunctuation(string message, string expected)
        => Assert.Contains(expected, SupporterCodes.CandidatesIn(message));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("toolong7")]
    [InlineData("K7Q2MXY")]
    public void AMessageWithNoSixCharacterWord_HasNoCandidates(string? message)
        => Assert.Empty(SupporterCodes.CandidatesIn(message));

    [Fact]
    public void TheSameCodeTwice_IsOneCandidate()
        => Assert.Single(SupporterCodes.CandidatesIn("K7Q2MX and k7q2mx"));

    [Fact]
    public async Task AskingAgain_GivesTheSameCode_AndTwoPeopleGetDifferentOnes()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var now = services.Clock.UtcNow;

        await using var db = services.Database.NewContext();
        var codes = new SupporterCodes(db);

        var first = await codes.ForAsync("7001", now, Ct);
        var again = await codes.ForAsync("7001", now, Ct);
        var other = await codes.ForAsync("7002", now, Ct);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Equal(2, await db.SupporterCodes.CountAsync(Ct));
    }

    [Fact]
    public async Task TheAccountOfACodeInAMessage_IsFound_AndNoneForAnUnknownCode()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);

        await using var db = services.Database.NewContext();
        var codes = new SupporterCodes(db);
        var code = await codes.ForAsync("7001", services.Clock.UtcNow, Ct);

        Assert.Equal("7001", await codes.DiscordUserIdInAsync($"hello {code.ToLowerInvariant()} there", Ct));
        Assert.Null(await codes.DiscordUserIdInAsync("hello there", Ct));
        Assert.Null(await codes.DiscordUserIdInAsync(null, Ct));
    }

    [Fact]
    public async Task ACodeRowThatAnotherPersonAlreadyHas_IsRefusedByTheDatabase()
    {
        await using var services = await SyncSetUp.CreateAsync(_db, null, Ct);
        var now = services.Clock.UtcNow;

        await using var db = services.Database.NewContext();
        db.SupporterCodes.Add(new SupporterCode { DiscordUserId = "7001", Code = "K7Q2MX", CreatedAt = now });
        await db.SaveChangesAsync(Ct);

        await using var other = services.Database.NewContext();
        other.SupporterCodes.Add(new SupporterCode { DiscordUserId = "7002", Code = "K7Q2MX", CreatedAt = now });

        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync(Ct));
    }
}
