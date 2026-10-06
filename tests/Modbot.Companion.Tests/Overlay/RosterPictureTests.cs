using System.Net;
using System.Text;
using Modbot.Companion.Ingest;
using Modbot.Companion.Overlay;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// The roster's picture paths, as the overlay's read makes them whole: only the paired server's own
/// picture route is taken, and on that server.
/// </summary>
public class RosterPictureTests
{
    private sealed class AnswersWith(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private static readonly ServerPairing Pairing = new("cats", new Uri("https://cats.example"), "token", "grp_cats");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<InstanceContext> ReadAsync(string members)
    {
        using var http = new HttpClient(new AnswersWith($$"""{"instanceId":"39911","members":[{{members}}]}"""));
        var reads = new HttpOverlayReadClient(http, new FakeClock());

        var read = await reads.GetContextAsync(Pairing, "39911", "wrld_4b34", Ct);

        Assert.Equal(ReadOutcome.Fetched, read.Outcome);
        return read.Value!;
    }

    [Fact]
    public async Task APicturePathBecomesTheServersWholeAddressForIt()
    {
        var context = await ReadAsync(
            """{"subjectId":"usr_a","displayName":"Rin","standing":"Ordinary","priorActions":0,"flags":[],"pictureUrl":"/api/v1/companion/picture/usr_a?v=0a1b2c3d"}""");

        Assert.Equal("https://cats.example/api/v1/companion/picture/usr_a?v=0a1b2c3d", context.Members[0].PictureUrl);
    }

    [Fact]
    public async Task AnAddressThatIsNotTheServersOwnPictureRouteIsDroppedAndNothingElseOnTheRowChanges()
    {
        var context = await ReadAsync(
            """{"subjectId":"usr_a","displayName":"Rin","standing":"Flagged","priorActions":2,"flags":["2 prior actions"],"pictureUrl":"https://elsewhere.example/a.png"}""");

        var member = context.Members[0];

        Assert.Null(member.PictureUrl);
        Assert.Equal("Rin", member.DisplayName);
        Assert.Equal(RosterStanding.Flagged, member.Standing);
        Assert.Equal(["2 prior actions"], member.Flags);
    }

    [Fact]
    public async Task ARosterWithNoPicturesIsReadAsItWas()
    {
        var context = await ReadAsync(
            """{"subjectId":"usr_a","displayName":"Rin","standing":"Ordinary","priorActions":0,"flags":[]}""");

        Assert.Null(context.Members[0].PictureUrl);
    }
}
