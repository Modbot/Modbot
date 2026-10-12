using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Profiles;

/// <summary>
/// Settings → Member profiles (member profiles design §2): who may change the fields, what a save
/// checks, that a new field is Staff unless told otherwise, and that an option keeps its id when
/// it is renamed.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class ProfileFieldEndpointsTests
{
    private const string Fields = "/api/settings/profile-fields";

    private readonly PostgresFixture _db;

    public ProfileFieldEndpointsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task ClearAsync(PostgresFixture db)
    {
        await using var context = db.NewContext();
        await context.ProfileValues.ExecuteDeleteAsync(Ct);
        await context.ProfileOptions.ExecuteDeleteAsync(Ct);
        await context.ProfileFields.ExecuteDeleteAsync(Ct);
    }

    private static object PickList(string name, params string[] options) => new
    {
        name,
        kind = "pick-list",
        options = options.Select(label => new { id = (Guid?)null, label }).ToArray(),
    };

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task TheFieldsNeedChangeSettings(string method)
    {
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var response = await host.SendJsonAsync(new HttpMethod(method), Fields, method == "POST" ? PickList("Pronouns", "she/her") : null, cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ANewFieldIsStaffUntilTheAdminSaysOtherwise()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Fields, PickList("Pronouns", "she/her", "he/him"), cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal("staff", body.GetProperty("defaultVisibility").GetString());
        Assert.Equal(["she/her", "he/him"], body.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()!));
    }

    [Fact]
    public async Task ATextFieldTakesNoOptionsAndKeepsTheDefaultTheAdminChose()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Fields, new { name = "Hobby", kind = "text", defaultVisibility = "members" }, cookie, Ct);

        var body = await ApiTestHost.BodyOf(response, Ct);
        Assert.Equal("text", body.GetProperty("kind").GetString());
        Assert.Equal("members", body.GetProperty("defaultVisibility").GetString());
        Assert.Empty(body.GetProperty("options").EnumerateArray());
    }

    [Theory]
    [InlineData("", "text", "name is required.")]
    [InlineData("Hobby", "number", "kind must be pick-list or text.")]
    [InlineData("Hobby", null, "kind must be pick-list or text.")]
    public async Task AFieldThatCannotWorkIsRefused(string name, string? kind, string error)
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, Fields, new { name, kind }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await ApiTestHost.BodyOf(response, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ANameIsRefusedWhenItIsTooLongOrAlreadyTaken()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var tooLong = await host.SendJsonAsync(HttpMethod.Post, Fields, new { name = new string('x', ProfileField.MaxNameLength + 1), kind = "text" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Post, Fields, new { name = "Hobby", kind = "text" }, cookie, Ct)).StatusCode);

        var twin = await host.SendJsonAsync(HttpMethod.Post, Fields, new { name = "hobby", kind = "text" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, twin.StatusCode);
        Assert.Equal("Another field has that name.", (await ApiTestHost.BodyOf(twin, Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task APickListNeedsOptionsAndNoTwoShareAName()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var none = await host.SendJsonAsync(HttpMethod.Post, Fields, PickList("Pronouns"), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("A pick-list needs at least one option.", (await ApiTestHost.BodyOf(none, Ct)).GetProperty("error").GetString());

        var twice = await host.SendJsonAsync(HttpMethod.Post, Fields, PickList("Pronouns", "she/her", "She/Her"), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
    }

    [Fact]
    public async Task ARenamedOptionKeepsItsIdAndAnOptionLeftOutIsRemoved()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var made = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, Fields, PickList("Pronouns", "she/her", "he/him"), cookie, Ct), Ct);
        var fieldId = made.GetProperty("id").GetGuid();
        var first = made.GetProperty("options")[0].GetProperty("id").GetGuid();

        var saved = await host.SendJsonAsync(
            HttpMethod.Put,
            $"{Fields}/{fieldId}",
            new { name = "Pronoun", options = new[] { new { id = (Guid?)first, label = "she / her" } } },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await ApiTestHost.BodyOf(saved, Ct);
        Assert.Equal("Pronoun", body.GetProperty("name").GetString());
        var options = body.GetProperty("options").EnumerateArray().ToList();
        Assert.Single(options);
        Assert.Equal(first, options[0].GetProperty("id").GetGuid());
        Assert.Equal("she / her", options[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task TheKindCannotBeChangedAfterTheFieldIsAdded()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var made = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, Fields, new { name = "Hobby", kind = "text" }, cookie, Ct), Ct);

        var saved = await host.SendJsonAsync(HttpMethod.Put, $"{Fields}/{made.GetProperty("id").GetGuid()}", PickList("Hobby", "chess"), cookie, Ct);

        Assert.Equal("text", (await ApiTestHost.BodyOf(saved, Ct)).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task FieldsAreListedInTheOrderTheAdminSetAndRemovingOneDeletesItsAnswers()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var a = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, Fields, new { name = "A", kind = "text" }, cookie, Ct), Ct)).GetProperty("id").GetGuid();
        var b = (await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, Fields, new { name = "B", kind = "text" }, cookie, Ct), Ct)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, $"{Fields}/order", new { ids = new[] { b, a } }, cookie, Ct)).StatusCode);

        await using (var context = _db.NewContext())
        {
            context.ProfileValues.Add(new ProfileValue { FieldId = a, DiscordUserId = "7001", Text = "hello", UpdatedAt = host.Clock.UtcNow });
            await context.SaveChangesAsync(Ct);
        }

        var listed = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, Fields, null, cookie, Ct), Ct);
        Assert.Equal(["B", "A"], listed.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!));

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendJsonAsync(HttpMethod.Delete, $"{Fields}/{a}", null, cookie, Ct)).StatusCode);

        await using var read = _db.NewContext();
        Assert.Empty(await read.ProfileValues.AsNoTracking().Where(v => v.FieldId == a).ToListAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendJsonAsync(HttpMethod.Delete, $"{Fields}/{a}", null, cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task OnlyStaffWhoMaySeeMembersReadAMembersAnswers()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);

        var (_, without) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Get, "/api/discord/members/7001/profile", null, without, Ct)).StatusCode);

        var (_, with) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var response = await host.SendJsonAsync(HttpMethod.Get, "/api/discord/members/7001/profile", null, with, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ApiTestHost.BodyOf(response, Ct)).GetProperty("lines").EnumerateArray());
    }
}
