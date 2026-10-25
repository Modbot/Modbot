using Modbot.Core.AutomaticRoles;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Tests.Data;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.AutomaticRoles;

/// <summary>The record of the automatic roles Modbot gave: open, find, close, last taken.</summary>
[Collection(nameof(PostgresCollection))]
public class AutomaticRoleGrantStoreTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _db;

    public AutomaticRoleGrantStoreTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task AGrantIsOpenUntilModbotTakesTheRoleAndThenTheTimeIsRemembered()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = Guid.NewGuid();
        var role = $"grol_{Guid.NewGuid()}";
        var rule = Guid.NewGuid();

        await using var context = _db.NewContext();
        var store = new AutomaticRoleGrantStore(context);

        Assert.Null(await store.FindOpenAsync(user, AutomaticRoleKind.VRChat, role, ct));
        Assert.Null(await store.LastTakenAsync(user, AutomaticRoleKind.VRChat, role, ct));

        var grant = await store.OpenAsync(rule, user, AutomaticRoleKind.VRChat, role, Noon, ct);
        var again = await store.OpenAsync(Guid.NewGuid(), user, AutomaticRoleKind.VRChat, role, Noon.AddSeconds(5), ct);

        Assert.Equal(grant.Id, again.Id);
        Assert.Equal(grant.Id, (await store.FindOpenAsync(user, AutomaticRoleKind.VRChat, role, ct))!.Id);
        Assert.Null(await store.FindOpenAsync(user, AutomaticRoleKind.Discord, role, ct));

        await store.RecordTakeAsync(rule, user, AutomaticRoleKind.VRChat, role, Noon.AddMinutes(10), "Clips turned off", ct);

        Assert.Null(await store.FindOpenAsync(user, AutomaticRoleKind.VRChat, role, ct));
        Assert.Equal(Noon.AddMinutes(10), await store.LastTakenAsync(user, AutomaticRoleKind.VRChat, role, ct));

        var reopened = await store.OpenAsync(rule, user, AutomaticRoleKind.VRChat, role, Noon.AddMinutes(12), ct);
        Assert.NotEqual(grant.Id, reopened.Id);
    }

    [Fact]
    public async Task TakingAHandGivenRoleStillRemembersWhenItWasTaken()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = Guid.NewGuid();
        var role = $"role_{Guid.NewGuid()}";

        await using var context = _db.NewContext();
        var store = new AutomaticRoleGrantStore(context);

        await store.RecordTakeAsync(Guid.NewGuid(), user, AutomaticRoleKind.Discord, role, Noon, "Daily ban cap reached", ct);

        Assert.Null(await store.FindOpenAsync(user, AutomaticRoleKind.Discord, role, ct));
        Assert.Equal(Noon, await store.LastTakenAsync(user, AutomaticRoleKind.Discord, role, ct));
    }
}
