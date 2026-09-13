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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyCollection<Guid> Viewer = [BuiltInRoles.ViewerId];
    private static readonly IReadOnlyCollection<Guid> NoRoles = [];

    [Fact]
    public async Task CreateAsync_StoresAHashAndNotThePassword()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();

        var user = await service.CreateAsync(name, "correct horse battery staple", Viewer, Ct);

        Assert.NotEqual("correct horse battery staple", user.PasswordHash);
        Assert.DoesNotContain("correct horse", user.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_RecordsCreationFromTheInjectedClock()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));
        await using var context = _db.NewContext();
        var service = Create(context, clock);

        var user = await service.CreateAsync(UniqueName(), "pw", NoRoles, Ct);

        Assert.Equal(clock.UtcNow, user.CreatedAt);
    }

    [Fact]
    public async Task TwoUsersWithTheSamePassword_GetDifferentHashes()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());

        var a = await service.CreateAsync(UniqueName(), "shared", NoRoles, Ct);
        var b = await service.CreateAsync(UniqueName(), "shared", NoRoles, Ct);

        Assert.NotEqual(a.PasswordHash, b.PasswordHash);
    }

    [Fact]
    public async Task VerifyCredentialsAsync_AcceptsTheRightPassword()
    {
        await using var context = _db.NewContext();
        var clock = new FakeClock();
        var service = Create(context, clock);
        var name = UniqueName();
        await service.CreateAsync(name, "hunter2", Viewer, Ct);

        clock.Advance(TimeSpan.FromHours(3));
        var verified = await service.VerifyCredentialsAsync(name, "hunter2", Ct);

        Assert.NotNull(verified);
        Assert.Equal(clock.UtcNow, verified.LastLoginAt);
    }

    [Fact]
    public async Task VerifyCredentialsAsync_RejectsTheWrongPassword()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();
        await service.CreateAsync(name, "hunter2", NoRoles, Ct);

        Assert.Null(await service.VerifyCredentialsAsync(name, "hunter3", Ct));
    }

    [Fact]
    public async Task VerifyCredentialsAsync_RejectsAnUnknownUser()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());

        Assert.Null(await service.VerifyCredentialsAsync(UniqueName(), "anything", Ct));
    }

    [Fact]
    public async Task VerifyCredentialsAsync_IsCaseInsensitiveOnTheUsername()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();
        await service.CreateAsync(name, "hunter2", NoRoles, Ct);

        Assert.NotNull(await service.VerifyCredentialsAsync(name.ToUpperInvariant(), "hunter2", Ct));
    }

    [Fact]
    public async Task CreateAsync_RejectsAUsernameThatDiffersOnlyInCase()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();
        await service.CreateAsync(name, "pw", NoRoles, Ct);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => service.CreateAsync(
            name.ToUpperInvariant(), "pw", NoRoles, Ct));
    }

    [Fact]
    public async Task VerifyCredentialsAsync_RejectsADisabledAccount()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var name = UniqueName();
        var user = await service.CreateAsync(name, "hunter2", NoRoles, Ct);

        user.IsDisabled = true;
        await context.SaveChangesAsync(Ct);

        Assert.Null(await service.VerifyCredentialsAsync(name, "hunter2", Ct));
    }

    // ── Roles (accounts and access design §3) ──────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_RefusesARoleIdThatNamesNothing()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());

        // Silently dropping it would create an account with fewer permissions than the
        // administrator chose, and nothing would say so.
        await Assert.ThrowsAsync<UnknownRoleException>(() =>
            service.CreateAsync(UniqueName(), "pw", [Guid.NewGuid()], Ct));
    }

    [Fact]
    public async Task EffectivePermissions_AreTheUnionOfTheRoles()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());

        var kickOnly = await TestAccounts.RoleForAsync(context, ModbotPermissions.Kick, Ct);
        var user = await service.CreateAsync(UniqueName(), "pw", [BuiltInRoles.ViewerId, kickOnly], Ct);

        var expected = BuiltInRoles.ViewerPermissions | ModbotPermissions.Kick;
        Assert.Equal(expected, user.EffectivePermissions);

        // And the same answer from the one read the session check makes per request.
        var state = await service.StateAsync(user.Id, Ct);
        Assert.Equal(expected, state!.Value.Permissions);
    }

    [Fact]
    public async Task SetRolesAsync_ReplacesRatherThanAdds()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var user = await service.CreateAsync(UniqueName(), "pw", [BuiltInRoles.ModeratorId], Ct);

        var (before, after) = await service.SetRolesAsync(user, [BuiltInRoles.ViewerId], Ct);

        Assert.Equal(["Moderator"], before);
        Assert.Equal(["Viewer"], after);
        Assert.Equal(BuiltInRoles.ViewerPermissions, (await service.StateAsync(user.Id, Ct))!.Value.Permissions);
    }

    [Fact]
    public async Task AnAccountWithNoRoles_HasNoPermissions()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var user = await service.CreateAsync(UniqueName(), "pw", NoRoles, Ct);

        Assert.Equal(ModbotPermissions.None, user.EffectivePermissions);
    }

    [Fact]
    public void Union_OfNothing_IsNone()
        => Assert.Equal(ModbotPermissions.None, ModbotRole.Union([]));

    [Fact]
    public void Union_IncludesEveryFlagFromEveryRole()
    {
        var union = ModbotRole.Union([ModbotPermissions.Kick, ModbotPermissions.Ban | ModbotPermissions.Kick, ModbotPermissions.ViewMembers]);

        Assert.Equal(ModbotPermissions.Kick | ModbotPermissions.Ban | ModbotPermissions.ViewMembers, union);
    }

    // ── Sessions (design §5) ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EndSessionsAsync_MovesTheCutOffToNow()
    {
        var clock = new FakeClock();
        await using var context = _db.NewContext();
        var service = Create(context, clock);
        var user = await service.CreateAsync(UniqueName(), "pw", NoRoles, Ct);

        clock.Advance(TimeSpan.FromMinutes(5));
        var cutOff = await service.EndSessionsAsync(user, Ct);

        Assert.Equal(clock.UtcNow, cutOff);
        Assert.Equal(cutOff, (await service.StateAsync(user.Id, Ct))!.Value.SessionsValidAfter);
    }

    [Fact]
    public async Task StateAsync_ReportsWhetherTheVRChatAccountIsLinked()
    {
        await using var context = _db.NewContext();
        var service = Create(context, new FakeClock());
        var user = await service.CreateAsync(UniqueName(), "pw", NoRoles, Ct);

        Assert.False((await service.StateAsync(user.Id, Ct))!.Value.VRChatLinked);

        await TestAccounts.LinkAsync(context, user.Id, $"usr_{Guid.NewGuid():N}", Ct);

        Assert.True((await service.StateAsync(user.Id, Ct))!.Value.VRChatLinked);
    }
}
