using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Analytics.DailyTotals;
using Modbot.Api.Auth;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Health;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Scheduling;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// A host for the read surfaces: the audit log, the ban list, metrics and health.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>ApiTestHost</c> because these slices are not wired into <c>ApiSurface</c> by
/// this project — the host composes the surface (spec 2.5), and these tests must be able to prove
/// the endpoints map and answer before that wiring exists. Mapping them here is also the test
/// that catches the minimal-API body-binding trap: an unattributed concrete parameter on a GET
/// throws while the route is being built, so a host that starts at all has already proved the
/// signatures are right.
/// </para>
/// <para>
/// It writes through the real <c>FactWriter</c> against real partitions rather than inserting
/// rows, so a test's facts are shaped the way a producer's are — including the server-stamped
/// <c>observed_at</c>, which the coverage figures are computed from.
/// </para>
/// </remarks>
public sealed class ReadSurfaceTestHost : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;
    private readonly PostgresFixture _db;

    private ReadSurfaceTestHost(
        WebApplication app, HttpClient client, FakeClock clock, FakeVRChatGate gate, PostgresFixture db)
    {
        _app = app;
        _db = db;
        Client = client;
        Clock = clock;
        VRChat = gate;
    }

    public HttpClient Client { get; }

    public FakeClock Clock { get; }

    public FakeVRChatGate VRChat { get; }

    public SyncDiagnostics Diagnostics { get; private set; } = null!;

    public IServiceProvider Services => _app.Services;

    /// <param name="withSync">
    /// Whether the producers' options and diagnostics are registered. False exercises the other
    /// half of the health endpoint: a host with no producers answers and says so, rather than
    /// failing to resolve a service mid-request.
    /// </param>
    public static async Task<ReadSurfaceTestHost> StartAsync(
        PostgresFixture db,
        FakeVRChatGate? gate = null,
        bool withSync = true)
    {
        ArgumentNullException.ThrowIfNull(db);

        var clock = new FakeClock();
        gate ??= new FakeVRChatGate();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddDbContext<ModbotContext>(o => o.UseNpgsql(db.ConnectionString));
        builder.Services.AddSingleton<IModbotClock>(clock);
        builder.Services.AddSingleton<IMonotonicClock, StopwatchMonotonicClock>();
        builder.Services.AddSingleton<IVRChatGate>(gate);

        builder.Services.AddSingleton<ISecretProtector>(services =>
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            return AesGcmSecretProtector.CreateAsync(context).GetAwaiter().GetResult();
        });

        // The analytics pieces these tests write through, without the hosted services -- a test
        // that started the daily totals timer would be racing its own assertions.
        builder.Services.AddScoped<IFactWriter, FactWriter>();
        builder.Services.AddScoped<EventPartitionMaintainer>();
        builder.Services.AddScoped<DailyTotalsJob>();

        var diagnostics = new SyncDiagnostics(clock);

        if (withSync)
        {
            builder.Services.AddSingleton(diagnostics);
            builder.Services.AddSingleton(new AuditLogSyncOptions().Clamped());
            builder.Services.AddSingleton(new GroupInfoSyncOptions().Clamped());
        }

        builder.Services.AddModbotAuth();
        builder.Services.AddModbotApi();

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        // Maps the read surface too, now that ApiSurface wires it. Mapping the four extensions
        // again here would register every route twice, and ASP.NET reports that as an ambiguous
        // match at request time rather than at startup -- so the symptom is every read test
        // failing, not a clear error where the duplication is.
        app.MapModbotApi();

        await app.StartAsync();

        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost/");

        return new ReadSurfaceTestHost(app, client, clock, gate, db) { Diagnostics = diagnostics };
    }

    /// <summary>
    /// Clears the fact log, the daily totals and the settings row.
    /// </summary>
    /// <remarks>
    /// The whole assembly shares one container and therefore one database, and every coverage
    /// figure on these screens is a minimum or a maximum over the entire table. A test that
    /// inherited another's facts would assert on numbers it did not write.
    /// </remarks>
    public async Task ResetAsync(CancellationToken ct)
    {
        await using var context = _db.NewContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM modbot_event", ct);
        await context.DailyTotals.ExecuteDeleteAsync(ct);
        await context.DailyTotalsState.ExecuteDeleteAsync(ct);
        await context.Settings.ExecuteDeleteAsync(ct);
    }

    public async Task<ModbotUser> CreateUserAsync(
        string username, string password, ModbotPermissions permissions, CancellationToken ct)
    {
        using var scope = Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<Core.Users.UserAccountService>();
        return await accounts.CreateAsync(username, password, permissions, ct);
    }

    /// <summary>Creates an account with exactly these permissions and returns its session cookie.</summary>
    public async Task<string> SignedInAsync(ModbotPermissions permissions, CancellationToken ct)
    {
        var name = $"u_{Guid.NewGuid():N}";
        await CreateUserAsync(name, "hunter2", permissions, ct);

        var response = await Client.PostAsync(
            "/api/auth/login",
            JsonContent.Create(new { username = name, password = "hunter2" }),
            ct);

        response.EnsureSuccessStatusCode();

        var cookie = response.Headers.GetValues("Set-Cookie")
            .First(v => v.StartsWith(ModbotAuth.CookieName, StringComparison.Ordinal));

        return cookie.Split(';')[0];
    }

    public async Task<HttpResponseMessage> GetAsync(string path, string cookie, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await Client.SendAsync(request, ct);
    }

    public async Task<T> GetJsonAsync<T>(string path, string cookie, CancellationToken ct)
    {
        var response = await GetAsync(path, cookie, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);

        return JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException($"{path} answered null.");
    }

    /// <summary>Writes one fact, creating the partition it belongs in first.</summary>
    public async Task WriteFactAsync(FactRecord fact, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fact);

        using var scope = Services.CreateScope();
        var partitions = scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>();
        var writer = scope.ServiceProvider.GetRequiredService<IFactWriter>();

        await partitions.EnsureForAsync(fact.OccurredAt, ct);
        await writer.WriteAsync(fact, ct);
    }

    public async Task RebuildDailyTotalsAsync(CancellationToken ct)
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DailyTotalsJob>().RebuildAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
