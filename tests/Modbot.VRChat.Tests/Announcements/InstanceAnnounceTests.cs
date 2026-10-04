using System.Text.Json;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Announcements;
using Modbot.VRChat.RateLimiting;

namespace Modbot.VRChat.Tests.Announcements;

public class InstanceAnnounceTests
{
    private const string Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:85019~group(grp_00000000-0000-0000-0000-000000000000)~groupAccessType(plus)~region(eu)";

    [Fact]
    public void TheLocationGoesInThePathAsVRChatGaveIt()
    {
        // VRChat's firewall answers 400 "malformed url" when the colon or brackets are encoded.
        var address = InstanceAnnounce.Address("https://api.vrchat.cloud/api/1", Location);

        Assert.Equal($"https://api.vrchat.cloud/api/1/instances/{Location}/announce", address.OriginalString);
        Assert.Contains(":85019~group(grp_", address.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("a?b", "a%3Fb")]
    [InlineData("a#b", "a%23b")]
    [InlineData("a%b", "a%25b")]
    [InlineData("a b", "a%20b")]
    [InlineData("é", "%C3%A9")]
    public void ACharacterAPathSegmentCannotHoldIsEncoded(string location, string segment)
    {
        Assert.Equal(segment, InstanceAnnounce.PathSegment(location));
    }

    [Fact]
    public void AHostileLocationNeverLeavesTheAnnounceAddress()
    {
        var address = InstanceAnnounce.Address("https://api.vrchat.cloud/api/1", "x/../../users/me?y=1#z");

        Assert.Equal("api.vrchat.cloud", address.Host);
        Assert.EndsWith("/announce", address.AbsolutePath, StringComparison.Ordinal);
        Assert.StartsWith("/api/1/instances/", address.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(string.Empty, address.Query);
        Assert.Equal(string.Empty, address.Fragment);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    public void ALocationThatIsNoSegmentIsNotSent(string? location)
    {
        Assert.False(InstanceAnnounce.CanBePath(location));
    }

    [Fact]
    public void TheBodyIsTheTitleAndTheMessageOnly()
    {
        using var body = JsonDocument.Parse(InstanceAnnounce.Body("Hi \"all\"", "Doors <close> soon"));

        Assert.Equal("Hi \"all\"", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Doors <close> soon", body.RootElement.GetProperty("message").GetString());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void ItsBudgetIsDeclaredTheWayGroupsEditIs()
    {
        var it = VRChatRateLimits.Defaults[VRChatEndpointClass.InstancesAnnounce];
        var edit = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsEdit];

        Assert.Equal(0.1, it.HardMaxPerSecond, 9);
        Assert.Equal(edit.Backstop, it.Backstop);
        Assert.Equal(edit.ResourceScoped, it.ResourceScoped);
        Assert.Equal(VRChatRateLimits.InstancesAnnounceLane, it.Lane);
        Assert.NotEqual(edit.Lane, it.Lane);
    }

    [Fact]
    public void AnAcceptedAnswerIsSent()
    {
        var (state, error, status, _) = AnnouncementSender.Outcome(VRChatResult<string>.Ok("{}", 200), "grp_x", null);

        Assert.Equal(VRChatAnnouncementStates.Sent, state);
        Assert.Null(error);
        Assert.Equal(200, status);
    }

    [Fact]
    public void A403IsRefusedWithVRChatsWordsAndThePermission()
    {
        var result = VRChatResult<string>.Failure(
            403, "Forbidden",
            rawResponse: """{"error":{"message":"You don't have permission to send announcements to this instance","status_code":403}}""");

        var (state, error, status, missing) = AnnouncementSender.Outcome(result, "grp_x", new Settings());

        Assert.Equal(VRChatAnnouncementStates.Refused, state);
        Assert.Equal("You don't have permission to send announcements to this instance", error);
        Assert.Equal(403, status);
        Assert.Equal(VRChatGroupPermissions.CreateInstanceAnnouncement, missing);
    }

    [Fact]
    public void A429FailsAndIsNotSentAgain()
    {
        var result = VRChatResult<string>.Failure(429, "rate limited", kind: VRChatFailureKind.RateLimited);

        var (state, error, status, _) = AnnouncementSender.Outcome(result, "grp_x", null);

        Assert.Equal(VRChatAnnouncementStates.Failed, state);
        Assert.Equal(429, status);
        Assert.Contains("not sent again", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A5xxFailsAsOneThatMayHaveGone()
    {
        var (state, error, _, _) = AnnouncementSender.Outcome(VRChatResult<string>.Failure(502, "Bad Gateway"), "grp_x", null);

        Assert.Equal(VRChatAnnouncementStates.Failed, state);
        Assert.Contains("may have gone out", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOperationNeedsCreateInstanceAnnouncement()
    {
        Assert.Equal(
            VRChatGroupPermissions.CreateInstanceAnnouncement,
            VRChatGroupPermissions.NeededFor(InstanceAnnounce.Operation));
        Assert.Equal("Create Instance Announcement", VRChatGroupPermissions.Label(VRChatGroupPermissions.CreateInstanceAnnouncement));
    }
}
