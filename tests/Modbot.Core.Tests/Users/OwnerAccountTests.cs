using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Tests.Data;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// Who counts as the person running this Modbot (server info and account email design §2.3): the
/// oldest enabled account holding Administrator that has an email address.
/// </summary>
/// <remarks>
/// The user table is shared by the whole assembly, so each test empties it first: the rule is
/// about which of several accounts wins, and an account another test left behind would be one of
/// the several.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class OwnerAccountTests
{
    private readonly PostgresFixture _db;

    public OwnerAccountTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    [Fact]
    public async Task TheOldestAdministratorWithAnAddressWins()
    {
        await using var context = await EmptyAsync();

        await AddAsync(context, "first@example.com", ModbotPermissions.Administrator, days: 0);
        await AddAsync(context, "second@example.com", ModbotPermissions.Administrator, days: 3);

        Assert.Equal("first@example.com", await OwnerAccount.EmailAsync(context, Ct));
    }

    [Fact]
    public async Task ADisabledAdministratorIsNotTheOwner()
    {
        await using var context = await EmptyAsync();

        await AddAsync(context, "gone@example.com", ModbotPermissions.Administrator, days: 0, disabled: true);
        await AddAsync(context, "here@example.com", ModbotPermissions.Administrator, days: 5);

        // Somebody who has been disabled is not somebody to write to.
        Assert.Equal("here@example.com", await OwnerAccount.EmailAsync(context, Ct));
    }

    [Fact]
    public async Task AnAdministratorWithNoAddressIsSkipped()
    {
        await using var context = await EmptyAsync();

        await AddAsync(context, null, ModbotPermissions.Administrator, days: 0);
        await AddAsync(context, "reachable@example.com", ModbotPermissions.Administrator, days: 9);

        Assert.Equal("reachable@example.com", await OwnerAccount.EmailAsync(context, Ct));
    }

    [Fact]
    public async Task SomebodyWhoIsNotAnAdministratorIsNeverTheOwner()
    {
        await using var context = await EmptyAsync();

        // Older, enabled, has an address, and manages users -- and still not the owner, because
        // the permission checked is Administrator itself and is never expanded (spec 7.3).
        await AddAsync(context, "manager@example.com", ModbotPermissions.ManageUsers, days: 0);
        await AddAsync(context, "boss@example.com", ModbotPermissions.Administrator, days: 4);

        Assert.Equal("boss@example.com", await OwnerAccount.EmailAsync(context, Ct));
    }

    [Fact]
    public async Task NoAdministratorWithAnAddressMeansNoOwner()
    {
        await using var context = await EmptyAsync();

        await AddAsync(context, "viewer@example.com", ModbotPermissions.ViewMembers, days: 0);

        Assert.Null(await OwnerAccount.EmailAsync(context, Ct));
    }

    [Fact]
    public async Task TheSynchronousReadGivesTheSameAnswer()
    {
        await using var context = await EmptyAsync();

        await AddAsync(context, "first@example.com", ModbotPermissions.Administrator, days: 0);
        await AddAsync(context, "second@example.com", ModbotPermissions.Administrator, days: 2);

        // The VRChat client factory reads it from a synchronous property; both readers must agree.
        Assert.Equal(await OwnerAccount.EmailAsync(context, Ct), OwnerAccount.Email(context));
    }

    private async Task<ModbotContext> EmptyAsync()
    {
        var context = _db.NewContext();
        await context.Users.ExecuteDeleteAsync(Ct);
        return context;
    }

    /// <param name="days">How long after the first account this one was made.</param>
    private static async Task AddAsync(
        ModbotContext context,
        string? email,
        ModbotPermissions permissions,
        int days,
        bool disabled = false)
    {
        var clock = new FakeClock();
        var service = new UserAccountService(context, new PasswordHasher<ModbotUser>(), clock);
        var roleId = await TestAccounts.RoleForAsync(context, permissions, TestContext.Current.CancellationToken);

        var user = await service.CreateAsync(
            UniqueName(),
            "a-long-enough-password",
            email ?? $"placeholder_{Guid.NewGuid():N}@example.com",
            [roleId],
            TestContext.Current.CancellationToken);

        // Set afterwards rather than passed in: creation requires an address (design §4.1), and
        // "an administrator who has none" is the state a deployment upgrades into, not one this
        // build can be asked to create.
        user.Email = email;
        user.IsDisabled = disabled;
        user.CreatedAt = clock.UtcNow.AddDays(days);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
