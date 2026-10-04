using Modbot.Companion.Ingest;
using Modbot.Companion.Overlay;
using Modbot.Companion.Presentation;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.Overlay;

/// <summary>
/// A person the Debug page made up is never asked about and never sent: the two clients that
/// would name a person to a server stop before any request.
/// </summary>
public class TestPeopleTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private static readonly ServerPairing Pairing = new("cats", new Uri("https://cats.example"), "token", "grp_cats");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheTestEventsUseTheSharedMarker()
    {
        Assert.Equal(TestPeople.Prefix, TestEvents.SubjectPrefix);
        Assert.True(TestPeople.IsTest(TestEvents.SubjectOf("Rin")));
        Assert.False(TestPeople.IsTest("usr_c1644b5b-3ca4-45b4-97c6-a2a0de70d469"));
        Assert.False(TestPeople.IsTest(null));
    }

    [Fact]
    public async Task ATestPersonsProfileIsNeverRead()
    {
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var reads = new HttpOverlayReadClient(http, new FakeClock());

        var read = await reads.GetUserAsync(Pairing, TestEvents.SubjectOf("Rin"), Ct);

        Assert.Equal(0, handler.Requests);
        Assert.NotEqual(ReadOutcome.Fetched, read.Outcome);

        await reads.GetUserAsync(Pairing, "usr_real", Ct);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task AHeadsUpOnATestPersonIsNeverSent()
    {
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var headsUps = new HttpHeadsUpClient(http);

        var sent = await headsUps.PlaceAsync(
            Pairing, "39911", "wrld_4b34", new HeadsUpDraft(TestEvents.SubjectOf("Rin"), "Rin", Text: "watch"), Ct);

        Assert.Equal(0, handler.Requests);
        Assert.Equal(HeadsUpSendOutcome.Refused, sent.Outcome);
    }
}
