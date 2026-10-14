using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Supporters;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Supporters;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Supporters;

/// <summary>
/// Patreon's webhook address: dark until Patreon is on and a secret is saved, refuses a message whose
/// signature is missing or wrong without reading it, and takes in a signed one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PatreonWebhookTests(PostgresFixture db)
{
    private const string Secret = "patreon-webhook-secret";

    private const string Membership = """
        {"data":{"id":"member-7","type":"member","attributes":{"patron_status":"active_patron","full_name":"Sam Patron",
          "last_charge_date":"2026-01-01T00:00:00.000+00:00","last_charge_status":"Paid"},
          "relationships":{"currently_entitled_tiers":{"data":[{"id":"gold"}]}}},
         "included":[{"id":"gold","type":"tier","attributes":{"title":"Gold","amount_cents":1500}}]}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync(bool on, bool secretSaved)
    {
        await using (var context = db.NewContext())
        {
            await context.Supporters.ExecuteDeleteAsync(Ct);
            await context.SupporterTiers.ExecuteDeleteAsync(Ct);
        }

        var host = await ApiTestHost.StartAsync(db);
        host.Clock.UtcNow = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

        using var scope = host.Services.CreateScope();
        var context2 = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var settings = await context2.GetSettingsAsync(Ct);
        settings.PatreonOn = on;
        settings.PatreonWebhookSecretEncrypted = secretSaved ? protector.Protect(Secret) : null;
        await context2.SaveChangesAsync(Ct);
        return host;
    }

    private static string Sign(string body, string secret)
        => Convert.ToHexStringLower(HMACMD5.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    private static Task<HttpResponseMessage> PostAsync(ApiTestHost host, string body, string? signature, string? trigger = "members:pledge:create")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, PatreonWebhookEndpoint.Path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (signature is not null)
            request.Headers.Add(PatreonRules.SignatureHeader, signature);

        if (trigger is not null)
            request.Headers.Add(PatreonRules.EventHeader, trigger);

        return host.Client.SendAsync(request, Ct);
    }

    private async Task<int> SupporterCountAsync()
    {
        await using var read = db.NewContext();
        return await read.Supporters.CountAsync(Ct);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task UntilPatreonIsOnAndASecretIsSaved_TheAddressAnswers404_EvenForASignedMessage(bool on, bool secretSaved)
    {
        await using var host = await StartAsync(on, secretSaved);

        var response = await PostAsync(host, Membership, Sign(Membership, Secret));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await SupporterCountAsync());
    }

    [Fact]
    public async Task AMessageWithNoSignature_IsRefused_AndNothingIsRead()
    {
        await using var host = await StartAsync(true, true);

        var response = await PostAsync(host, Membership, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await SupporterCountAsync());
    }

    [Fact]
    public async Task AMessageWithTheWrongSignature_IsRefused_AndNothingIsRead()
    {
        await using var host = await StartAsync(true, true);

        var response = await PostAsync(host, Membership, Sign(Membership, "another-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await SupporterCountAsync());
    }

    [Fact]
    public async Task AWrongSignatureOnAMessageThatIsNotJson_IsStillRefused_NotCalledBad()
    {
        await using var host = await StartAsync(true, true);

        var response = await PostAsync(host, "not json", "00");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ASignedMessage_IsTakenIn_AsAPatreonSupporterWaitingToBeMatched()
    {
        await using var host = await StartAsync(true, true);

        var response = await PostAsync(host, Membership, Sign(Membership, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = db.NewContext();
        var supporter = await read.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(SupporterProviders.Patreon, supporter.Provider);
        Assert.Equal("Sam Patron", supporter.Name);
        Assert.Null(supporter.DiscordUserId);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), supporter.PaidThrough);
    }

    [Fact]
    public async Task ASignedMessageThatIsNotAboutAPatronWhoCounts_IsAnswered200AndIgnored()
    {
        await using var host = await StartAsync(true, true);
        const string Follower = """{"data":{"id":"member-8","type":"member","attributes":{"patron_status":null,"full_name":"Free"}}}""";

        var response = await PostAsync(host, Follower, Sign(Follower, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await SupporterCountAsync());
    }

    [Fact]
    public async Task ASignedMessageThatIsNotJson_IsABadRequest()
    {
        await using var host = await StartAsync(true, true);

        var response = await PostAsync(host, "not json", Sign("not json", Secret));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ASignedPledgeDelete_EndsTheMonthWhereThePaidChargeLeavesIt()
    {
        await using var host = await StartAsync(true, true);
        await PostAsync(host, Membership, Sign(Membership, Secret));
        const string Deleted = """
            {"data":{"id":"member-7","type":"member","attributes":{"patron_status":"former_patron","full_name":"Sam Patron",
              "last_charge_date":"2026-01-01T00:00:00.000+00:00","last_charge_status":"Paid"},
              "relationships":{"currently_entitled_tiers":{"data":[]}}}}
            """;

        var response = await PostAsync(host, Deleted, Sign(Deleted, Secret), "members:pledge:delete");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = db.NewContext();
        var supporter = await read.Supporters.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), supporter.PaidThrough);
        Assert.Equal(1, await read.SupporterPayments.CountAsync(Ct));
    }
}
