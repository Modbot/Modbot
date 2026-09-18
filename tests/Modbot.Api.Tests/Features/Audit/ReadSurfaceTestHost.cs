using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Retention;
using Modbot.Analytics.Reviews;
using Modbot.Api.Auth;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Evidence;
using Modbot.Api.Features.Health;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Evidence;
using Modbot.Evidence.Upload;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Scheduling;
using Modbot.VRChat.Sync;
using Modbot.VRChat.Users;

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
    /// <param name="configure">
    /// Last word on the container, after every registration this host makes: a test that needs a
    /// service the host deliberately leaves out — the machine usage sampler, say, which is filled
    /// by a background timer here — registers its own here, and the later registration wins.
    /// </param>
    public static async Task<ReadSurfaceTestHost> StartAsync(
        PostgresFixture db,
        FakeVRChatGate? gate = null,
        bool withSync = true,
        Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var clock = new FakeClock();
        gate ??= new FakeVRChatGate();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.QuietForTests();

        builder.Services.AddDbContext<ModbotContext>(o => o.UseNpgsql(db.ConnectionString));
        builder.Services.AddSingleton<IModbotClock>(clock);
        builder.Services.AddSingleton<IMonotonicClock, StopwatchMonotonicClock>();
        builder.Services.AddSingleton<IVRChatGate>(gate);

        // The three kick/ban/unban calls, over the scripted gate. Registered here rather than
        // through AddModbotVRChat, which would bring the hosted syncs with it.
        builder.Services.AddSingleton<Modbot.VRChat.Moderation.GroupModeration>();

        // The join queue and the two answers to one of it, over the same scripted gate.
        builder.Services.AddSingleton<Modbot.VRChat.Moderation.GroupJoinRequests>();

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

        // The review job and the facts it and the close endpoint write, again without the hosted
        // service: a test runs detection when it wants to assert on the result.
        builder.Services.AddScoped<ReviewFacts>();
        builder.Services.AddScoped<ReviewJob>();

        // Settings → Purge a person, which counts through one of these and erases through the
        // other. Registered here for the same reason as the rest: the hosted retention service
        // would be pruning partitions underneath a test that is asserting on them.
        builder.Services.AddScoped<IUserPurger, UserPurger>();
        builder.Services.AddScoped<PurgePreviewer>();

        var diagnostics = new SyncDiagnostics(clock);
        var queue = new UserRefreshQueue();

        if (withSync)
        {
            builder.Services.AddSingleton(diagnostics);
            builder.Services.AddSingleton(new AuditLogSyncOptions().Clamped());
            builder.Services.AddSingleton(new GroupInfoSyncOptions().Clamped());

            // The profile sync's pieces the API writes through -- the queue a refresh request
            // lands in and the one writer of vrchat_user rows -- without the hosted service that
            // would drain the queue. A test asserts on what was queued, not on what a producer
            // did with it a moment later.
            var profileOptions = new UserProfileSyncOptions().Clamped();
            builder.Services.AddSingleton(profileOptions);
            builder.Services.AddSingleton(queue);
            builder.Services.AddScoped(sp => new VRChatUserProfiles(
                sp.GetRequiredService<ModbotContext>(),
                sp.GetRequiredService<IFactWriter>(),
                sp.GetRequiredService<EventPartitionMaintainer>(),
                sp.GetRequiredService<IModbotClock>(),
                queue,
                profileOptions));
        }

        // Evidence, the way the host wires it, so a case file test can attach a real upload to a
        // case file through the real endpoints. Points at a scratch directory once a test asks.
        var evidenceRoot = Path.Combine(Path.GetTempPath(), "modbot-case-file-tests", Guid.NewGuid().ToString("n"));
        builder.Services.AddSingleton(HostPlatform.SelfHosted);
        builder.Services.AddModbotEvidence();
        builder.Services.AddModbotEvidenceSettings();
        builder.Services.AddScoped<IEvidenceMetadata, DatabaseEvidenceMetadata>();

        builder.Services.AddModbotAuth();
        builder.Services.AddModbotApi();

        configure?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        // Maps the read surface too, now that ApiSurface wires it. Mapping the four extensions
        // again here would register every route twice, and ASP.NET reports that as an ambiguous
        // match at request time rather than at startup -- so the symptom is every read test
        // failing, not a clear error where the duplication is.
        app.MapModbotApi();

        await app.StartAsync();
        await app.Services.LoadEvidenceSettingsAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost/");

        return new ReadSurfaceTestHost(app, client, clock, gate, db)
        {
            Diagnostics = diagnostics,
            Queue = queue,
            EvidenceRoot = evidenceRoot,
        };
    }

    /// <summary>The refresh queue the API feeds. Empty and unregistered when <c>withSync</c> was false.</summary>
    public UserRefreshQueue Queue { get; private set; } = null!;

    /// <summary>A scratch directory the filesystem evidence backend can be pointed at.</summary>
    public string EvidenceRoot { get; private set; } = string.Empty;

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
        await context.Database.ExecuteSqlRawAsync("DELETE FROM discord_message", ct);
        await context.VRChatUsers.ExecuteDeleteAsync(ct);
        await context.GroupMemberCounts.ExecuteDeleteAsync(ct);
        await context.VRChatInstances.ExecuteDeleteAsync(ct);
        await context.VRChatWorlds.ExecuteDeleteAsync(ct);
        await context.GroupMembers.ExecuteDeleteAsync(ct);
        await context.GroupBans.ExecuteDeleteAsync(ct);
        await context.DailyTotals.ExecuteDeleteAsync(ct);
        await context.DailyTotalsState.ExecuteDeleteAsync(ct);
        await context.Reviews.ExecuteDeleteAsync(ct);
        await context.RepeatOffenders.ExecuteDeleteAsync(ct);
        await context.ModeratorBaselines.ExecuteDeleteAsync(ct);
        await context.ReviewRunState.ExecuteDeleteAsync(ct);
        await context.CaseFiles.ExecuteDeleteAsync(ct);
        await context.ModerationActions.ExecuteDeleteAsync(ct);
        await context.BanReasons.ExecuteDeleteAsync(ct);
        await context.EvidenceBlobs.ExecuteDeleteAsync(ct);
        await context.DiscordChannels.ExecuteDeleteAsync(ct);
        await context.DiscordReadBacks.ExecuteDeleteAsync(ct);
        await context.DiscordRoles.ExecuteDeleteAsync(ct);
        await context.DiscordServers.ExecuteDeleteAsync(ct);
        await context.DiscordMembers.ExecuteDeleteAsync(ct);
        await context.DiscordEventRoutes.ExecuteDeleteAsync(ct);
        await context.DiscordAccountLinks.ExecuteDeleteAsync(ct);
        await context.DiscordEventChannels.ExecuteDeleteAsync(ct);
        await context.Settings.ExecuteDeleteAsync(ct);
    }

    public async Task<ModbotUser> CreateUserAsync(
        string username, string password, ModbotPermissions permissions, CancellationToken ct, bool linked = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await TestAccounts.CreateAsync(db, username, password, permissions, linked, ct);
    }

    /// <summary>
    /// Creates an account with exactly these permissions and returns its session cookie. Linked
    /// to a VRChat account unless a test about the link itself says otherwise.
    /// </summary>
    public async Task<string> SignedInAsync(ModbotPermissions permissions, CancellationToken ct, bool linked = true)
    {
        var name = $"u_{Guid.NewGuid():N}";
        await CreateUserAsync(name, "hunter2", permissions, ct, linked);

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

    /// <summary>The daily totals and then the detection run, the way the host sequences them.</summary>
    public async Task<ReviewRunResult> RunReviewsAsync(CancellationToken ct)
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DailyTotalsJob>().RunIncrementalAsync(ct);
        return await scope.ServiceProvider.GetRequiredService<ReviewJob>().RunIncrementalAsync(ct);
    }

    public async Task<HttpResponseMessage> PostJsonAsync(string path, object? body, string cookie, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", cookie);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request, ct);
    }

    public async Task<HttpResponseMessage> PutJsonAsync(string path, object? body, string cookie, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path);
        request.Headers.Add("Cookie", cookie);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request, ct);
    }

    /// <summary>The evidence transfer phase: the file as a raw body, no form encoding.</summary>
    public async Task<HttpResponseMessage> PutBytesAsync(string path, byte[] bytes, string cookie, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path);
        request.Headers.Add("Cookie", cookie);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await Client.SendAsync(request, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        Client.Dispose();
        await _app.DisposeAsync();

        try
        {
            if (Directory.Exists(EvidenceRoot))
                Directory.Delete(EvidenceRoot, recursive: true);
        }
        catch (IOException)
        {
            // A scratch directory that could not be removed is litter in the temp folder, not a
            // failed test.
        }
    }
}
