using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// Event templates (calendar templates plan, step 2): read with Manage calendar or Manage templates,
/// changed only with Manage templates, saved as the event's own checks would keep them but with no
/// title, channel or world needed, each change a fact, and a template's picture left to the sweep's keeping.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarTemplateEndpointTests(PostgresFixture db)
{
    private const string Path = "/api/calendar/templates";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync()
    {
        await using (var context = db.NewContext())
            await context.CalendarTemplates.ExecuteDeleteAsync(Ct);

        return await ApiTestHost.StartAsync(db);
    }

    private static async Task<string> ManagerAsync(ApiTestHost host) =>
        (await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageTemplates, Ct)).Cookie;

    private static Dictionary<string, object?> Fields(
        string? title = "Movie night", int lengthMinutes = 120, string repeat = "none", Guid? cover = null)
    {
        return new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = "Bring snacks",
            ["lengthMinutes"] = lengthMinutes,
            ["repeat"] = repeat,
            ["repeatDays"] = repeat == "weekly" ? new[] { "TU" } : Array.Empty<string>(),
            ["category"] = "gaming",
            ["languages"] = Array.Empty<string>(),
            ["platforms"] = Array.Empty<string>(),
            ["tags"] = new[] { "movies" },
            ["publishToVRChat"] = false,
            ["publishToDiscord"] = true,
            ["postToChannel"] = false,
            ["autoOpen"] = false,
            ["coverPictureId"] = cover,
        };
    }

    private static object Body(string? name, Dictionary<string, object?>? fields = null) =>
        new { name, fields = fields ?? Fields() };

    private static async Task<Guid> CreateAsync(ApiTestHost host, string cookie, string name = "Movie night", Dictionary<string, object?>? fields = null)
    {
        var response = await host.SendJsonAsync(HttpMethod.Post, Path, Body(name, fields), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ListAsync(ApiTestHost host, string cookie)
    {
        var response = await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Path, cookie), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.BodyOf(response, Ct);
    }

    private static async Task<Guid> UploadedCoverAsync(ApiTestHost host, string cookie)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);

        var request = host.Authenticated(HttpMethod.Post, "/api/calendar/cover", cookie);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.BodyOf(response, Ct)).GetProperty("coverId").GetGuid();
    }

    [Fact]
    public async Task ATemplateIsMadeReadChangedAndDeleted()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var id = await CreateAsync(host, manager);

        var listed = await ListAsync(host, manager);
        Assert.True(listed.GetProperty("canManage").GetBoolean());
        var template = Assert.Single(listed.GetProperty("templates").EnumerateArray());
        Assert.Equal(id, template.GetProperty("id").GetGuid());
        Assert.Equal("Movie night", template.GetProperty("name").GetString());
        Assert.Equal(120, template.GetProperty("fields").GetProperty("lengthMinutes").GetInt32());

        var changed = await host.SendJsonAsync(
            HttpMethod.Put, $"{Path}/{id}", Body("Film club", Fields(title: "Films", lengthMinutes: 90)), manager, Ct);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        template = Assert.Single((await ListAsync(host, manager)).GetProperty("templates").EnumerateArray());
        Assert.Equal("Film club", template.GetProperty("name").GetString());
        Assert.Equal("Films", template.GetProperty("fields").GetProperty("title").GetString());
        Assert.Equal(90, template.GetProperty("fields").GetProperty("lengthMinutes").GetInt32());

        var deleted = await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, manager, Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await ListAsync(host, manager)).GetProperty("templates").EnumerateArray());
    }

    [Fact]
    public async Task SomeoneWhoOnlyViewsTheCalendarCannotReadOrChangeTemplates()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var id = await CreateAsync(host, manager);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Path, viewer), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, Path, Body("Another"), viewer, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", Body("Another"), viewer, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, viewer, Ct)).StatusCode);
    }

    [Fact]
    public async Task ManageCalendarReadsTemplatesButCannotChangeThem()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var id = await CreateAsync(host, manager);
        var (_, calendar) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var listed = await ListAsync(host, calendar);
        Assert.False(listed.GetProperty("canManage").GetBoolean());
        Assert.Single(listed.GetProperty("templates").EnumerateArray());

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, Path, Body("Another"), calendar, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", Body("Another"), calendar, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, calendar, Ct)).StatusCode);
    }

    [Fact]
    public async Task ManageTemplatesWithoutSeeCalendarCannotReadOrChangeTemplates()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var id = await CreateAsync(host, manager);
        var (_, templatesOnly) = await host.SignedInAsync(ModbotPermissions.ManageTemplates, Ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.Client.SendAsync(host.Authenticated(HttpMethod.Get, Path, templatesOnly), Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Post, Path, Body("Another"), templatesOnly, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", Body("Another"), templatesOnly, Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, templatesOnly, Ct)).StatusCode);
    }

    [Fact]
    public async Task ManageTemplatesWithoutManageCalendarMakesATemplate()
    {
        await using var host = await StartAsync();
        var (_, templates) = await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageTemplates, Ct);

        await CreateAsync(host, templates);

        Assert.True((await ListAsync(host, templates)).GetProperty("canManage").GetBoolean());
    }

    [Theory]
    [InlineData("", 120, "none")]
    [InlineData("   ", 120, "none")]
    [InlineData("Movie night", 0, "none")]
    [InlineData("Movie night", 7 * 24 * 60 + 1, "none")]
    [InlineData("Movie night", 120, "yearly")]
    public async Task ASaveIsRefusedForNoNameABadLengthOrABadRepeat(string name, int lengthMinutes, string repeat)
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var response = await host.SendJsonAsync(
            HttpMethod.Post, Path, Body(name, Fields(lengthMinutes: lengthMinutes, repeat: repeat)), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await ListAsync(host, manager)).GetProperty("templates").EnumerateArray());
    }

    [Fact]
    public async Task ALengthOfSevenDaysIsKept()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        await CreateAsync(host, manager, fields: Fields(lengthMinutes: 7 * 24 * 60));

        var template = Assert.Single((await ListAsync(host, manager)).GetProperty("templates").EnumerateArray());
        Assert.Equal(7 * 24 * 60, template.GetProperty("fields").GetProperty("lengthMinutes").GetInt32());
    }

    [Fact]
    public async Task ABlankTitleChannelAndWorldAreAllowed()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var fields = Fields(title: "");
        fields["postToChannel"] = true;
        fields["channelId"] = null;
        fields["worldId"] = null;

        var response = await host.SendJsonAsync(HttpMethod.Post, Path, Body("Empty", fields), manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AWeeklyRepeatKeepsItsDaysAndNoLastDate()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        await CreateAsync(host, manager, fields: Fields(repeat: "weekly"));

        var fields = Assert.Single((await ListAsync(host, manager)).GetProperty("templates").EnumerateArray()).GetProperty("fields");
        Assert.Equal("weekly", fields.GetProperty("repeat").GetString());
        Assert.Equal(["TU"], fields.GetProperty("repeatDays").EnumerateArray().Select(d => d.GetString()));
        Assert.False(fields.TryGetProperty("repeatUntil", out _));
    }

    [Fact]
    public async Task EachChangeIsRecordedAsAFact()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var id = await CreateAsync(host, manager);
        Assert.Single(await host.FactsAsync(FactType.CalendarTemplateCreated, id.ToString(), Ct));

        await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", Body("Film club"), manager, Ct);
        Assert.Single(await host.FactsAsync(FactType.CalendarTemplateChanged, id.ToString(), Ct));

        await host.SendJsonAsync(HttpMethod.Delete, $"{Path}/{id}", null, manager, Ct);
        Assert.Single(await host.FactsAsync(FactType.CalendarTemplateDeleted, id.ToString(), Ct));
    }

    [Fact]
    public async Task ASavedTemplateWithNothingChangedRecordsNoFact()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var id = await CreateAsync(host, manager);
        await host.SendJsonAsync(HttpMethod.Put, $"{Path}/{id}", Body("Movie night"), manager, Ct);

        Assert.Empty(await host.FactsAsync(FactType.CalendarTemplateChanged, id.ToString(), Ct));
    }

    private async Task<Guid> CategoryAsync(string name)
    {
        var id = Guid.CreateVersion7();

        await using var context = db.NewContext();
        context.CalendarCategories.Add(new CalendarCategory { Id = id, Name = name, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
        await context.SaveChangesAsync(Ct);

        return id;
    }

    private static IEnumerable<Guid> CategoryIdsOf(JsonElement listed) =>
        Assert.Single(listed.GetProperty("templates").EnumerateArray())
            .GetProperty("fields").GetProperty("categoryIds").EnumerateArray().Select(c => c.GetGuid());

    /// <summary>A template keeps the Modbot categories it was saved with.</summary>
    [Fact]
    public async Task ATemplateKeepsItsCategories()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var first = await CategoryAsync("Movies");
        var second = await CategoryAsync("Games");

        var fields = Fields();
        fields["categoryIds"] = new[] { first, second };
        await CreateAsync(host, manager, fields: fields);

        Assert.Equal(new[] { first, second }.Order(), CategoryIdsOf(await ListAsync(host, manager)).Order());
    }

    /// <summary>A save naming a category that is not there is refused, as an event's is.</summary>
    [Fact]
    public async Task ASaveNamingAGoneCategoryIsRefused()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        var fields = Fields();
        fields["categoryIds"] = new[] { Guid.NewGuid() };
        var response = await host.SendJsonAsync(HttpMethod.Post, Path, Body("Gone", fields), manager, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A category deleted after the template was saved is left out when the template is read.</summary>
    [Fact]
    public async Task ADeletedCategoryIsLeftOutOfATemplate()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);
        var kept = await CategoryAsync("Movies");
        var removed = await CategoryAsync("Games");

        var fields = Fields();
        fields["categoryIds"] = new[] { kept, removed };
        await CreateAsync(host, manager, fields: fields);

        await using (var context = db.NewContext())
            await context.CalendarCategories.Where(c => c.Id == removed).ExecuteDeleteAsync(Ct);

        Assert.Equal([kept], CategoryIdsOf(await ListAsync(host, manager)));
    }

    /// <summary>A template saved without categories (older ones are) reads as having none.</summary>
    [Fact]
    public async Task ATemplateWithoutCategoriesHasNone()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        await CreateAsync(host, manager);

        Assert.Empty(CategoryIdsOf(await ListAsync(host, manager)));
    }

    /// <summary>The sweep keeps a picture a template uses, however old, and a template without it loses it.</summary>
    [Fact]
    public async Task TheCoverSweepKeepsATemplatesPicture()
    {
        await using var host = await StartAsync();
        var manager = await ManagerAsync(host);

        // Uploading a picture is a calendar action (Manage calendar), which Manage templates alone does not hold.
        var uploader = (await host.SignedInAsync(ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct)).Cookie;
        var used = await UploadedCoverAsync(host, uploader);
        var unused = await UploadedCoverAsync(host, uploader);

        await CreateAsync(host, manager, fields: Fields(cover: used));

        host.Clock.Advance(CalendarCoverSweep.KeptUnsaved + TimeSpan.FromMinutes(1));
        await using (var context = db.NewContext())
            await new CalendarCoverSweep(context, host.Clock).RunOnceAsync(Ct);

        await using var check = db.NewContext();
        Assert.True(await check.CalendarCoverPictures.AnyAsync(c => c.Id == used, Ct));
        Assert.False(await check.CalendarCoverPictures.AnyAsync(c => c.Id == unused, Ct));
    }
}
