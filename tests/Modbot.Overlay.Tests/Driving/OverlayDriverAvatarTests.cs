using Modbot.Companion.Ingest;
using Modbot.Companion.Instances;
using Modbot.Companion.Overlay;
using Modbot.Overlay.Driving;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.Views;
using Modbot.TestSupport;

namespace Modbot.Overlay.Tests.Driving;

/// <summary>
/// Mark avatar on a person's card: it asks the server for exactly that person and only then reads as
/// listed, it does nothing without an avatar, when it is already listed or with no client, and a
/// refusal stays on the card in the server's words.
/// </summary>
public class OverlayDriverAvatarTests
{
    private const string Group = "grp_cats";
    private const string Instance = "39911";

    private sealed class RecordingPresenter : IOverlayPresenter
    {
        public OverlayScreen Last { get; private set; } = OverlayScreen.Idle;

        public bool Update(OverlayScreen screen)
        {
            Last = screen;
            return true;
        }

        public void Show() { }

        public void Hide() { }
    }

    private sealed class Reads : IOverlayReadClient
    {
        public Dictionary<string, ReadResult<UserSummary>> Users { get; } = new(StringComparer.Ordinal);

        public Task<ReadResult<InstanceContext>> GetContextAsync(ServerPairing pairing, string instanceId, string? worldId, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<InstanceContext>(ReadOutcome.Unreachable));

        public Task<ReadResult<LivePollPage>> PollLiveAsync(ServerPairing pairing, string instanceId, string? worldId, string? after, int waitSeconds, CancellationToken cancellationToken)
            => Task.FromResult(new ReadResult<LivePollPage>(ReadOutcome.NothingWaiting, Elapsed: TimeSpan.FromSeconds(waitSeconds)));

        public Task<ReadResult<UserSummary>> GetUserAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
            => Task.FromResult(Users.TryGetValue(subjectId, out var user) ? user : new ReadResult<UserSummary>(ReadOutcome.Unreachable));
    }

    private sealed class Marker : IProblemAvatarClient
    {
        public List<string> Marked { get; } = [];

        public AvatarMarked Answer { get; set; } = new(MarkAvatarOutcome.Done);

        public Task<AvatarMarked> MarkAsync(ServerPairing pairing, string subjectId, CancellationToken cancellationToken)
        {
            Marked.Add(subjectId);
            return Task.FromResult(Answer);
        }
    }

    private static InstanceLocation Location()
    {
        Assert.True(InstanceLocation.TryParse($"wrld_4b34:{Instance}~group({Group})~groupAccessType(members)~region(use)", out var location));
        return location;
    }

    private static UserSummary Kai(string? avatar, bool? listed = null)
        => new("usr_Kai", "Kai", RosterStanding.Member, 0, null, [], [], AvatarName: avatar, AvatarListed: listed);

    private static async Task<(OverlayDriver Driver, RecordingPresenter Presenter)> OpenCard(Marker? marker, UserSummary person)
    {
        var presenter = new RecordingPresenter();
        var reads = new Reads();
        reads.Users["usr_Kai"] = new ReadResult<UserSummary>(ReadOutcome.Fetched, person);
        var driver = new OverlayDriver(presenter, reads, new FakeClock(), avatars: marker);
        driver.Add(new ServerPairing("cats", new Uri("https://cats.example"), "token", Group), "Cat Lounge");
        driver.EnteredInstance(Location());
        await driver.TickAsync(TestContext.Current.CancellationToken);
        await driver.OpenPersonAsync("usr_Kai");
        await driver.TickAsync(TestContext.Current.CancellationToken);
        return (driver, presenter);
    }

    [Fact]
    public async Task MarkAvatarAsksForThatPersonAndTheCardThenReadsAsListed()
    {
        var marker = new Marker();
        var (driver, presenter) = await OpenCard(marker, Kai("Fluffy Fox", listed: false));
        Assert.True(presenter.Last.CanMarkAvatar);

        await driver.MarkAvatarAsync();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["usr_Kai"], marker.Marked);
        Assert.True(presenter.Last.Person?.AvatarListed);
        Assert.Null(presenter.Last.AvatarProblem);
    }

    [Fact]
    public async Task TheTapOnTheButtonSendsTheSameThing()
    {
        var marker = new Marker();
        var (driver, _) = await OpenCard(marker, Kai("Fluffy Fox", listed: false));

        driver.Tap(new OverlayTarget.MarkAvatar());
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(["usr_Kai"], marker.Marked);
    }

    [Fact]
    public async Task NothingIsSentWithNoAvatarOrWhenItIsAlreadyListed()
    {
        var marker = new Marker();
        var (noAvatar, _) = await OpenCard(marker, Kai(null));
        await noAvatar.MarkAvatarAsync();

        var (listed, _) = await OpenCard(marker, Kai("Fluffy Fox", listed: true));
        await listed.MarkAvatarAsync();

        Assert.Empty(marker.Marked);
    }

    [Fact]
    public async Task WithNoClientThePanelOffersNothingAndTheTapDoesNothing()
    {
        var (driver, presenter) = await OpenCard(null, Kai("Fluffy Fox", listed: false));

        await driver.MarkAvatarAsync();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(presenter.Last.CanMarkAvatar);
        Assert.NotEqual(true, presenter.Last.Person?.AvatarListed);
    }

    [Fact]
    public async Task ARefusalStaysOnTheCardInTheServersWordsAndTheAvatarIsNotListed()
    {
        var marker = new Marker { Answer = new AvatarMarked(MarkAvatarOutcome.Refused, "You cannot manage this server's lists.") };
        var (driver, presenter) = await OpenCard(marker, Kai("Fluffy Fox", listed: false));

        await driver.MarkAvatarAsync();
        await driver.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal("You cannot manage this server's lists.", presenter.Last.AvatarProblem);
        Assert.NotEqual(true, presenter.Last.Person?.AvatarListed);
    }

    [Fact]
    public async Task AnUnreachableServerSaysSoAndOpeningSomebodyElseClearsIt()
    {
        var marker = new Marker { Answer = new AvatarMarked(MarkAvatarOutcome.Unreachable) };
        var (driver, presenter) = await OpenCard(marker, Kai("Fluffy Fox", listed: false));

        await driver.MarkAvatarAsync();
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Cat Lounge", presenter.Last.AvatarProblem);

        await driver.OpenPersonAsync("usr_Kai");
        await driver.TickAsync(TestContext.Current.CancellationToken);
        Assert.Null(presenter.Last.AvatarProblem);
    }
}
