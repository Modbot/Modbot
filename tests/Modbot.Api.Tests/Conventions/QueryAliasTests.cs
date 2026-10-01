using System.Net;
using Microsoft.AspNetCore.Http;
using Modbot.Api.Conventions;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Conventions;

/// <summary>
/// One name per kind of query parameter (API conventions design §3): the common name reaches the
/// endpoint, the endpoint's own name still works, and the own name wins when both are sent.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class QueryAliasTests
{
    private readonly PostgresFixture _db;

    public QueryAliasTests(PostgresFixture db) => _db = db;

    private static HttpRequest RequestFor(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context.Request;
    }

    [Fact]
    public void TheCommonName_IsCopiedToTheEndpointsOwn()
    {
        var request = RequestFor("/api/search", "?search=troublemaker");

        QueryAliases.Apply(request);

        Assert.Equal("troublemaker", request.Query["q"].ToString());
        Assert.Equal("troublemaker", request.Query["search"].ToString());
    }

    [Fact]
    public void TheEndpointsOwnName_WinsWhenBothAreSent()
    {
        var request = RequestFor("/api/reviews", "?status=closed&state=open");

        QueryAliases.Apply(request);

        Assert.Equal("open", request.Query["state"].ToString());
    }

    [Fact]
    public void EveryValue_IsCopied()
    {
        var request = RequestFor("/api/cases/lookup", "?vrchatUserId=usr_a&vrchatUserId=usr_b");

        QueryAliases.Apply(request);

        Assert.Equal(["usr_a", "usr_b"], request.Query["userId"].ToArray());
    }

    [Fact]
    public void APathWithNoAliases_IsLeftAlone()
    {
        var request = RequestFor("/api/notes", "?vrchatUserId=usr_a");

        QueryAliases.Apply(request);

        Assert.False(request.Query.ContainsKey("userId"));
    }

    [Fact]
    public void EveryCommonName_IsOneOfTheFour()
        => Assert.All(
            QueryAliases.Table.Values.SelectMany(a => a),
            a => Assert.Contains(a.Common, new[] { QueryAliases.Search, QueryAliases.Status, QueryAliases.VRChatUserId, QueryAliases.DiscordUserId }));

    [Fact]
    public async Task TheCommonName_ReachesTheHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);

        // A blank id reaches the handler, which says so in its own words; a missing one would not.
        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, "/api/vrchat-users/profile?vrchatUserId=%20", cookie), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("id is required.", (await ApiTestHost.BodyOf(response, ct)).GetProperty("detail").GetString());
    }
}
