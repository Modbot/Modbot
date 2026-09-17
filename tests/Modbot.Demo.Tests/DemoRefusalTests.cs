using Microsoft.AspNetCore.Http;
using Modbot.Api.Auth;
using Modbot.Core.Configuration;
using Modbot.Core.Email;
using Modbot.TestSupport;
using Modbot.VRChat;

namespace Modbot.Demo.Tests;

/// <summary>
/// What a demo refuses: VRChat, Discord, email and pairing (demo mode design §3.2).
/// </summary>
public class DemoRefusalTests
{
    [Fact]
    public async Task TheGateReachesVRChatForNothing()
    {
        var gate = new DemoVRChatGate(new FakeClock());

        Assert.Equal(VRChatSessionState.Unconfigured, gate.State);

        var result = await gate.SignInAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.WasNotSent);
        Assert.Equal(VRChatFailureKind.NotConfigured, result.Kind);
        Assert.Equal(DemoVRChatGate.Message, result.ErrorMessage);
    }

    [Fact]
    public async Task TheGateRefusesEveryCallWithoutIssuingIt()
    {
        var gate = new DemoVRChatGate(new FakeClock());
        var called = false;

        var result = await gate.ExecuteAsync<string>(
            new VRChatEndpoint(VRChatEndpointClass.GroupsMembers),
            (_, _) =>
            {
                called = true;
                throw new InvalidOperationException("The demo must never reach VRChat.");
            },
            ct: TestContext.Current.CancellationToken);

        Assert.False(called);
        Assert.False(result.Success);
        Assert.Equal(DemoVRChatGate.Message, result.ErrorMessage);
    }

    [Fact]
    public async Task NoEmailLeavesADemo()
    {
        var relay = new DemoMailRelay();

        Assert.False(await relay.IsConfiguredAsync(TestContext.Current.CancellationToken));

        var outcome = await relay.SendAsync(
            new EmailMessage("someone@example.invalid", "Hello", "Body", EmailKind.Account),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Sent);
        Assert.Equal(DemoMailRelay.Message, outcome.Error);
    }

    [Fact]
    public async Task NoDiscordMessageLeavesADemo()
    {
        var messenger = new DemoDiscordMessenger();

        Assert.False(await messenger.IsConfiguredAsync(TestContext.Current.CancellationToken));

        var outcome = await messenger.SendDirectMessageAsync(
            "123", "Hello", TestContext.Current.CancellationToken);

        Assert.False(outcome.Sent);
        Assert.Equal(DemoDiscordMessenger.Message, outcome.Error);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/forgot-password")]
    [InlineData("/api/auth/vrchat-link/start")]
    [InlineData("/api/invites")]
    [InlineData("/api/join/abc")]
    [InlineData("/api/reset/abc")]
    [InlineData("/api/users/7f1f2b1e-0000-0000-0000-000000000000/reset-link")]
    [InlineData("/api/companion-devices/pairing-code")]
    [InlineData("/api/v1/companion/pair")]
    [InlineData("/api/settings/email/test")]
    public void SigningInPairingAndMailAreTurnedAway(string path)
        => Assert.True(DemoRefusals.Refuses(new PathString(path)));

    /// <summary>
    /// The rest of Modbot is untouched — including AI, which is the one thing a demo is for.
    /// </summary>
    [Theory]
    [InlineData("/api/members")]
    [InlineData("/api/live")]
    [InlineData("/api/analytics/group")]
    [InlineData("/api/chat/messages")]
    [InlineData("/api/settings/ai")]
    [InlineData("/api/settings/email")]
    [InlineData("/api/onboarding/status")]
    [InlineData("/api/demo")]
    [InlineData("/api/v1/companion/events")]
    [InlineData("/api/companion-devices")]
    public void EverythingElseIsLeftAlone(string path)
        => Assert.False(DemoRefusals.Refuses(new PathString(path)));

    [Fact]
    public void NobodyIsAnAdministratorUnlessStartupSaidSo()
    {
        Assert.False(DemoAuthentication.MayServeEveryoneAsAdministrator(null));

        // Asked for, but never decided: still nobody.
        Assert.False(DemoAuthentication.MayServeEveryoneAsAdministrator(new DemoMode { Requested = true }));

        var ignored = new DemoMode { Requested = true };
        ignored.Decide(hasStaffAccount: true, onboardingComplete: true, holdsDemoData: false);
        Assert.False(DemoAuthentication.MayServeEveryoneAsAdministrator(ignored));

        var on = new DemoMode { Requested = true };
        on.Decide(hasStaffAccount: false, onboardingComplete: false, holdsDemoData: false);
        Assert.True(DemoAuthentication.MayServeEveryoneAsAdministrator(on));
    }
}
