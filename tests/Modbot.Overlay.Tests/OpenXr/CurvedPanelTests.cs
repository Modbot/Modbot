using System.Numerics;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenXr;

namespace Modbot.Overlay.Tests.OpenXr;

/// <summary>
/// The cylinder maths (overlay OpenXR and interaction design, 4.2): curve 0 is flat, curve 1
/// wraps the width round a full circle, and the layer's pose is the centre line one radius in
/// front of the panel.
/// </summary>
public class CurvedPanelTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    public void NoCurveMeansFlat(float curve)
    {
        Assert.Null(CurvedPanel.For(0.45f, curve));
    }

    [Fact]
    public void AWidthOfNothingIsFlatToo()
    {
        Assert.Null(CurvedPanel.For(0f, 0.5f));
    }

    [Fact]
    public void CurveOneWrapsTheWidthRoundAFullCircle()
    {
        var curved = CurvedPanel.For(1f, 1f);

        Assert.NotNull(curved);
        Assert.Equal(1f / MathF.Tau, curved.Value.Radius, 1e-6f);
        // Just under a full turn, as the layer requires.
        Assert.Equal(CurvedPanel.MaxCentralAngle, curved.Value.CentralAngle, 1e-6f);
        Assert.True(curved.Value.CentralAngle < MathF.Tau);
    }

    [Theory]
    [InlineData(0.45f, 0.5f)]
    [InlineData(1.5f, 0.25f)]
    [InlineData(0.2f, 0.1f)]
    public void RadiusComesFromTheWidthAndTheCentralAngleFromTheRadius(float width, float curve)
    {
        var curved = CurvedPanel.For(width, curve);

        Assert.NotNull(curved);
        Assert.Equal(width / (MathF.Tau * curve), curved.Value.Radius, 1e-5f);
        Assert.Equal(width / curved.Value.Radius, curved.Value.CentralAngle, 1e-5f);
        // The arc is the same length as the flat panel is wide.
        Assert.Equal(width, curved.Value.Radius * curved.Value.CentralAngle, 1e-5f);
    }

    [Fact]
    public void ACurveAboveOneIsTreatedAsOne()
    {
        var one = CurvedPanel.For(0.5f, 1f);
        var more = CurvedPanel.For(0.5f, 3f);

        Assert.Equal(one, more);
    }

    [Fact]
    public void TheCentreLineIsOneRadiusInFrontOfThePanelAlongItsOwnZ()
    {
        var curved = new CurvedPanel(0.5f, 1f);

        // A panel one metre ahead, facing back: its +Z points at the viewer, and the centre
        // sits between the two.
        var centre = curved.CentreOf(new Pose(new Vector3(0f, 0f, -1f), Quaternion.Identity));
        Assert.Equal(new Vector3(0f, 0f, -0.5f), centre.Position);
        Assert.Equal(Quaternion.Identity, centre.Rotation);

        // Turned to face the other way, the centre goes the other way with it.
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        var away = curved.CentreOf(new Pose(new Vector3(0f, 0f, -1f), turned));
        Assert.Equal(0f, away.Position.X, 1e-5f);
        Assert.Equal(-1.5f, away.Position.Z, 1e-5f);
    }
}
