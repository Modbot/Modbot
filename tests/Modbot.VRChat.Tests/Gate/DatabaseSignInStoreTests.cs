using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.TestSupport;
using Modbot.VRChat.Session;
using Modbot.VRChat.Tests.Fakes;
using VRChat.API.Client;

namespace Modbot.VRChat.Tests.Gate;

/// <summary>
/// Spec 4.1.2's restart guarantee, against the database it depends on: a new process reads the
/// hour's sign-ins and the wait the old one left.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DatabaseSignInStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IsolatedDatabase _database = null!;

    public async ValueTask InitializeAsync() =>
        _database = await IsolatedDatabase.CreateAsync(fixture, Ct);

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task TheHoursSignInsAndTheWaitSurviveTheProcess()
    {
        var clock = new FakeClock();
        var store = NewStore();

        await store.RecordAttemptAsync(clock.UtcNow - TimeSpan.FromHours(2), "GetCurrentUser", Ct);
        await store.RecordAttemptAsync(clock.UtcNow - TimeSpan.FromMinutes(20), "GetCurrentUser", Ct);
        await store.RecordAttemptAsync(clock.UtcNow - TimeSpan.FromMinutes(19), "Verify2FA", Ct);
        await store.SaveWaitAsync(new SignInWait(SignInWaitReason.RateLimitedByVRChat, clock.UtcNow.AddMinutes(40)), Ct);
        await store.RecordSignedInAsync(clock.UtcNow - TimeSpan.FromMinutes(19), Ct);

        var restarted = await NewStore().LoadAsync(clock.UtcNow - SignInBudget.Window, Ct);

        Assert.Equal(2, restarted.Attempts.Count);
        Assert.Equal(new SignInWait(SignInWaitReason.RateLimitedByVRChat, clock.UtcNow.AddMinutes(40)), restarted.Wait);
        Assert.Equal(clock.UtcNow - TimeSpan.FromMinutes(19), restarted.LastSignedInAt);

        await store.SaveWaitAsync(null, Ct);
        Assert.Null((await NewStore().LoadAsync(clock.UtcNow - SignInBudget.Window, Ct)).Wait);
    }

    [Fact]
    public async Task AGateStartedDuringAWaitSendsNothing()
    {
        var clock = new FakeClock();
        await NewStore().SaveWaitAsync(new SignInWait(SignInWaitReason.RateLimitedByVRChat, clock.UtcNow.AddMinutes(59)), Ct);

        var vrchat = new FakeVRChat().AlwaysSignedInAs();
        var gate = new VRChatGate(
            new FakeClientFactory(vrchat.Client),
            new FakeConnectionStore(new VRChatConnection("modbot@example.com", "hunter2", AuthCookie: "cookie")),
            new LimiterHarness(LimiterHarness.Unpaced(), clock).Limiter,
            clock,
            new FakeMonotonicClock(),
            signIns: NewStore());

        var result = await gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsMembers, "grp_x", "GetGroupMembers"),
            (_, _) => Task.FromResult(new ApiResponse<string>(HttpStatusCode.OK, new Multimap<string, string>(), "x", "x")),
            ct: Ct);

        Assert.Equal(VRChatFailureKind.SignInWaiting, result.Kind);
        Assert.Equal(0, vrchat.VerifyAuthTokenCalls);
        Assert.Equal(0, vrchat.GetCurrentUserCalls);
    }

    private DatabaseSignInStore NewStore()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ModbotContext>(options => options.UseNpgsql(_database.ConnectionString));
        return new DatabaseSignInStore(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }
}
