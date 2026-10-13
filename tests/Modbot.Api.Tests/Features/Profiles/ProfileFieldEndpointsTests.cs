using System.Net;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Profiles;
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

    [Theory]
    [InlineData("staff", "members")]
    [InlineData("staff", "only-me")]
    [InlineData("members", "staff")]
    public async Task WhoSeesAFieldCannotBeChangedAfterTheFieldIsAdded(string added, string tried)
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var made = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, Fields, new { name = "Hobby", kind = "text", defaultVisibility = added }, cookie, Ct), Ct);
        var fieldId = made.GetProperty("id").GetGuid();

        var refused = await host.SendJsonAsync(HttpMethod.Put, $"{Fields}/{fieldId}", new { name = "Hobbies", defaultVisibility = tried }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await using var read = _db.NewContext();
        var stored = await read.ProfileFields.AsNoTracking().SingleAsync(f => f.Id == fieldId, Ct);
        Assert.Equal("Hobby", stored.Name);
        Assert.Equal(added, ProfileFieldEndpoints.Visibility(stored.DefaultVisibility));
    }

    [Fact]
    public async Task ASaveThatSendsTheSameLevelOrNoneIsAccepted()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var made = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, Fields, new { name = "Hobby", kind = "text", defaultVisibility = "members" }, cookie, Ct), Ct);
        var url = $"{Fields}/{made.GetProperty("id").GetGuid()}";

        Assert.Equal(HttpStatusCode.OK, (await host.SendJsonAsync(HttpMethod.Put, url, new { name = "Hobbies", defaultVisibility = "members" }, cookie, Ct)).StatusCode);

        var withoutLevel = await host.SendJsonAsync(HttpMethod.Put, url, new { name = "Pastimes" }, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, withoutLevel.StatusCode);
        Assert.Equal("members", (await ApiTestHost.BodyOf(withoutLevel, Ct)).GetProperty("defaultVisibility").GetString());
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

    private static async Task AddRolesAsync(PostgresFixture db, params string[] ids)
    {
        await using var context = db.NewContext();
        await context.DiscordRoles.Where(r => ids.Contains(r.RoleId)).ExecuteDeleteAsync(Ct);
        context.DiscordRoles.AddRange(ids.Select(id => new DiscordRole { RoleId = id, GuildId = "88000", Name = "Role " + id }));
        await context.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AnOptionCanBeLinkedToARoleOfTheServerButNotToAnUnknownOneOrToTwoOptions()
    {
        await ClearAsync(_db);
        await AddRolesAsync(_db, "88001", "88002");
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        object Body(params string?[] roles) => new
        {
            name = "Pronouns",
            kind = "pick-list",
            options = roles.Select((role, i) => new { id = (Guid?)null, label = "option " + i, roleId = role }).ToArray(),
        };

        var unknown = await host.SendJsonAsync(HttpMethod.Post, Fields, Body("88999"), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var twice = await host.SendJsonAsync(HttpMethod.Post, Fields, Body("88001", "88001"), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        Assert.Equal("Two options use the same role.", (await ApiTestHost.BodyOf(twice, Ct)).GetProperty("error").GetString());

        var saved = await host.SendJsonAsync(HttpMethod.Post, Fields, Body("88001", null), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var options = (await ApiTestHost.BodyOf(saved, Ct)).GetProperty("options").EnumerateArray().ToList();
        Assert.Equal("88001", options[0].GetProperty("roleId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, options[1].GetProperty("roleId").ValueKind);
    }

    [Fact]
    public async Task CopyFromRolesNeedsChangeSettingsFillsTheMembersAndSaysHowMany()
    {
        await ClearAsync(_db);
        await AddRolesAsync(_db, "88001");
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var field = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Post, Fields, new
        {
            name = "Pronouns",
            kind = "pick-list",
            options = new[] { new { id = (Guid?)null, label = "she/her", roleId = (string?)"88001" } },
        }, cookie, Ct), Ct);
        var id = field.GetProperty("id").GetGuid();

        await using (var context = _db.NewContext())
        {
            await context.DiscordMembers.Where(m => m.GuildId == "88000").ExecuteDeleteAsync(Ct);
            foreach (var (user, roles) in new[] { ("88101", "[\"88001\"]"), ("88102", "[\"88001\"]"), ("88103", "[]") })
            {
                context.DiscordMembers.Add(new DiscordMember
                {
                    GuildId = "88000",
                    UserId = user,
                    Username = "u" + user,
                    DisplayName = "U " + user,
                    Roles = roles,
                    FirstSeenAt = host.Clock.UtcNow,
                    UpdatedAt = host.Clock.UtcNow,
                });
            }

            await context.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendJsonAsync(HttpMethod.Post, $"{Fields}/{id}/copy-from-roles", null, viewer, Ct)).StatusCode);

        var done = await host.SendJsonAsync(HttpMethod.Post, $"{Fields}/{id}/copy-from-roles", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(2, (await ApiTestHost.BodyOf(done, Ct)).GetProperty("filled").GetInt32());

        var again = await host.SendJsonAsync(HttpMethod.Post, $"{Fields}/{id}/copy-from-roles", null, cookie, Ct);
        Assert.Equal(0, (await ApiTestHost.BodyOf(again, Ct)).GetProperty("filled").GetInt32());

        var profile = await host.SendJsonAsync(HttpMethod.Get, "/api/discord/members/88101/profile", null, viewer, Ct);
        var lines = (await ApiTestHost.BodyOf(profile, Ct)).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal("she/her", Assert.Single(lines).GetProperty("answer").GetString());

        await using var cleanup = _db.NewContext();
        await cleanup.DiscordMembers.Where(m => m.GuildId == "88000").ExecuteDeleteAsync(Ct);
    }

    [Fact]
    public async Task AMembersPageSaysWhetherTheyAreFindable()
    {
        await ClearAsync(_db);
        await using var host = await ApiTestHost.StartAsync(_db);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);

        var off = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/discord/members/88201/profile", null, viewer, Ct), Ct);
        Assert.False(off.GetProperty("findable").GetBoolean());

        await using (var context = _db.NewContext())
        {
            context.MemberProfiles.Add(new MemberProfile { DiscordUserId = "88201", Findable = true, UpdatedAt = host.Clock.UtcNow });
            await context.SaveChangesAsync(Ct);
        }

        var on = await ApiTestHost.BodyOf(await host.SendJsonAsync(HttpMethod.Get, "/api/discord/members/88201/profile", null, viewer, Ct), Ct);
        Assert.True(on.GetProperty("findable").GetBoolean());

        await using var cleanup = _db.NewContext();
        await cleanup.MemberProfiles.Where(m => m.DiscordUserId == "88201").ExecuteDeleteAsync(Ct);
    }
}
