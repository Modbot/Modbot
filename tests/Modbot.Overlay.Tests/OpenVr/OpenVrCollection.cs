namespace Modbot.Overlay.Tests.OpenVr;

/// <summary>
/// The tests that really attach to SteamVR, kept off each other.
/// </summary>
/// <remarks>
/// <para><strong>OpenVR's init and shutdown are process-wide.</strong> <c>VR_InitInternal</c> and
/// <c>VR_ShutdownInternal</c> act on the whole process, not on the object that called them, which
/// is the reason <c>OpenVrSession</c> exists. Test classes run in parallel by default, so two of
/// them attaching and letting go at the same time pull the function table out from under each
/// other, and the next call through it does not fail — it crashes the process, taking the whole
/// suite with it rather than one test.</para>
/// <para>Naming one collection is all it takes: xUnit runs the classes in a collection one after
/// another. Every class that constructs a real <c>OpenVrOverlayRuntime</c> or calls
/// <c>OpenVrSession.Open</c> belongs here.</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenVrCollection
{
    public const string Name = "OpenVR attachment";
}
