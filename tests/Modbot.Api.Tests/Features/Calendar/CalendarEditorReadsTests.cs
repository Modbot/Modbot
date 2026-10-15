using System.Net;
using System.Net.Http;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Calendar;

/// <summary>
/// The read the event form makes when it opens, which the template editor makes too: the staff to
/// invite. Somebody who sees the calendar and may change templates, but not
/// events, reads it; See calendar alone does not.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CalendarEditorReadsTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/calendar/invite-choices")]
    public async Task ATemplateManagerWhoCanSeeTheCalendarMayRead(string path)
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, templateManager) = await host.SignedInAsync(
            ModbotPermissions.ViewCalendar | ModbotPermissions.ManageTemplates, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, path, null, templateManager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/calendar/invite-choices")]
    public async Task AnEventManagerStillReads(string path)
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, manager) = await host.SignedInAsync(
            ModbotPermissions.ViewCalendar | ModbotPermissions.ManageCalendar, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, path, null, manager, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/calendar/invite-choices")]
    public async Task SeeingTheCalendarAloneMayNotRead(string path)
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, viewer) = await host.SignedInAsync(ModbotPermissions.ViewCalendar, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, path, null, viewer, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/calendar/invite-choices")]
    public async Task ManageTemplatesWithoutSeeCalendarMayNotRead(string path)
    {
        await using var host = await ApiTestHost.StartAsync(db);
        var (_, templateManager) = await host.SignedInAsync(ModbotPermissions.ManageTemplates, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Get, path, null, templateManager, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
