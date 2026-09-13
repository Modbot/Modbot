using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Api.Features.Auth.Login;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Evidence;
using Modbot.Evidence.Upload;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// A host that maps the evidence slice and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>MapModbotApi()</c>. <c>MapEvidence</c> is not wired into
/// <c>ApiSurface</c> by this change — the host owns that line — and mapping both here would
/// register every evidence route twice the moment it is. ASP.NET reports a duplicate as an
/// ambiguous match at <em>request</em> time rather than at startup, so the symptom would be every
/// test in the assembly failing with nothing pointing at the cause. The client slice already
/// sets this precedent for the same reason.
/// </para>
/// <para>
/// The login endpoint is mapped on its own because the tests sign in for real: permissions are
/// what this feature is about, and a faked principal would not prove the flags are attached to the
/// routes.
/// </para>
/// </remarks>
public sealed class EvidenceApiTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EvidenceApiTestHost(WebApplication app, HttpClient client, FakeClock clock, string root)
    {
        _app = app;
        Client = client;
        Clock = clock;
        Root = root;
    }

    public HttpClient Client { get; }

    public FakeClock Clock { get; }

    /// <summary>A scratch directory the filesystem backend can be pointed at.</summary>
    public string Root { get; }

    public IServiceProvider Services => _app.Services;

    /// <param name="platform">
    /// What Modbot should believe it is running on. The default is a platform whose filesystem is
    /// assumed durable, because that is what a developer's machine is; pass an ephemeral one to
    /// exercise the durability warning, which is the only thing that platform detection affects.
    /// </param>
    public static async Task<EvidenceApiTestHost> StartAsync(
        PostgresFixture db, HostPlatform? platform = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var clock = new FakeClock(new DateTimeOffset(2026, 9, 13, 9, 0, 0, TimeSpan.Zero));
        var root = Path.Combine(Path.GetTempPath(), "modbot-evidence-tests", Guid.NewGuid().ToString("n"));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddDbContext<ModbotContext>(o => o.UseNpgsql(db.ConnectionString));
        builder.Services.AddSingleton<IModbotClock>(clock);

        // Registered before AddModbotEvidenceSettings, which only fills this in if nothing else
        // has. Platform detection is the single input to the durability warning and nothing else.
        builder.Services.AddSingleton(platform ?? HostPlatform.SelfHosted);

        builder.Services.AddSingleton<ISecretProtector>(services =>
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            return AesGcmSecretProtector.CreateAsync(context).GetAwaiter().GetResult();
        });

        builder.Services.AddModbotAuth();
        builder.Services.AddModbotEvidence();
        builder.Services.AddModbotEvidenceSettings();
        builder.Services.AddScoped<IEvidenceMetadata, DatabaseEvidenceMetadata>();

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapLogin();
        app.MapEvidence();

        await app.StartAsync();
        await app.Services.LoadEvidenceSettingsAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost/");

        return new EvidenceApiTestHost(app, client, clock, root);
    }

    /// <summary>
    /// Puts the deployment back to "nobody has ever configured evidence".
    /// </summary>
    /// <remarks>
    /// The settings row is a singleton and the blob projection is shared by every test in the
    /// assembly, so a test about an unconfigured store has to establish that rather than inherit
    /// whatever the last one left behind.
    /// </remarks>
    public static async Task ResetAsync(PostgresFixture db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        await using var context = db.NewContext();
        await context.EvidenceBlobs.ExecuteDeleteAsync(ct);
        await context.Users.ExecuteDeleteAsync(ct);
        await context.Settings.ExecuteDeleteAsync(ct);
    }

    /// <summary>Creates an account with exactly these permissions and signs in as it.</summary>
    public async Task<string> SignedInAsync(ModbotPermissions permissions, CancellationToken ct)
    {
        var username = $"u_{Guid.NewGuid():N}";

        using (var scope = Services.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<Core.Users.UserAccountService>();
            await accounts.CreateAsync(username, "hunter2", permissions, ct);
        }

        var response = await Client.PostAsync(
            "/api/auth/login", Json(new { username, password = "hunter2" }), ct);

        response.EnsureSuccessStatusCode();

        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(ModbotAuth.CookieName, StringComparison.Ordinal));

        return setCookie.Split(';')[0];
    }

    public Task<HttpResponseMessage> GetAsync(string path, string cookie, CancellationToken ct)
        => SendAsync(HttpMethod.Get, path, cookie, null, ct);

    public Task<HttpResponseMessage> PostAsync<T>(string path, string cookie, T body, CancellationToken ct)
        => SendAsync(HttpMethod.Post, path, cookie, Json(body), ct);

    public Task<HttpResponseMessage> PutAsync<T>(string path, string cookie, T body, CancellationToken ct)
        => SendAsync(HttpMethod.Put, path, cookie, Json(body), ct);

    public Task<HttpResponseMessage> PutBytesAsync(
        string path, string cookie, byte[] bytes, CancellationToken ct)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return SendAsync(HttpMethod.Put, path, cookie, content, ct);
    }

    public async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);

        return System.Text.Json.JsonSerializer.Deserialize<T>(
            json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"Empty body where {typeof(T).Name} was expected.");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        Client.Dispose();
        await _app.DisposeAsync();

        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A scratch directory that could not be removed is litter in the temp folder, not a
            // failed test.
        }
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string cookie, HttpContent? content, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("Cookie", cookie);
        return Client.SendAsync(request, ct);
    }

    private static StringContent Json<T>(T value)
    {
        var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}
