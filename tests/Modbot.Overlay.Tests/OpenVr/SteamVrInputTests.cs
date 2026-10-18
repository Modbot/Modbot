using System.Buffers.Binary;
using System.Numerics;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;

namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// SteamVR Input's action data turned into the hand data the panels read, and the choice between
/// it and the legacy controller state. None of it needs SteamVR.
/// </summary>
public class SteamVrInputTests
{
    private static byte[] Analog(bool active, float x, float y)
    {
        var bytes = new byte[InputActionLayouts.AnalogSize];
        bytes[0] = active ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), x);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20), y);
        return bytes;
    }

    private static byte[] Digital(bool active, bool state)
    {
        var bytes = new byte[InputActionLayouts.DigitalSize];
        bytes[0] = active ? (byte)1 : (byte)0;
        bytes[16] = state ? (byte)1 : (byte)0;
        return bytes;
    }

    /// <summary>An upright pose at <paramref name="x"/> metres, the pose starting at byte 16 as in InputPoseActionData_t.</summary>
    private static byte[] PoseAt(bool active, bool valid, float x)
    {
        var bytes = new byte[InputActionLayouts.PoseSize];
        bytes[0] = active ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16 + 12), x);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16 + 20), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16 + 40), 1f);
        bytes[16 + 76] = valid ? (byte)1 : (byte)0;
        return bytes;
    }

    [Fact]
    public void AnAnalogActionIsReadAtItsOffsets()
    {
        Assert.Equal((true, new Vector2(0.3f, -0.6f)), InputActionLayouts.ReadAnalog(Analog(true, 0.3f, -0.6f)));
    }

    [Fact]
    public void AnActionThatIsNotBoundReadsAsInactiveWhateverItsBytesSay()
    {
        Assert.Equal((false, Vector2.Zero), InputActionLayouts.ReadAnalog(Analog(false, 0.9f, 0.9f)));
        Assert.Equal((false, false), InputActionLayouts.ReadDigital(Digital(false, true)));
        Assert.False(InputActionLayouts.ReadPose(PoseAt(false, true, 1f)).Active);
    }

    [Fact]
    public void ADigitalActionIsReadAtItsOffset()
    {
        Assert.Equal((true, true), InputActionLayouts.ReadDigital(Digital(true, true)));
        Assert.Equal((true, false), InputActionLayouts.ReadDigital(Digital(true, false)));
    }

    [Fact]
    public void ATooShortAnswerIsInactive()
    {
        Assert.Equal((false, Vector2.Zero), InputActionLayouts.ReadAnalog(new byte[InputActionLayouts.AnalogSize - 1]));
        Assert.Equal((false, false), InputActionLayouts.ReadDigital(new byte[InputActionLayouts.DigitalSize - 1]));
        Assert.Equal((false, (Pose?)null), InputActionLayouts.ReadPose(new byte[InputActionLayouts.PoseSize - 1]));
    }

    [Fact]
    public void APoseActionHoldsTheDevicePoseAfterItsHeader()
    {
        var (active, pose) = InputActionLayouts.ReadPose(PoseAt(true, true, 0.75f));

        Assert.True(active);
        Assert.NotNull(pose);
        Assert.True(MathF.Abs(pose.Value.Position.X - 0.75f) < 1e-5f);
    }

    /// <summary>Live but not valid is not tracked: the action is bound, the controller is not seen.</summary>
    [Fact]
    public void ALivePoseThatIsNotValidIsActiveWithNoPose()
    {
        var (active, pose) = InputActionLayouts.ReadPose(PoseAt(true, false, 0.75f));

        Assert.True(active);
        Assert.Null(pose);
    }

    [Fact]
    public void AHandWithNoDevicePoseIsMissingAndLetsGoOfThePanel()
    {
        var hold = new GripHold();
        hold.Squeeze(button: false, 1f);

        var hand = new ActionHand(true, 1f, false, true, Vector2.Zero, null, null).ToHandState(hold);

        Assert.False(hand.Tracked);
        Assert.False(hold.Held);
    }

    [Fact]
    public void TheGrabValueGoesThroughTheSameGripHoldAsTheLegacyAxis()
    {
        var device = new Pose(new Vector3(0.1f, 1f, -0.3f), Quaternion.Identity);
        var aim = new Pose(new Vector3(0.1f, 1f, -0.3f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f));

        var light = new ActionHand(true, GripHold.Take - 0.01f, false, false, Vector2.Zero, device, aim)
            .ToHandState(new GripHold());
        var firm = new ActionHand(true, GripHold.Take, false, true, new Vector2(0f, 0.5f), device, aim)
            .ToHandState(new GripHold());

        Assert.False(light.Grab);
        Assert.True(firm.Grab);
        Assert.True(firm.Click);
        Assert.Equal(new Vector2(0f, 0.5f), firm.Scroll);
        Assert.True(firm.Tracked);
        Assert.Equal(device, firm.Device);
        Assert.Equal(aim, firm.Aim);
    }

    /// <summary>A Vive wand's grip is a switch: the grip button is the only grab it has.</summary>
    [Fact]
    public void TheGripButtonGrabsWithNoAnalogueValue()
    {
        var hand = new ActionHand(true, 0f, true, false, Vector2.Zero, Pose.Identity, null)
            .ToHandState(new GripHold());

        Assert.True(hand.Grab);
    }

    [Fact]
    public void WithoutAPointingPoseTheLegacyTiltOffTheDevicePoseStandsIn()
    {
        var device = new Pose(new Vector3(0f, 1f, 0f), Quaternion.Identity);

        var hand = new ActionHand(true, 0f, false, false, Vector2.Zero, device, null)
            .ToHandState(new GripHold());

        Assert.Equal(ControllerPointing.Aim(device), hand.Aim);
    }

    [Fact]
    public void TheLegacyReadIsUsedWhenSteamVrInputCouldNotBeSetUp()
    {
        var choice = new InputSourceChoice(actionsAvailable: false);

        Assert.True(choice.Settled);
        Assert.Equal(ControllerInputSource.Legacy, choice.Next(actionsReportData: true, controllerSeen: true));
    }

    [Fact]
    public void TheLegacyReadIsUsedUntilAnActionReportsData()
    {
        var choice = new InputSourceChoice(actionsAvailable: true);

        Assert.Equal(ControllerInputSource.Legacy, choice.Next(false, true));
        Assert.False(choice.Settled);
        Assert.Equal(ControllerInputSource.SteamVrInput, choice.Next(true, true));
        Assert.True(choice.Settled);
    }

    [Fact]
    public void SteamVrInputOnceSeenWorkingIsKept()
    {
        var choice = new InputSourceChoice(actionsAvailable: true);
        choice.Next(true, true);

        Assert.Equal(ControllerInputSource.SteamVrInput, choice.Next(false, true));
    }

    [Fact]
    public void NoDataWithAControllerInViewIsWrittenOffAfterAWhile_ButALateBindingStillTakesOver()
    {
        var choice = new InputSourceChoice(actionsAvailable: true);

        for (var i = 0; i < InputSourceChoice.QuietPollsBeforeLegacy; i++)
            choice.Next(false, true);

        Assert.True(choice.Settled);
        Assert.Equal(ControllerInputSource.Legacy, choice.Source);
        Assert.Equal(ControllerInputSource.SteamVrInput, choice.Next(true, true));
    }

    /// <summary>With no controller in view, nothing is learned about whether the bindings work.</summary>
    [Fact]
    public void PollsWithNoControllerDoNotCountAgainstSteamVrInput()
    {
        var choice = new InputSourceChoice(actionsAvailable: true);

        for (var i = 0; i < InputSourceChoice.QuietPollsBeforeLegacy * 2; i++)
            choice.Next(false, false);

        Assert.False(choice.Settled);
    }
}
