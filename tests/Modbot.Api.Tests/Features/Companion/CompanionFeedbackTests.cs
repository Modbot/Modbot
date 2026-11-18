using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.AI;
using Modbot.Api.Tests.Features.Settings;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Companion;

/// <summary>
/// The Feedback button's three companion endpoints: whether the owner of a paired companion may send,
/// tidying what they said, and saving what they confirmed as them. Written, not run (Docker needed).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CompanionFeedbackTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Path = "/api/v1/companion/feedback";

    /// <summary>AI is not set up: the client answers nothing.</summary>
    private sealed class NoAi : IAiClients
    {
        public Task<AiChat?> GetChatAsync(CancellationToken ct) => Task.FromResult<AiChat?>(null);

        public Task<AiTestResult> TestAsync(AiConnection connection, CancellationToken ct) => throw new NotSupportedException();

        public Task<AiModelList> ListModelsAsync(AiConnection connection, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>With <paramref name="reply"/> null, AI is off; otherwise the model answers with it.</summary>
    private async Task<CompanionApiTestHost> StartHostAsync(Func<string, string>? reply)
    {
        await CompanionApiTestHost.ResetAsync(db, Ct);

        await using (var context = db.NewContext())
            await context.Feedback.ExecuteDeleteAsync(Ct);

        return await CompanionApiTestHost.StartAsync(db, configure: services =>
        {
            services.AddModbotAi();
            services.AddScoped<IAiClients>(_ => reply is null ? new NoAi() : new AutoModTests.FakeAi(reply));
        });
    }

    private static async Task<string> PairAsync(CompanionApiTestHost host, ModbotPermissions owns)
    {
        var owner = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion | owns, Ct);
        var (token, _) = await host.PairDeviceToAsync(owner.Id, Ct);
        return token;
    }

    private static async Task<HttpResponseMessage> PostAsync(CompanionApiTestHost host, string path, string? token, object body)
    {
        using var request = host.WithToken(HttpMethod.Post, path, token);
        request.Content = JsonContent.Create(body);
        return await host.Client.SendAsync(request, Ct);
    }

    private async Task<int> SavedAsync()
    {
        await using var context = db.NewContext();
        return await context.Feedback.CountAsync(Ct);
    }

    [Fact]
    public async Task WithoutAToken_AllThreeAreRefused()
    {
        await using var host = await StartHostAsync(null);

        using var read = host.WithToken(HttpMethod.Get, Path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(read, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(host, Path + "/tidy", null, new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(host, Path, null, new { kind = "bug", details = "hi" })).StatusCode);
    }

    [Theory]
    [InlineData(true, ModbotPermissions.SendFeedback, true, true)]
    [InlineData(false, ModbotPermissions.SendFeedback, true, false)]
    [InlineData(true, ModbotPermissions.Administrator, true, true)]
    [InlineData(true, ModbotPermissions.UseAiChat, false, false)]
    [InlineData(false, ModbotPermissions.ViewMembers, false, false)]
    public async Task Available_FollowsSendFeedback_AndNotAiChat_AndSaysWhetherAiIsSetUp(
        bool aiOn, ModbotPermissions owns, bool available, bool ai)
    {
        await using var host = await StartHostAsync(aiOn ? _ => "ok" : null);
        var token = await PairAsync(host, owns);

        using var request = host.WithToken(HttpMethod.Get, Path, token);
        var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(available, body.GetProperty("available").GetBoolean());
        Assert.Equal(ai, body.GetProperty("ai").GetBoolean());
    }

    [Fact]
    public async Task WithoutSendFeedback_TidyAndSendAreRefused_AndNothingIsSaved()
    {
        await using var host = await StartHostAsync(_ => "ok");
        var token = await PairAsync(host, ModbotPermissions.UseAiChat | ModbotPermissions.ReviewFeedback);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(host, Path + "/tidy", token, new { text = "hi" })).StatusCode);

        var send = await PostAsync(host, Path, token, new { kind = "bug", title = "t", details = "d" });
        Assert.Equal(HttpStatusCode.Forbidden, send.StatusCode);
        Assert.Equal("feedback_not_allowed", (await send.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());

        Assert.Equal(0, await SavedAsync());
    }

    [Fact]
    public async Task Send_WithAiOff_SavesTheRowAsTheOwner_AndRecordsTheKindOnly()
    {
        await using var host = await StartHostAsync(null);
        var owner = await host.CreateOwnerAsync(ModbotPermissions.PairCompanion | ModbotPermissions.SendFeedback, Ct);
        var (token, _) = await host.PairDeviceToAsync(owner.Id, Ct);

        var response = await PostAsync(host, Path, token,
            new { kind = " Bug ", title = "  Panel will not open ", details = "  It stays shut on the second try.  " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        await using var context = db.NewContext();
        var row = await context.Feedback.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(id, row.Id);
        Assert.Equal(owner.Id, row.SenderUserId);
        Assert.Equal(owner.Username, row.SenderName);
        Assert.Equal("bug", row.Kind);
        Assert.Equal("Panel will not open", row.Title);
        Assert.Equal("It stays shut on the second try.", row.Details);
        Assert.Equal(FeedbackStatuses.New, row.Status);
        Assert.Equal(CompanionApiTestHost.Now, row.CreatedAt);

        var fact = await context.Events.AsNoTracking()
            .SingleAsync(e => e.Type == FactType.FeedbackSent && e.SubjectId == id.ToString(), Ct);
        Assert.Contains("bug", fact.Data);
        Assert.DoesNotContain("Panel will not open", fact.Data);
        Assert.DoesNotContain("second try", fact.Data);
    }

    [Fact]
    public async Task Send_WithAiOn_SavesTheSame_AndAMissingTitleIsTheFirstLineOfTheDetails()
    {
        await using var host = await StartHostAsync(_ => "ok");
        var token = await PairAsync(host, ModbotPermissions.SendFeedback);

        var response = await PostAsync(host, Path, token, new { kind = "idea", details = "Add a dark mode.\nIt is too bright at night." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = db.NewContext();
        var row = await context.Feedback.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("idea", row.Kind);
        Assert.Equal("Add a dark mode.", row.Title);
    }

    [Theory]
    [InlineData("question", "t", "d")]
    [InlineData(null, "t", "d")]
    [InlineData("bug", "t", "")]
    [InlineData("bug", "t", null)]
    public async Task Send_WithABadKindOrNoDetails_IsRefused_AndNothingIsSaved(string? kind, string? title, string? details)
    {
        await using var host = await StartHostAsync(null);
        var token = await PairAsync(host, ModbotPermissions.SendFeedback);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(host, Path, token, new { kind, title, details })).StatusCode);
        Assert.Equal(0, await SavedAsync());
    }

    [Fact]
    public async Task Send_WithATitleOrDetailsThatIsTooLong_IsRefused()
    {
        await using var host = await StartHostAsync(null);
        var token = await PairAsync(host, ModbotPermissions.SendFeedback);

        var longTitle = new string('t', FeedbackItem.MaxTitleLength + 1);
        var longDetails = new string('d', FeedbackItem.MaxDetailsLength + 1);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(host, Path, token, new { kind = "bug", title = longTitle, details = "d" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(host, Path, token, new { kind = "bug", title = "t", details = longDetails })).StatusCode);
        Assert.Equal(0, await SavedAsync());
    }

    [Fact]
    public async Task Tidy_WithAiOn_ReturnsTheDraft_AndSavesNothing()
    {
        await using var host = await StartHostAsync(_ =>
            """Here you go: {"kind":"Bug","title":"Panel will not open","details":"The panel stays shut when a member joins."}""");
        var token = await PairAsync(host, ModbotPermissions.SendFeedback);

        var response = await PostAsync(host, Path + "/tidy", token, new { text = "uh the panel um never opens when Sam joins" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("bug", body.GetProperty("kind").GetString());
        Assert.Equal("Panel will not open", body.GetProperty("title").GetString());
        Assert.Equal("The panel stays shut when a member joins.", body.GetProperty("details").GetString());
        Assert.True(body.GetProperty("tidied").GetBoolean());

        Assert.Equal(0, await SavedAsync());
    }

    [Fact]
    public async Task Tidy_WithAiOff_GivesTheWordsBack_WithNoKind()
    {
        await using var host = await StartHostAsync(null);
        var token = await PairAsync(host, ModbotPermissions.SendFeedback);

        var response = await PostAsync(host, Path + "/tidy", token, new { text = "  the panel never opens  " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("kind").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("title").ValueKind);
        Assert.Equal("the panel never opens", body.GetProperty("details").GetString());
        Assert.False(body.GetProperty("tidied").GetBoolean());
    }

    [Fact]
    public async Task Tidy_WhenTheModelAnswersNothingUsable_GivesTheWordsBack()
    {
        await using var host = await StartHostAsync(_ => "I cannot help with that.");
        var token = await PairAsync(host, ModbotPermissions.SendFeedback);

        var response = await PostAsync(host, Path + "/tidy", token, new { text = "the panel never opens" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("the panel never opens", body.GetProperty("details").GetString());
        Assert.False(body.GetProperty("tidied").GetBoolean());
    }

    [Fact]
    public async Task Tidy_WithNoWords_IsRefused()
    {
        await using var host = await StartHostAsync(null);
        var token = await PairAsync(host, ModbotPermissions.SendFeedback);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(host, Path + "/tidy", token, new { text = "   " })).StatusCode);
    }
}
