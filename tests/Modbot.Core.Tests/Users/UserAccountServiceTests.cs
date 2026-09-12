using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.Core.Tests.Data;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Users;

[Collection(nameof(PostgresCollection))]
public class UserAccountServiceTests
{
    private readonly PostgresFixture _db;

    public UserAccountServiceTests(PostgresFixture db) => _db = db;

    private static UserAccountService Create(Modbot.Core.Data.ModbotContext context, FakeClock clock)
        => new(context, new PasswordHasher<ModbotUser>(), clock);

    /// <summary>Unique per call: the container is shared, so the user table persists across tests.</summary>
    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    [Fact]
    public async Task CreateAsync_StoresAHashAndNotThePassword()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();

        var user = await service.CreateAsync(
            name, "correct horse battery staple", ModbotPermissions.ViewMembers,
            TestContext.Current.CancellationToken);

        Assert.NotEqual("correct horse battery staple", user.PasswordHash);
        Assert.DoesNotContain("correct horse", user.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RecordsCreationFromTheInjectedClock()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        await using var context = _db.NewContext();
        var service = Create(context, clock);

        var user = await service.CreateAsync(
            UniqueName(), "pw", ModbotPermissions.None, TestContext.Current.CancellationToken);

        Assert.Equal(clock.UtcNow, user.CreatedAt);
    }

    [Fact]
    public async Task TwoUsersWithTheSamePassword_GetDifferentHashes()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());

        var a = await service.CreateAsync(UniqueName(), "shared", ModbotPermissions.None, TestContext.Current.CancellationToken);
        var b = await service.CreateAsync(UniqueName(), "shared", ModbotPermissions.None, TestContext.Current.CancellationToken);

        Assert.NotEqual(a.PasswordHash, b.PasswordHash);
    }

    [Fact]
    public async Task VerifyCredentialsAsync_AcceptsTheRightPassword()
    {
        await using var context = _db.NewContext();
        var clock = new FakeClock();
        var service = Create(context, clock);
        var name = UniqueName();
        await service.CreateAsync(name, "hunter2", ModbotPermissions.ViewMembers, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromHours(3));
        var verified = await service.VerifyCredentialsAsync(name, "hunter2", TestContext.Current.CancellationToken);

        Assert.NotNull(verified);
        Assert.Equal(clock.UtcNow, verified.LastLoginAt);
    }

    [Fact]
    public async Task VerifyCredentialsAsync_RejectsTheWrongPassword()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();
        await service.CreateAsync(name, "hunter2", ModbotPermissions.None, TestContext.Current.CancellationToken);

        Assert.Null(await service.VerifyCredentialsAsync(name, "hunter3", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyCredentialsAsync_RejectsAnUnknownUser()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());

        Assert.Null(await service.VerifyCredentialsAsync(
            UniqueName(), "anything", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyCredentialsAsync_IsCaseInsensitiveOnTheUsername()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();
        await service.CreateAsync(name, "hunter2", ModbotPermissions.None, TestContext.Current.CancellationToken);

        Assert.NotNull(await service.VerifyCredentialsAsync(
            name.ToUpperInvariant(), "hunter2", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAsync_RejectsAUsernameThatDiffersOnlyInCase()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();
        await service.CreateAsync(name, "pw", ModbotPermissions.None, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => service.CreateAsync(
            name.ToUpperInvariant(), "pw", ModbotPermissions.None, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyCredentialsAsync_RejectsADisabledAccount()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();
        var user = await service.CreateAsync(name, "hunter2", ModbotPermissions.None, TestContext.Current.CancellationToken);

        user.IsDisabled = true;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await service.VerifyCredentialsAsync(name, "hunter2", TestContext.Current.CancellationToken));
    }
}
