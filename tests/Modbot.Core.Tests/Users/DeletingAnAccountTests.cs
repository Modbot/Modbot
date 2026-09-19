using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Core.Tests.Data;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// Deleting an account by emptying it, and what the character rule does to accounts that were made
/// before it existed (username rules and deleting accounts design §2, §3).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DeletingAnAccountTests
{
    private readonly PostgresFixture _db;

    public DeletingAnAccountTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UserAccountService Create(Modbot.Core.Data.ModbotContext context, FakeClock clock)
        => new(context, new PasswordHasher<ModbotUser>(), clock);

    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    private static string UniqueEmail() => $"u_{Guid.NewGuid():N}@test.example";

    [Fact]
    public async Task DeletingReplacesEverythingThatSaysWhoTheAccountBelongedTo()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        await using var context = _db.NewContext();
        var service = Create(context, clock);

        var user = await service.CreateAsync(UniqueName(), "a-long-enough-password", UniqueEmail(), [BuiltInRoles.ViewerId], Ct);
        var id = user.Id;
        user.DiscordUserId = "123456789012345678";
        user.VRChatUserId = $"usr_{Guid.NewGuid()}";
        user.VRChatDisplayName = "Someone";
        user.VRChatLinkedAt = clock.UtcNow;
        user.VRChatLinkCode = "modbot-1234";
        user.VRChatLinkPendingUserId = "usr_pending";
        user.VRChatLinkChecks = 3;
        await context.SaveChangesAsync(Ct);

        var name = await service.DeleteAsync(user, Ct);

        Assert.Equal(DeletedAccount.NameFor(id), name);
        Assert.Equal(id, user.Id);
        Assert.Equal(name, user.Username);
        Assert.Equal(UserAccountService.Normalize(name), user.UsernameNormalized);
        Assert.Null(user.Email);
        Assert.Null(user.DiscordUserId);
        Assert.Null(user.VRChatUserId);
        Assert.Null(user.VRChatDisplayName);
        Assert.Null(user.VRChatLinkedAt);
        Assert.Null(user.VRChatLinkCode);
        Assert.Null(user.VRChatLinkPendingUserId);
        Assert.Equal(0, user.VRChatLinkChecks);
        Assert.Empty(user.Roles);
        Assert.True(user.IsDisabled);
        Assert.True(user.IsDeleted);
        Assert.Equal(clock.UtcNow, user.DeletedAt);
        Assert.Equal(clock.UtcNow, user.SessionsValidAfter);
    }

    [Fact]
    public async Task ADeletedAccountCannotSignInWithItsOldPassword()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();

        var user = await service.CreateAsync(name, "a-long-enough-password", UniqueEmail(), [], Ct);
        await service.DeleteAsync(user, Ct);

        Assert.Null(await service.VerifyCredentialsAsync(name, "a-long-enough-password", Ct));
    }

    /// <summary>The row stays where it is, so everything that names it still finds it.</summary>
    [Fact]
    public async Task TheRowIsStillThereUnderItsOwnId()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());

        var user = await service.CreateAsync(UniqueName(), "a-long-enough-password", UniqueEmail(), [], Ct);
        var id = user.Id;
        await service.DeleteAsync(user, Ct);

        await using var fresh = _db.NewContext();
        var row = await fresh.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, Ct);

        Assert.NotNull(row);
        Assert.Equal(DeletedAccount.NameFor(id), row.Username);
    }

    [Fact]
    public async Task TwoDeletedAccountsDoNotCollide()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());

        var one = await service.CreateAsync(UniqueName(), "a-long-enough-password", UniqueEmail(), [], Ct);
        var two = await service.CreateAsync(UniqueName(), "a-long-enough-password", UniqueEmail(), [], Ct);

        var first = await service.DeleteAsync(one, Ct);
        var second = await service.DeleteAsync(two, Ct);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// The character rule applies when a username is set. An account made before it existed may
    /// hold anything, and it signs in exactly as it always did.
    /// </summary>
    [Fact]
    public async Task AnAccountWithAnOldStyleUsernameCanStillSignIn()
    {
        var clock = new FakeClock();
        await using var context = _db.NewContext();
        var service = Create(context, clock);
        var hasher = new PasswordHasher<ModbotUser>();

        // Straight into the table, the way a deployment from before the rule holds it.
        var old = new ModbotUser
        {
            Username = $"Old Name.{Guid.NewGuid():N}",
            Email = UniqueEmail(),
            CreatedAt = clock.UtcNow,
        };
        old.UsernameNormalized = UserAccountService.Normalize(old.Username);
        old.PasswordHash = hasher.HashPassword(old, "a-long-enough-password");
        context.Users.Add(old);
        await context.SaveChangesAsync(Ct);

        Assert.False(UsernameRules.LooksLike(old.Username));

        var byName = await service.VerifyCredentialsAsync(old.Username, "a-long-enough-password", Ct);
        Assert.NotNull(byName);
        Assert.Equal(old.Id, byName.Id);

        var byEmail = await service.VerifyCredentialsAsync(old.Email!, "a-long-enough-password", Ct);
        Assert.NotNull(byEmail);
        Assert.Equal(old.Id, byEmail.Id);
    }

    /// <summary>
    /// The backstop under the four slices that check the username first: nothing can make an
    /// account with a name the rule refuses.
    /// </summary>
    [Fact]
    public async Task CreateAsyncRefusesANameTheRuleWouldNotAccept()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync("bad name", "a-long-enough-password", UniqueEmail(), [], Ct));
    }
}
