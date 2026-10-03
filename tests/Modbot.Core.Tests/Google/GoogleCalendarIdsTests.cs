using System.Net;
using System.Text;
using Modbot.Core.Google;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Google;

/// <summary>
/// Calendar ids as people paste them, the paths they go into, and the links a public calendar gets
/// (Google Calendar design §3.1, §3.9).
/// </summary>
public class GoogleCalendarIdsTests
{
    private const string Id = "c_abc123@group.calendar.google.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Id)]
    [InlineData("  " + Id + "  ")]
    [InlineData("https://calendar.google.com/calendar/embed?src=c_abc123%40group.calendar.google.com&ctz=Europe%2FLondon")]
    [InlineData("https://calendar.google.com/calendar/ical/c_abc123%40group.calendar.google.com/public/basic.ics")]
    [InlineData("https://calendar.google.com/calendar/r?cid=c_abc123%40group.calendar.google.com")]
    public void TheIdIsTakenFromWhateverWasPasted(string pasted)
    {
        Assert.Equal(Id, GoogleCalendarIds.Parse(pasted));
    }

    [Fact]
    public void GooglesShareableLinkCarriesTheIdInBase64()
    {
        var cid = Convert.ToBase64String(Encoding.UTF8.GetBytes(Id)).TrimEnd('=');

        Assert.Equal(Id, GoogleCalendarIds.Parse($"https://calendar.google.com/calendar/u/0?cid={cid}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("two words@gmail.com")]
    [InlineData("https://calendar.google.com/calendar/u/0/r")]
    public void NothingUsableIsRefused(string pasted)
    {
        Assert.Null(GoogleCalendarIds.Parse(pasted));
    }

    [Fact]
    public void TheIdStaysOnePathSegment()
    {
        Assert.Equal("calendars/c_abc123%40group.calendar.google.com", GoogleCalendarClient.CalendarPath(Id));
        Assert.Equal("calendars/..%2F..%2Foauth2%2Fv4%2Ftoken", GoogleCalendarClient.CalendarPath("../../oauth2/v4/token"));
        Assert.Throws<ArgumentException>(() => GoogleCalendarClient.CalendarPath(".."));
    }

    [Fact]
    public async Task ACalendarIdWithSlashesCannotClimbOutOfItsPath()
    {
        var google = new FakeGoogle();
        var client = new GoogleCalendarClient(new OneHandlerClients(google));

        await client.CalendarAsync(google.AccessToken, "../../../oauth2/v4/token", Ct);

        var request = Assert.Single(google.Requests);
        Assert.Equal("www.googleapis.com", request.Uri.Host);
        Assert.StartsWith("/calendar/v3/calendars/", request.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePublicLinksAreGooglesOwn()
    {
        Assert.Equal(
            "https://calendar.google.com/calendar/r?cid=c_abc123%40group.calendar.google.com",
            GoogleCalendarIds.SubscribeLink(Id));
        Assert.Equal(
            "https://calendar.google.com/calendar/embed?src=c_abc123%40group.calendar.google.com&ctz=Europe%2FLondon",
            GoogleCalendarIds.PublicPageLink(Id, "Europe/London"));
        Assert.Equal(
            "https://calendar.google.com/calendar/ical/c_abc123%40group.calendar.google.com/public/basic.ics",
            GoogleCalendarIds.ICalLink(Id));
    }

    [Fact]
    public void AnEventsAddressIsKept_WhenItIsGooglesOwn()
    {
        const string link = "https://www.google.com/calendar/event?eid=bWIwMTIz";

        Assert.Equal(link, GoogleCalendarIds.EventLink(new System.Text.Json.Nodes.JsonObject { ["htmlLink"] = link }));
        Assert.Equal(
            "https://calendar.google.com/calendar/event?eid=x",
            GoogleCalendarIds.EventLink(new System.Text.Json.Nodes.JsonObject { ["htmlLink"] = "https://calendar.google.com/calendar/event?eid=x" }));
    }

    [Theory]
    [InlineData("http://www.google.com/calendar/event?eid=x")]
    [InlineData("https://www.google.com.example.net/calendar/event?eid=x")]
    [InlineData("https://notgoogle.com/calendar/event?eid=x")]
    [InlineData("https://user@www.google.com/calendar/event?eid=x")]
    [InlineData("https://www.google.com:8443/calendar/event?eid=x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/calendar/event?eid=x")]
    public void AnEventsAddressAnywhereElseIsNotKept(string link)
    {
        Assert.Null(GoogleCalendarIds.EventLink(new System.Text.Json.Nodes.JsonObject { ["htmlLink"] = link }));
    }

    [Fact]
    public void AnAnswerWithNoAddressGivesNone()
    {
        Assert.Null(GoogleCalendarIds.EventLink(null));
        Assert.Null(GoogleCalendarIds.EventLink(new System.Text.Json.Nodes.JsonObject()));
        Assert.Null(GoogleCalendarIds.EventLink(new System.Text.Json.Nodes.JsonObject { ["htmlLink"] = 42 }));
    }

    [Theory]
    [InlineData("reader", GooglePublic.All)]
    [InlineData("freeBusyReader", GooglePublic.FreeBusy)]
    [InlineData(null, GooglePublic.No)]
    public async Task ThePublicLineComesFromTheSharingEntryForEveryone(string? role, string expected)
    {
        var google = new FakeGoogle { PublicRole = role };
        var client = new GoogleCalendarClient(new OneHandlerClients(google));

        var shared = await client.PublicAsync(google.AccessToken, Id, Ct);

        Assert.Equal(expected, shared.Value);
    }

    [Fact]
    public async Task ASharingListModbotMayNotReadIsAFailureNotAnAnswer()
    {
        var google = new FakeGoogle { AclStatus = HttpStatusCode.Forbidden };
        var client = new GoogleCalendarClient(new OneHandlerClients(google));

        var shared = await client.PublicAsync(google.AccessToken, Id, Ct);

        Assert.Null(shared.Value);
        Assert.Equal(GoogleProblem.Forbidden, shared.Failure?.Problem);
    }

    [Theory]
    [InlineData("writer", true, true)]
    [InlineData("owner", true, true)]
    [InlineData("reader", false, true)]
    [InlineData("freeBusyReader", false, false)]
    public async Task TheCalendarsNameZoneAndModbotsRoleAreRead(string role, bool canChange, bool canRead)
    {
        var google = new FakeGoogle { AccessRole = role };
        var client = new GoogleCalendarClient(new OneHandlerClients(google));

        var info = (await client.CalendarAsync(google.AccessToken, Id, Ct)).Value;

        Assert.NotNull(info);
        Assert.Equal("Group events", info.Name);
        Assert.Equal("Europe/London", info.TimeZone);
        Assert.Equal(canChange, info.CanChangeEvents);
        Assert.Equal(canRead, info.CanRead);
        Assert.Equal($"Bearer {google.AccessToken}", Assert.Single(google.Requests).Authorization);
    }
}
