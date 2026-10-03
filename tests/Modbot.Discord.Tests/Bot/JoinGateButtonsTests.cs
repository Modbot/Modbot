using Microsoft.Extensions.DependencyInjection;
using Modbot.Discord.Bot;
using Modbot.Discord.Commands;
using Modbot.Discord.Gate;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Bot;

/// <summary>
/// The join gate's buttons after acting from Discord joined the button routing: still acknowledged
/// the moment they arrive, still answered by the join gate and nothing else, in the server and in a
/// direct message.
/// </summary>
/// <remarks>
/// Acting from Discord stopped acknowledging a press before its handler ran, because a staff button
/// may answer with a form, which must be the first answer (acting from Discord design §11). The join
/// gate's buttons never show a form, and the gate takes a lock and reads settings, the member and
/// their row before it answers, so they keep being acknowledged first. A direct-message press
/// carries no server and reaches this Modbot only by its server mark.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class JoinGateButtonsTests
{
    private const string Guild = "424242";

    private readonly PostgresFixture _db;

    public JoinGateButtonsTests(PostgresFixture db) => _db = db;

    public static TheoryData<string> GateButtons =>
    [
        JoinGateButtons.GetIn,
        JoinGateButtons.Agree,
        JoinGateButtons.Check,
        JoinGateButtons.Hold,
        JoinGateButtons.LiftHold,
        JoinGateButtons.PauseInvites,
    ];

    // ── The gateway: acknowledged first ────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(GateButtons))]
    public void AJoinGateButton_IsAcknowledgedStraightAway_InTheServerAndInADirectMessage(string id)
    {
        var marked = DiscordActionButton.Marked(id, Guild);

        Assert.False(DiscordNetGateway.AnswersInPlace(id));
        Assert.False(DiscordNetGateway.AnswersInPlace(marked));

        // And it still reaches this Modbot from both places.
        Assert.True(DiscordNetGateway.IsOurButton(Guild, ulong.Parse(Guild), marked));
        Assert.True(DiscordNetGateway.IsOurButton(Guild, null, marked));
        Assert.False(DiscordNetGateway.IsOurButton(Guild, null, DiscordActionButton.Marked(id, "999999")));
    }

    [Theory]
    [InlineData(MeCommand.KeepsButton)]
    [InlineData(MeCommand.DeleteButton)]
    [InlineData(MeCommand.InvitesOnButton)]
    [InlineData(MeCommand.InvitesOffButton)]
    public void MesButtons_AreAcknowledgedStraightAway_TooAsTheyWere(string id)
        => Assert.False(DiscordNetGateway.AnswersInPlace(id));

    /// <summary>Only the staff buttons, which may open a form or rewrite their message, wait.</summary>
    [Theory]
    [InlineData(StaffMenus.NoteButton + "v:usr_c9094d86-1846-43eb-b79d-7e3dc318f42a")]
    [InlineData(StaffMenus.ActButton + "ban:usr_c9094d86-1846-43eb-b79d-7e3dc318f42a")]
    [InlineData(StaffMenus.YesButton + "abc123")]
    [InlineData(StaffMenus.NoButton + "abc123")]
    public void StaffButtons_AreNotAcknowledgedFirst(string id)
        => Assert.True(DiscordNetGateway.AnswersInPlace(id));

    // ── The routing: the join gate first ───────────────────────────────────────────────────

    private static DiscordBotService Service(TestServices services, FakeGatewayFactory gateways)
        => new(
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            gateways,
            services.Clock,
            services.Status,
            new DiscordBotOptions
            {
                FirstRetry = TimeSpan.FromSeconds(30),
                MaxRetry = TimeSpan.FromMinutes(10),
                RebuildAfterDisconnected = TimeSpan.FromMinutes(3),
            },
            (_, _) => Task.CompletedTask);

    private static async Task<FakeGateway> ConnectedAsync(TestServices services, CancellationToken ct)
    {
        var gateways = new FakeGatewayFactory();
        var gateway = gateways.Next(g => g.ReadyOnConnect = true);
        var bot = Service(services, gateways);

        await services.ConfigureAsync(s =>
        {
            s.DiscordBotTokenEncrypted = services.Protector.Protect("token");
            s.DiscordGuildId = Guild;
        }, ct);
        await bot.TickAsync(ct);

        return gateway;
    }

    /// <summary>
    /// A member's Get in, I agree and Check, pressed in the direct message the gate sent: answered
    /// once, by the join gate (whose answer while it is off says so), never by the staff handler or
    /// by <c>/me</c>'s.
    /// </summary>
    [Theory]
    [InlineData(JoinGateButtons.GetIn)]
    [InlineData(JoinGateButtons.Agree)]
    [InlineData(JoinGateButtons.Check)]
    public async Task AMembersPress_InADirectMessage_IsAnsweredByTheJoinGate(string id)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = await ConnectedAsync(services, ct);

        var answers = new List<DiscordReply>();
        var press = new DiscordButtonPress(
            "8080", "newcomer", DiscordActionButton.Marked(id, Guild),
            (reply, _) =>
            {
                answers.Add(reply);
                return Task.CompletedTask;
            });

        await gateway.RaiseButtonAsync(press);

        Assert.Equal(JoinGate.GateOff, Assert.Single(answers).Text);
    }

    /// <summary>Staff's Hold on an alert: the join gate's own staff check answers it, once.</summary>
    [Fact]
    public async Task StaffsHold_OnAnAlert_IsAnsweredByTheJoinGate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = await ConnectedAsync(services, ct);

        var answers = new List<DiscordReply>();
        var press = new DiscordButtonPress(
            "5001", "moderator", DiscordActionButton.Marked(JoinGateButtons.Hold, Guild),
            (reply, _) =>
            {
                answers.Add(reply);
                return Task.CompletedTask;
            });

        await gateway.RaiseButtonAsync(press);

        // Nobody connected that Discord account to Modbot, so the gate's own check refuses it.
        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, Assert.Single(answers).Text);
    }
}
