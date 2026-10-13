using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using VRChatUserModel = VRChat.API.Model.UserResponse;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Whether Modbot's own VRChat account has VRChat+, shown in Settings with a button that reads it
/// again: one read of the account's own user, interactive, on the users.read lane, never retried.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VRChatPlusSettingsTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Check = "/api/settings/vrchat-pictures/check-plus";

    private const string Operation = "GetUser (VRChat+ check)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string? _sessionUserId;
    private string? _accountUserId;

    public async ValueTask InitializeAsync()
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);

        _sessionUserId = settings.VRChatSessionUserId;
        _accountUserId = settings.VRChatAccountUserId;

        settings.VRChatHasPlus = null;
        settings.VRChatPictureUploads = false;
        settings.VRChatSessionUserId = "usr_modbot";
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>The settings row is shared with every other class: put back what this one changed.</summary>
    public async ValueTask DisposeAsync()
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);

        settings.VRChatHasPlus = null;
        settings.VRChatPictureUploads = false;
        settings.VRChatSessionUserId = _sessionUserId;
        settings.VRChatAccountUserId = _accountUserId;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<bool?> StoredAsync()
    {
        await using var context = db.NewContext();
        return (await context.GetSettingsAsync(Ct)).VRChatHasPlus;
    }

    [Fact]
    public async Task CheckingNeedsManageSettings()
    {
        var gate = new FakeVRChatGate().Returns(Operation, new VRChatUserModel { Id = "usr_modbot", Tags = ["system_supporter"] });
        await using var host = await ApiTestHost.StartAsync(db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Check, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(gate.Calls);
    }

    [Theory]
    [InlineData("system_supporter", true)]
    [InlineData("system_trust_known", false)]
    public async Task CheckingAgainReadsTheOwnUserOnce_AndKeepsWhatItsTagsSay(string tag, bool hasPlus)
    {
        var gate = new FakeVRChatGate().Returns(Operation, new VRChatUserModel { Id = "usr_modbot", Tags = [tag, "language_eng"] });
        await using var host = await ApiTestHost.StartAsync(db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Check, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(hasPlus, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("hasPlus").GetBoolean());
        Assert.Equal(hasPlus, await StoredAsync());

        var call = Assert.Single(gate.Calls);
        Assert.Equal(VRChatEndpointClass.UsersRead, call.Endpoint.Class);
        Assert.Equal(VRChatCallPriority.Interactive, call.Priority);

        var read = await host.SendJsonAsync(HttpMethod.Get, "/api/settings/vrchat-pictures", null, cookie, Ct);
        Assert.Equal(hasPlus, (await ApiTestHost.BodyOf(read, Ct)).GetProperty("hasPlus").GetBoolean());
    }

    [Fact]
    public async Task WhenItHasNotBeenReadItIsNull()
    {
        await using var host = await ApiTestHost.StartAsync(db, new FakeVRChatGate());
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var read = await host.SendJsonAsync(HttpMethod.Get, "/api/settings/vrchat-pictures", null, cookie, Ct);

        Assert.Equal(JsonValueKind.Null, (await ApiTestHost.BodyOf(read, Ct)).GetProperty("hasPlus").ValueKind);
    }

    [Fact]
    public async Task A429IsAnsweredAsOne_NothingIsKept_AndItIsNotSentAgain()
    {
        var gate = new FakeVRChatGate().Returns(
            Operation,
            VRChatResult<VRChatUserModel>.Failure(429, "VRChat is rate limiting Modbot.", kind: VRChatFailureKind.RateLimited));
        await using var host = await ApiTestHost.StartAsync(db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Check, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull((await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
        Assert.Null(await StoredAsync());
        Assert.Single(gate.Calls);
    }

    [Fact]
    public async Task AnAnswerWithNoTagsKeepsNothing()
    {
        var gate = new FakeVRChatGate().Returns(Operation, new VRChatUserModel { Id = "usr_modbot" });
        await using var host = await ApiTestHost.StartAsync(db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Check, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Null(await StoredAsync());
    }

    private async Task SetAsync(bool? hasPlus, bool pictureUploads)
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.VRChatHasPlus = hasPlus;
        settings.VRChatPictureUploads = pictureUploads;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<bool> UploadsAsync()
    {
        await using var context = db.NewContext();
        return (await context.GetSettingsAsync(Ct)).VRChatPictureUploads;
    }

    private async Task<List<string>> AutomaticSwitchOffsAsync()
    {
        await using var context = db.NewContext();
        // The data column is jsonb, which has no LIKE: read the changes of settings and look inside them here.
        var rows = await context.Events.AsNoTracking()
            .Where(e => e.Type == FactType.SettingsChanged)
            .Select(e => e.Data)
            .ToListAsync(Ct);

        return [.. rows.Where(d => d.Contains("\"vrchatPictureUploads\"") && d.Contains("\"automatic\""))];
    }

    [Fact]
    public async Task CheckingAgainWithoutVRChatPlusTurnsThePictureSwitchOff_AndRecordsItAsAutomatic()
    {
        await SetAsync(hasPlus: true, pictureUploads: true);
        var before = (await AutomaticSwitchOffsAsync()).Count;
        var gate = new FakeVRChatGate().Returns(Operation, new VRChatUserModel { Id = "usr_modbot", Tags = ["system_trust_known"] });
        await using var host = await ApiTestHost.StartAsync(db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Check, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.False(body.GetProperty("on").GetBoolean());
        Assert.False(await UploadsAsync());

        var after = await AutomaticSwitchOffsAsync();
        Assert.Equal(before + 1, after.Count);
        Assert.Contains("\"before\":true", after[^1].Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Contains("\"after\":false", after[^1].Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckingAgainWithVRChatPlusLeavesThePictureSwitchOn_AndRecordsNothing()
    {
        await SetAsync(hasPlus: null, pictureUploads: true);
        var before = (await AutomaticSwitchOffsAsync()).Count;
        var gate = new FakeVRChatGate().Returns(Operation, new VRChatUserModel { Id = "usr_modbot", Tags = ["system_supporter"] });
        await using var host = await ApiTestHost.StartAsync(db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Check, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await UploadsAsync());
        Assert.Equal(before, (await AutomaticSwitchOffsAsync()).Count);
    }

    [Fact]
    public async Task ThePictureSwitchCannotBeTurnedOnWhileTheAccountHasNoVRChatPlus()
    {
        await SetAsync(hasPlus: false, pictureUploads: false);
        await using var host = await ApiTestHost.StartAsync(db, new FakeVRChatGate());
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/vrchat-pictures", new { on = true }, cookie, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await UploadsAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task ThePictureSwitchCanBeTurnedOnWhenTheAccountHasVRChatPlusOrItIsNotKnown(bool? hasPlus)
    {
        await SetAsync(hasPlus, pictureUploads: false);
        await using var host = await ApiTestHost.StartAsync(db, new FakeVRChatGate());
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/vrchat-pictures", new { on = true }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await UploadsAsync());
    }

    [Fact]
    public async Task ThePictureSwitchCanStillBeTurnedOffWhileTheAccountHasNoVRChatPlus()
    {
        await SetAsync(hasPlus: false, pictureUploads: true);
        await using var host = await ApiTestHost.StartAsync(db, new FakeVRChatGate());
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Put, "/api/settings/vrchat-pictures", new { on = false }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await UploadsAsync());
    }

    [Fact]
    public async Task WithoutASignInThereIsNoUserToRead_AndVRChatIsNotAsked()
    {
        await using (var context = db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.VRChatSessionUserId = null;
            settings.VRChatAccountUserId = null;
            await context.SaveChangesAsync(Ct);
        }

        var gate = new FakeVRChatGate();
        await using var host = await ApiTestHost.StartAsync(db, gate);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Check, null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(gate.Calls);
    }
}
