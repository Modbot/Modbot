using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Modbot.Api.Conventions;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Conventions;

/// <summary>
/// The one error shape (API conventions design §2): every error under <c>/api</c> is
/// <c>application/problem+json</c> with a code, whether the endpoint answered <c>{ error }</c>,
/// answered nothing, or never ran because sign-in refused it -- and <c>error</c> is still there
/// for the web app and the scripts that read it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ProblemTests
{
    private const string Audit = "/api/audit";

    /// <summary>
    /// An endpoint that names its permission with <c>RequiresFlag</c>. The audit log itself is
    /// authorised by hand (any of several permissions), so its refusal names none.
    /// </summary>
    private const string BanList = "/api/audit/bans";

    private readonly PostgresFixture _db;

    public ProblemTests(PostgresFixture db) => _db = db;

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage response, CancellationToken ct)
    {
        Assert.Equal(Problems.ContentType, response.Content.Headers.ContentType?.MediaType);
        return await ApiTestHost.BodyOf(response, ct);
    }

    [Fact]
    public async Task NotSignedIn_IsAProblemWithItsCode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync(Audit, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await ProblemOf(response, ct);
        Assert.Equal("not-signed-in", body.GetProperty("code").GetString());
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.Equal(Problems.TypeBase + "not-signed-in", body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task AMissingPermission_IsNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, ct);

        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, BanList, cookie), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ProblemOf(response, ct);
        Assert.Equal("needs-permission", body.GetProperty("code").GetString());
        Assert.Contains(
            "ViewAuditLog",
            body.GetProperty("neededPermissions").EnumerateArray().Select(p => p.GetString()));

        // The sentence the web app shows, in both places it reads one from.
        Assert.Equal(body.GetProperty("detail").GetString(), body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnUnlinkedAccount_IsToldThatRatherThanAPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct, linked: false);

        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Audit, cookie), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("vrchat-not-linked", (await ProblemOf(response, ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnEndpointsOwnSentence_KeepsItsErrorField()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, "/api/vrchat-users/profile?id=%20", cookie), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ProblemOf(response, ct);
        Assert.Equal("invalid-request", body.GetProperty("code").GetString());
        Assert.Equal("id is required.", body.GetProperty("error").GetString());
        Assert.Equal("id is required.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AParameterThatCouldNotBeRead_IsAProblemToo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);

        // No id at all: the framework refuses before the handler runs, with no body of its own.
        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, "/api/vrchat-users/profile", cookie), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-request", (await ProblemOf(response, ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task OutsideTheApi_NothingIsReshaped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync(ApiTestHost.AuditProbe, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(Problems.ContentType, response.Content.Headers.ContentType?.MediaType);
    }

    // ── The shape itself ───────────────────────────────────────────────────────────────────

    [Fact]
    public void AnEndpointsOwnFields_AreKept()
    {
        var body = Problems.Body(
            StatusCodes.Status409Conflict,
            null,
            null,
            new JsonObject { ["error"] = "A case file already exists.", ["caseId"] = "c1" });

        Assert.Equal("conflict", (string?)body["code"]);
        Assert.Equal("A case file already exists.", (string?)body["detail"]);
        Assert.Equal("A case file already exists.", (string?)body["error"]);
        Assert.Equal("c1", (string?)body["caseId"]);
        Assert.Equal(409, (int?)body["status"]);
    }

    [Fact]
    public void AnEndpointsOwnDetail_IsNotWrittenOver()
    {
        var body = Problems.Body(
            StatusCodes.Status422UnprocessableEntity,
            null,
            null,
            new JsonObject { ["headline"] = "Blocked", ["detail"] = "Cloudflare turned the request away." });

        Assert.Equal("Cloudflare turned the request away.", (string?)body["detail"]);
        Assert.Equal("Blocked", (string?)body["headline"]);
        Assert.Equal("not-possible", (string?)body["code"]);
    }

    [Fact]
    public void ACodeTheEndpointNamed_WinsOverTheStatussOwn()
    {
        var body = Problems.Body(StatusCodes.Status502BadGateway, "VRChat said no.", Problems.VRChatRefused);

        Assert.Equal("vrchat-refused", (string?)body["code"]);
        Assert.Equal("VRChat refused", (string?)body["title"]);
    }

    [Theory]
    [InlineData("/api/members", true)]
    [InlineData("/api/auth/me", true)]
    [InlineData("/api/v1/companion/time", false)]
    [InlineData("/api/proxy/vrchat/users/usr_x", true)]
    [InlineData("/mcp", false)]
    [InlineData("/probe/audit", false)]
    public void OnlyTheApi_TakesTheShape(string path, bool covered)
        => Assert.Equal(covered, Problems.Covers(new PathString(path)));

    [Fact]
    public void AnEmptyAnswerFromTheProxy_IsVRChats()
    {
        Assert.True(Problems.IsPassedOn(new PathString("/api/proxy/vrchat/users/usr_x")));
        Assert.False(Problems.IsPassedOn(new PathString("/api/members")));
    }

    [Fact]
    public void EveryCode_IsPlainAndUnique()
    {
        Assert.Equal(Problems.All.Count, Problems.All.Select(c => c.Code).Distinct().Count());
        Assert.All(Problems.All, c => Assert.Matches("^[a-z]+(-[a-z]+)*$", c.Code));
        Assert.All(Problems.All, c => Assert.InRange(c.Status, 400, 599));
    }
}
