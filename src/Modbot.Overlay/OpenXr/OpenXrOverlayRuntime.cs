using Modbot.Companion.Overlay;
using Modbot.Overlay.Interaction;
using Modbot.Overlay.OpenVr;
using Modbot.Overlay.Rendering;
using Serilog;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using Vulkan = Silk.NET.Vulkan;

namespace Modbot.Overlay.OpenXr;

/// <summary>
/// WiVRn and Monado, through an OpenXR overlay session.
/// </summary>
/// <remarks>
/// <para><strong>What this does.</strong> Opens a second OpenXR session marked as an overlay
/// (<c>XR_EXTX_overlay</c>), whose layers the runtime draws over VRChat's own frames. Each layer
/// sits where its placement says — on the head in the <c>VIEW</c> space, on a hand in that hand's
/// aim space, or left in the room in <c>LOCAL</c>. The controllers are read through one action set
/// (<see cref="OpenXrInput"/>) so the main panel can be pointed at and held; that is the entire
/// interaction: no other session is inspected, and nothing is sent anywhere.</para>
/// <para><strong>Both panels ride one session.</strong> Modbot draws two panels, the main one and
/// the notification one, and on OpenXR they are two layers on the same frame rather than two
/// sessions. A second session would mean a second <c>XrInstance</c>, a second Vulkan device and a
/// second frame thread on a machine that is also running VRChat, for two quads the runtime is
/// perfectly happy to take together. So this type is the main panel's runtime, and
/// <see cref="NotificationPanel"/> is a second one onto the same session; the session is attached
/// when the first of them starts and let go when the last is disposed, so either panel can be
/// switched off without taking the other down (two overlay modes design §4.2).</para>
/// <para><strong>Opacity and curve</strong> are two optional extensions. Opacity goes through
/// <c>XR_KHR_composition_layer_color_scale_bias</c> and curve through
/// <c>XR_KHR_composition_layer_cylinder</c>; each is asked for only when the runtime lists it,
/// and without it the panel is simply drawn solid, or flat.</para>
/// <para><strong>Why OpenXR at all.</strong> On Linux, WiVRn and Monado users run OpenVR games
/// through xrizer, which by design takes games only and refuses overlays. The OpenXR overlay
/// extension is how desktop overlays work on those runtimes, so it is how this one does. On a
/// machine with SteamVR the OpenVR runtime is tried first and this one is never reached, because
/// SteamVR's own OpenXR has no overlay extension (overlay-on-OpenXR spec, 3.1).</para>
/// <para><strong>A layer is submitted every frame</strong>, unlike OpenVR's set-once texture, so
/// the runtime owns a frame thread. A picture is copied into a swapchain image only when the
/// compositor drew something new; every other frame resubmits the images already there, which
/// costs one <c>xrEndFrame</c> and nothing else.</para>
/// <para><strong>Nothing is shipped and nothing is launched.</strong> The OpenXR loader and
/// Vulkan come from the machine; a missing loader is a state, not a crash, and starting never
/// starts a runtime. Absence is reported, and the companion looks again in ten seconds.</para>
/// </remarks>
public sealed class OpenXrOverlayRuntime : IOverlayRuntime
{
    public const string OverlayExtension = "XR_EXTX_overlay";

    public const string VulkanExtension = "XR_KHR_vulkan_enable2";

    /// <summary>Opacity: a colour scale on the layer. Optional.</summary>
    public const string ColorScaleExtension = "XR_KHR_composition_layer_color_scale_bias";

    /// <summary>Curve: the panel as a cylinder layer. Optional.</summary>
    public const string CylinderExtension = "XR_KHR_composition_layer_cylinder";

    /// <summary><c>XR_MAKE_VERSION(1, 0, 0)</c>: the overlay extension predates OpenXR 1.1, and every runtime speaks 1.0.</summary>
    private const ulong OpenXrApiVersion = 1UL << 48;

    /// <summary><c>XR_INFINITE_DURATION</c>.</summary>
    private const long InfiniteDuration = 0x7fffffffffffffff;

    private static XR? _xrApi;
    private static Vulkan.Vk? _vkApi;

    /// <summary>The main panel and the notification panel, in that order.</summary>
    internal const int PanelCount = 2;

    private const int MainPanel = 0;

    private const int NotifyPanel = 1;

    private readonly float _widthInMetres;
    private readonly ILogger _log;
    private readonly LatestTracking _tracking = new();
    private readonly PanelState[] _panels;
    private readonly Lock _gate = new();

    private volatile Attachment? _attachment;
    private volatile OverlayRuntimeStatus _status = new(OverlayRuntimeState.NotStarted, Detail: "OpenXR has not been looked for yet.");
    private Thread? _frameThread;
    private volatile bool _stop;
    private int _wanted;

    /// <param name="resolution">The main panel's texture, square.</param>
    /// <param name="widthInMetres">How wide the main panel starts, until a placement says otherwise.</param>
    /// <param name="log">Where the attachment's story goes.</param>
    /// <param name="notificationResolution">
    /// The notification panel's texture. Null makes it the same as the main panel's; the client
    /// gives it a smaller one, because a pop-up is three lines.
    /// </param>
    public OpenXrOverlayRuntime(
        int resolution = OverlayHost.DefaultResolution,
        float widthInMetres = OpenVrOverlayRuntime.DefaultWidthInMetres,
        ILogger? log = null,
        int? notificationResolution = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(notificationResolution ?? 1, 1);

        _widthInMetres = widthInMetres;
        _log = log ?? Log.ForContext<OpenXrOverlayRuntime>();

        // Each panel keeps its own size: the swapchains are made once, at these, and the sub-image
        // rectangle a layer names has to match the swapchain it came from.
        _panels = [new PanelState(resolution), new PanelState(notificationResolution ?? resolution)];
        NotificationPanel = new PanelRuntime(this, NotifyPanel);
    }

    /// <summary>
    /// The notification panel, as a runtime of its own: the same session, its own layer, its own
    /// placement and its own on and off.
    /// </summary>
    public IOverlayRuntime NotificationPanel { get; }

    private static readonly Lock SharedGate = new();

    private static OpenXrOverlayRuntime? _shared;

    /// <summary>
    /// The one OpenXR session the client's panels share, made on the first ask.
    /// </summary>
    /// <remarks>
    /// <para>Shared for the same reason <see cref="OpenVrSession"/> is: one process, one session.
    /// Either panel may be the first to ask for it and either may be the last to let it go, so
    /// neither can own it. Letting go tears the attachment down but leaves this object, which
    /// attaches again the next time a panel starts.</para>
    /// <para>The sizes are taken from the first ask and kept, because the swapchains are made
    /// once. In this client both asks pass the same two constants.</para>
    /// </remarks>
    public static OpenXrOverlayRuntime Shared(
        int resolution = OverlayHost.DefaultResolution,
        int notificationResolution = OverlayHost.DefaultNotificationResolution)
    {
        lock (SharedGate)
            return _shared ??= new OpenXrOverlayRuntime(resolution, notificationResolution: notificationResolution);
    }

    public OverlayRuntimeStatus Status => _status;

    /// <summary>Whether a picture handed over by <see cref="Submit"/> is still waiting for a frame.</summary>
    public bool HasPendingFrame => _panels[MainPanel].Pending.HasPending;

    public OverlayRuntimeStatus Start() => StartPanel(MainPanel);

    /// <summary>Counts one panel in, and attaches the session if it is not up yet.</summary>
    private OverlayRuntimeStatus StartPanel(int panel)
    {
        lock (_gate)
        {
            if (!_panels[panel].Wanted)
            {
                _panels[panel].Wanted = true;
                _wanted++;
            }

            return StartShared();
        }
    }

    private OverlayRuntimeStatus StartShared()
    {
        if (_status.State is OverlayRuntimeState.Running)
            return _status;

        // A frame thread that closed the session on its own is finishing its teardown; let it.
        _frameThread?.Join();
        _frameThread = null;

        Attachment? attachment = null;
        try
        {
            attachment = Attach();
            _attachment = attachment;
            _stop = false;
            _frameThread = new Thread(() => FrameLoop(attachment))
            {
                IsBackground = true,
                Name = "modbot-openxr-frame",
            };
            _frameThread.Start();

            _log.Information("The overlay is attached to {Runtime} through OpenXR", attachment.RuntimeName);
            return _status = new(OverlayRuntimeState.Running, Detail: $"Attached to {attachment.RuntimeName}.");
        }
        catch (OverlayStartFailure failure)
        {
            attachment?.Dispose();
            _attachment = null;
            _log.Information("OpenXR overlay not started: {Detail}", failure.Detail);
            return _status = new(failure.State, Detail: failure.Detail);
        }
        catch (Exception ex)
        {
            // Every failure is a status. An exception out of Start would take the companion's
            // overlay loop down with it, over a headset most moderators do not have.
            attachment?.Dispose();
            _attachment = null;
            _log.Warning(ex, "OpenXR overlay start failed in an unexpected way");
            return _status = new(OverlayRuntimeState.Refused, Detail: $"OpenXR failed to start: {ex.Message}");
        }
    }

    /// <summary>The frame thread handles the runtime's events itself; here a finished thread is just let go.</summary>
    public void Poll()
    {
        if (_frameThread is { IsAlive: false })
        {
            _frameThread.Join();
            _frameThread = null;
        }
    }

    /// <summary>
    /// Keeps the surface's pixels for the frame thread. Kept even before <see cref="Start"/>, so
    /// the first frame after attaching shows the last thing drawn rather than nothing.
    /// </summary>
    public bool Submit(IOverlaySurface surface) => SubmitTo(MainPanel, surface);

    private bool SubmitTo(int panel, IOverlaySurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        var state = _panels[panel];
        var pixels = surface.Pixels.Span;
        if (pixels.Length != state.Scratch.Length)
            return false;

        state.Pending.Offer(pixels);
        return true;
    }

    public void Show() => _panels[MainPanel].Visible = true;

    public void Hide() => _panels[MainPanel].Visible = false;

    /// <summary>What the frame thread last read, synced once a frame; none before the first frame or after the session is gone.</summary>
    public OverlayTracking ReadTracking() => _tracking.Read();

    /// <summary>
    /// Stored for the frame thread, which picks the space (VIEW for the head, a hand's aim space,
    /// LOCAL for the world), the size, the opacity and the curve on its next frame. A hand anchor
    /// before the action set exists is shown on the head meanwhile.
    /// </summary>
    public void Place(OverlayPlacement placement) => PlacePanel(MainPanel, placement);

    private void PlacePanel(int panel, OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var state = _panels[panel];
        var clamped = placement.Clamped();
        if (clamped.Anchor != state.Placement.Anchor)
            _log.Debug("Panel {Panel} is now anchored to the {Anchor}", panel, clamped.Anchor);

        state.Placement = clamped;
    }

    public void Dispose() => ReleasePanel(MainPanel);

    /// <summary>
    /// Counts one panel out. The session is let go when the last panel does, so switching the
    /// main overlay off leaves the notification overlay attached, and the other way round.
    /// </summary>
    private void ReleasePanel(int panel)
    {
        lock (_gate)
        {
            if (_panels[panel].Wanted)
            {
                _panels[panel].Wanted = false;
                _wanted--;
            }

            if (_wanted > 0)
                return;

            _stop = true;
            _frameThread?.Join(TimeSpan.FromSeconds(5));
            _frameThread = null;

            var attachment = _attachment;
            _attachment = null;
            attachment?.Dispose();
            _tracking.Clear();

            _status = new(OverlayRuntimeState.NotStarted, Detail: "The overlay has been let go.");
        }
    }

    // ---- Start-up, step by step (overlay-on-OpenXR spec, 3.2) ----

    private unsafe Attachment Attach()
    {
        var xr = LoadOpenXr();
        var available = InstanceExtensions(xr);
        _log.Debug("OpenXR loader found, {Count} instance extensions", available.Count);

        if (!available.Contains(OverlayExtension))
        {
            var name = RuntimeNameWithoutExtensions(xr);
            throw new OverlayStartFailure(
                OverlayRuntimeState.Refused,
                name is null ? "This OpenXR runtime has no overlay extension." : $"{name} has no overlay extension.");
        }

        if (!available.Contains(VulkanExtension))
        {
            var name = RuntimeNameWithoutExtensions(xr);
            throw new OverlayStartFailure(
                OverlayRuntimeState.Refused,
                name is null ? "This OpenXR runtime does not draw through Vulkan." : $"{name} does not draw through Vulkan.");
        }

        // The two optional ones are asked for only when listed: asking for an extension a runtime
        // lacks fails xrCreateInstance outright, over a fade and a curve the panel can do without.
        var extensions = new List<string> { OverlayExtension, VulkanExtension };
        var attachment = new Attachment(xr)
        {
            CanFade = available.Contains(ColorScaleExtension),
            CanCurve = available.Contains(CylinderExtension),
        };
        if (attachment.CanFade)
            extensions.Add(ColorScaleExtension);
        if (attachment.CanCurve)
            extensions.Add(CylinderExtension);

        try
        {
            attachment.Instance = CreateInstance(xr, [.. extensions]);
            attachment.RuntimeName = RuntimeName(xr, attachment.Instance, out var version);
            _log.Information("OpenXR instance created on {Runtime} {Version} with {Extensions}",
                attachment.RuntimeName, version, extensions);
            _log.Debug("Opacity {Fade} and curve {Curve} on {Runtime}",
                attachment.CanFade ? "supported" : "not supported (panel drawn solid)",
                attachment.CanCurve ? "supported" : "not supported (panel drawn flat)",
                attachment.RuntimeName);

            attachment.SystemId = GetSystem(xr, attachment.Instance, attachment.RuntimeName);
            _log.Debug("OpenXR system {SystemId} is a head-mounted display", attachment.SystemId);

            CreateVulkan(attachment);
            _log.Information("Vulkan instance and device created through {Runtime}, queue family {Family}",
                attachment.RuntimeName, attachment.QueueFamily);

            CreateSession(attachment);
            _log.Information("OpenXR overlay session created on {Runtime}", attachment.RuntimeName);

            attachment.ViewSpace = CreateSpace(attachment, ReferenceSpaceType.View);
            attachment.LocalSpace = CreateSpace(attachment, ReferenceSpaceType.Local);
            _log.Debug("VIEW and LOCAL reference spaces created");

            // Before the session is begun, as the actions need. A runtime that refuses them still
            // gets the panel; it just cannot be pointed at or held there.
            try
            {
                attachment.Input = OpenXrInput.Create(xr, attachment.Instance, attachment.Session, attachment.RuntimeName, _log);
                _log.Information("Controller input set up on {Runtime}: action set {ActionSet}, {Count} controller profiles",
                    attachment.RuntimeName, ControllerBindings.ActionSet, ControllerBindings.Profiles.Count);
            }
            catch (OverlayStartFailure failure)
            {
                _log.Warning("Controller input is not available on {Runtime}: {Detail} The panel is shown but cannot be held.",
                    attachment.RuntimeName, failure.Detail);
            }

            attachment.BlendMode = ChooseBlendMode(attachment);
            _log.Debug("Environment blend mode {Mode}", attachment.BlendMode);

            // One swapchain and one staging buffer per panel, each at that panel's own size.
            for (var index = 0; index < PanelCount; index++)
            {
                var gpu = attachment.Panels[index];
                var size = _panels[index].Resolution;

                CreateSwapchain(attachment, gpu, size);

                gpu.Uploader = VulkanUploader.Create(
                    attachment.Vk, attachment.PhysicalDevice, attachment.Device, attachment.Queue, attachment.QueueFamily,
                    (uint)size, (uint)size, attachment.Format);

                _log.Debug("Panel {Panel}: swapchain of {Count} {Format} images at {Size}x{Size}",
                    index, gpu.Images.Length, attachment.Format.Name, size);
            }

            return attachment;
        }
        catch
        {
            attachment.Dispose();
            throw;
        }
    }

    private XR LoadOpenXr()
    {
        try
        {
            return _xrApi ??= XR.GetApi();
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _log.Debug("OpenXR loader not found: {Message}", ex.Message);
            throw new OverlayStartFailure(OverlayRuntimeState.NoRuntime, "The OpenXR loader is not installed.");
        }
    }

    private unsafe HashSet<string> InstanceExtensions(XR xr)
    {
        uint count = 0;
        Result result;
        try
        {
            result = xr.EnumerateInstanceExtensionProperties((byte*)null, 0, &count, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _log.Debug("OpenXR loader not usable: {Message}", ex.Message);
            throw new OverlayStartFailure(OverlayRuntimeState.NoRuntime, "The OpenXR loader is not installed.");
        }

        if (result < 0)
            throw NotRunning(result, "xrEnumerateInstanceExtensionProperties");

        var properties = new ExtensionProperties[count];
        for (var i = 0; i < properties.Length; i++)
            properties[i].Type = StructureType.ExtensionProperties;

        if (count > 0)
        {
            fixed (ExtensionProperties* p = properties)
                result = xr.EnumerateInstanceExtensionProperties((byte*)null, count, &count, p);

            if (result < 0)
                throw NotRunning(result, "xrEnumerateInstanceExtensionProperties");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var property = properties[i];
            names.Add(FixedString(property.ExtensionName));
        }

        return names;
    }

    /// <summary>Names the runtime for a refusal, through a bare instance; null when even that fails.</summary>
    private unsafe string? RuntimeNameWithoutExtensions(XR xr)
    {
        try
        {
            var instance = CreateInstance(xr, []);
            try
            {
                return RuntimeName(xr, instance, out _);
            }
            finally
            {
                xr.DestroyInstance(instance);
            }
        }
        catch (OverlayStartFailure)
        {
            return null;
        }
    }

    private unsafe Instance CreateInstance(XR xr, string[] extensions)
    {
        var info = new InstanceCreateInfo
        {
            Type = StructureType.InstanceCreateInfo,
            EnabledExtensionCount = (uint)extensions.Length,
        };
        info.ApplicationInfo.ApiVersion = OpenXrApiVersion;
        info.ApplicationInfo.ApplicationVersion = 1;
        info.ApplicationInfo.EngineVersion = 1;
        WriteFixedString(info.ApplicationInfo.ApplicationName, (int)XR.MaxApplicationNameSize, "Modbot");
        WriteFixedString(info.ApplicationInfo.EngineName, (int)XR.MaxEngineNameSize, "Modbot");

        var names = extensions.Length == 0 ? 0 : SilkMarshal.StringArrayToPtr(extensions, NativeStringEncoding.UTF8);
        try
        {
            info.EnabledExtensionNames = (byte**)names;
            Instance instance;
            var result = xr.CreateInstance(&info, &instance);
            switch (result)
            {
                case Result.Success:
                    return instance;
                case Result.ErrorRuntimeUnavailable or Result.ErrorRuntimeFailure or Result.ErrorInitializationFailed:
                    throw NotRunning(result, "xrCreateInstance");
                case Result.ErrorExtensionNotPresent:
                    throw new OverlayStartFailure(OverlayRuntimeState.Refused, "This OpenXR runtime has no overlay extension.");
                default:
                    throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"OpenXR answered {result} to xrCreateInstance.");
            }
        }
        finally
        {
            if (names != 0)
                SilkMarshal.Free(names);
        }
    }

    private static unsafe string RuntimeName(XR xr, Instance instance, out string version)
    {
        var properties = new InstanceProperties { Type = StructureType.InstanceProperties };
        var result = xr.GetInstanceProperties(instance, &properties);
        if (result < 0)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"OpenXR answered {result} to xrGetInstanceProperties.");

        var v = properties.RuntimeVersion;
        version = $"{(v >> 48) & 0xffff}.{(v >> 32) & 0xffff}.{v & 0xffffffff}";
        var name = FixedString(properties.RuntimeName);
        return string.IsNullOrWhiteSpace(name) ? "the OpenXR runtime" : name;
    }

    private static unsafe ulong GetSystem(XR xr, Instance instance, string runtimeName)
    {
        var info = new SystemGetInfo { Type = StructureType.SystemGetInfo, FormFactor = FormFactor.HeadMountedDisplay };
        ulong systemId;
        var result = xr.GetSystem(instance, &info, &systemId);
        return result switch
        {
            Result.Success => systemId,
            Result.ErrorFormFactorUnavailable => throw new OverlayStartFailure(
                OverlayRuntimeState.NotStarted, $"{runtimeName} has no headset connected."),
            _ => throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"{runtimeName} answered {result} to xrGetSystem."),
        };
    }

    private unsafe void CreateVulkan(Attachment a)
    {
        if (!a.Xr.TryGetInstanceExtension<KhrVulkanEnable2>(null!, a.Instance, out var vulkanExt))
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"{a.RuntimeName} does not draw through Vulkan.");

        a.VulkanExt = vulkanExt;

        // Asked before the session is made, as the extension requires; the answer also says which
        // Vulkan version the runtime needs from the instance it is about to create.
        var requirements = new GraphicsRequirementsVulkanKHR { Type = StructureType.GraphicsRequirementsVulkanKhr };
        CheckXr(vulkanExt.GetVulkanGraphicsRequirements2(a.Instance, a.SystemId, &requirements), a.RuntimeName, "xrGetVulkanGraphicsRequirements2KHR");
        var minMajor = (uint)((requirements.MinApiVersionSupported >> 48) & 0xffff);
        var minMinor = (uint)((requirements.MinApiVersionSupported >> 32) & 0xffff);
        var apiVersion = Vulkan.Vk.MakeVersion(Math.Max(minMajor, 1), minMajor > 1 ? minMinor : Math.Max(minMinor, 1), 0);
        _log.Debug("Runtime wants Vulkan {Major}.{Minor} or later", minMajor, minMinor);

        Vulkan.Vk vk;
        try
        {
            vk = _vkApi ??= Vulkan.Vk.GetApi();
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _log.Debug("Vulkan not found: {Message}", ex.Message);
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, "Vulkan is not installed on this PC.");
        }

        a.Vk = vk;

        var getInstanceProcAddr = vk.Context.GetProcAddress("vkGetInstanceProcAddr");
        if (getInstanceProcAddr == 0)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, "Vulkan is not installed on this PC.");

        var pfn = new PfnVoidFunction((delegate* unmanaged[Cdecl]<void>)getInstanceProcAddr);

        var appName = SilkMarshal.StringToPtr("Modbot", NativeStringEncoding.UTF8);
        try
        {
            var appInfo = new Vulkan.ApplicationInfo
            {
                SType = Vulkan.StructureType.ApplicationInfo,
                PApplicationName = (byte*)appName,
                ApplicationVersion = 1,
                PEngineName = (byte*)appName,
                EngineVersion = 1,
                ApiVersion = apiVersion,
            };
            var instanceInfo = new Vulkan.InstanceCreateInfo
            {
                SType = Vulkan.StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
            };
            var xrInstanceInfo = new VulkanInstanceCreateInfoKHR
            {
                Type = StructureType.VulkanInstanceCreateInfoKhr,
                SystemId = a.SystemId,
                PfnGetInstanceProcAddr = pfn,
                VulkanCreateInfo = &instanceInfo,
            };

            VkHandle vkInstance;
            uint vkResult;
            CheckXr(vulkanExt.CreateVulkanInstance(a.Instance, &xrInstanceInfo, &vkInstance, &vkResult), a.RuntimeName, "xrCreateVulkanInstanceKHR");
            CheckVk(vkResult, "vkCreateInstance");
            a.VkInstance = new Vulkan.Instance(vkInstance.Handle);
            vk.CurrentInstance = a.VkInstance;
        }
        finally
        {
            SilkMarshal.Free(appName);
        }

        var deviceGetInfo = new VulkanGraphicsDeviceGetInfoKHR
        {
            Type = StructureType.VulkanGraphicsDeviceGetInfoKhr,
            SystemId = a.SystemId,
            VulkanInstance = new VkHandle(a.VkInstance.Handle),
        };
        VkHandle physical;
        CheckXr(vulkanExt.GetVulkanGraphicsDevice2(a.Instance, &deviceGetInfo, &physical), a.RuntimeName, "xrGetVulkanGraphicsDevice2KHR");
        a.PhysicalDevice = new Vulkan.PhysicalDevice(physical.Handle);

        uint familyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(a.PhysicalDevice, &familyCount, null);
        var families = new Vulkan.QueueFamilyProperties[familyCount];
        fixed (Vulkan.QueueFamilyProperties* f = families)
            vk.GetPhysicalDeviceQueueFamilyProperties(a.PhysicalDevice, &familyCount, f);

        var family = -1;
        for (var i = 0; i < familyCount; i++)
        {
            if ((families[i].QueueFlags & Vulkan.QueueFlags.GraphicsBit) != 0)
            {
                family = i;
                break;
            }
        }

        if (family < 0)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, "The graphics device has no queue that can draw.");

        a.QueueFamily = (uint)family;

        var priority = 1f;
        var queueInfo = new Vulkan.DeviceQueueCreateInfo
        {
            SType = Vulkan.StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = a.QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        var deviceInfo = new Vulkan.DeviceCreateInfo
        {
            SType = Vulkan.StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
        };
        var xrDeviceInfo = new VulkanDeviceCreateInfoKHR
        {
            Type = StructureType.VulkanDeviceCreateInfoKhr,
            SystemId = a.SystemId,
            PfnGetInstanceProcAddr = pfn,
            VulkanPhysicalDevice = physical,
            VulkanCreateInfo = &deviceInfo,
        };

        VkHandle device;
        uint deviceResult;
        CheckXr(vulkanExt.CreateVulkanDevice(a.Instance, &xrDeviceInfo, &device, &deviceResult), a.RuntimeName, "xrCreateVulkanDeviceKHR");
        CheckVk(deviceResult, "vkCreateDevice");
        a.Device = new Vulkan.Device(device.Handle);
        vk.CurrentDevice = a.Device;

        Vulkan.Queue queue;
        vk.GetDeviceQueue(a.Device, a.QueueFamily, 0, &queue);
        a.Queue = queue;
    }

    private unsafe void CreateSession(Attachment a)
    {
        // The structure types are the XR_KHR_vulkan_enable ones on purpose: the header defines the
        // VULKAN2 names as aliases of them, and Silk.NET's *Vulkan2Khr members carry the wrong
        // numbers (they collide with VulkanInstanceCreateInfoKhr).
        var binding = new GraphicsBindingVulkanKHR
        {
            Type = StructureType.GraphicsBindingVulkanKhr,
            Instance = new VkHandle(a.VkInstance.Handle),
            PhysicalDevice = new VkHandle(a.PhysicalDevice.Handle),
            Device = new VkHandle(a.Device.Handle),
            QueueFamilyIndex = a.QueueFamily,
            QueueIndex = 0,
        };

        // createFlags must be 0 and sessionLayersPlacement 0: the extension defines no flags, and
        // placement 0 is "over the main session", which is the one thing this session is for.
        var overlay = new SessionCreateInfoOverlayEXTX
        {
            Type = StructureType.SessionCreateInfoOverlayExtx,
            CreateFlags = 0,
            SessionLayersPlacement = 0,
            Next = &binding,
        };

        var info = new SessionCreateInfo
        {
            Type = StructureType.SessionCreateInfo,
            SystemId = a.SystemId,
            Next = &overlay,
        };

        Session session;
        var result = a.Xr.CreateSession(a.Instance, &info, &session);
        if (result < 0)
        {
            throw new OverlayStartFailure(
                OverlayRuntimeState.Refused,
                $"{a.RuntimeName} answered {result} to xrCreateSession for an overlay session.");
        }

        a.Session = session;
    }

    private static unsafe Space CreateSpace(Attachment a, ReferenceSpaceType type)
    {
        var info = new ReferenceSpaceCreateInfo
        {
            Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = type,
            PoseInReferenceSpace = Identity,
        };

        Space space;
        CheckXr(a.Xr.CreateReferenceSpace(a.Session, &info, &space), a.RuntimeName, $"xrCreateReferenceSpace({type})");
        return space;
    }

    private unsafe void CreateSwapchain(Attachment a, Attachment.PanelGpu gpu, int size)
    {
        uint count = 0;
        CheckXr(a.Xr.EnumerateSwapchainFormats(a.Session, 0, &count, null), a.RuntimeName, "xrEnumerateSwapchainFormats");
        var formats = new long[count];
        if (count > 0)
        {
            fixed (long* f = formats)
                CheckXr(a.Xr.EnumerateSwapchainFormats(a.Session, count, &count, f), a.RuntimeName, "xrEnumerateSwapchainFormats");
        }

        a.Format = SwapchainFormat.Choose(formats)
            ?? throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"{a.RuntimeName} offers neither BGRA nor RGBA swapchain images.");

        var info = new SwapchainCreateInfo
        {
            Type = StructureType.SwapchainCreateInfo,
            UsageFlags = SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.TransferDstBit,
            Format = a.Format.VulkanFormat,
            SampleCount = 1,
            Width = (uint)size,
            Height = (uint)size,
            FaceCount = 1,
            ArraySize = 1,
            MipCount = 1,
        };

        Swapchain swapchain;
        CheckXr(a.Xr.CreateSwapchain(a.Session, &info, &swapchain), a.RuntimeName, "xrCreateSwapchain");
        gpu.Swapchain = swapchain;

        uint imageCount = 0;
        CheckXr(a.Xr.EnumerateSwapchainImages(gpu.Swapchain, 0, &imageCount, null), a.RuntimeName, "xrEnumerateSwapchainImages");
        var images = new SwapchainImageVulkanKHR[imageCount];
        for (var i = 0; i < images.Length; i++)
            images[i].Type = StructureType.SwapchainImageVulkanKhr;

        fixed (SwapchainImageVulkanKHR* p = images)
            CheckXr(a.Xr.EnumerateSwapchainImages(gpu.Swapchain, imageCount, &imageCount, (SwapchainImageBaseHeader*)p), a.RuntimeName, "xrEnumerateSwapchainImages");

        gpu.Images = new Vulkan.Image[imageCount];
        for (var i = 0; i < imageCount; i++)
            gpu.Images[i] = new Vulkan.Image(images[i].Image);

        if (gpu.Images.Length == 0)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"{a.RuntimeName} made a swapchain with no images.");
    }

    private static unsafe EnvironmentBlendMode ChooseBlendMode(Attachment a)
    {
        uint count = 0;
        CheckXr(a.Xr.EnumerateEnvironmentBlendModes(a.Instance, a.SystemId, ViewConfigurationType.PrimaryStereo, 0, &count, null),
            a.RuntimeName, "xrEnumerateEnvironmentBlendModes");
        if (count == 0)
            return EnvironmentBlendMode.Opaque;

        var modes = new EnvironmentBlendMode[count];
        fixed (EnvironmentBlendMode* m = modes)
        {
            CheckXr(a.Xr.EnumerateEnvironmentBlendModes(a.Instance, a.SystemId, ViewConfigurationType.PrimaryStereo, count, &count, m),
                a.RuntimeName, "xrEnumerateEnvironmentBlendModes");
        }

        // Alpha blend first, wherever the headset offers it: an overlay drawn opaque over a
        // passthrough headset blacks the passthrough out. Then additive, then whatever is first.
        foreach (var wanted in new[] { EnvironmentBlendMode.AlphaBlend, EnvironmentBlendMode.Additive })
        {
            if (Array.IndexOf(modes, wanted) >= 0)
                return wanted;
        }

        return modes[0];
    }

    // ---- The frame thread (overlay-on-OpenXR spec, 3.3) ----

    private unsafe void FrameLoop(Attachment a)
    {
        var closed = $"{a.RuntimeName} closed.";
        try
        {
            while (!_stop)
            {
                if (!PumpEvents(a, out var reason))
                {
                    closed = reason;
                    break;
                }

                if (!a.SessionRunning)
                {
                    Thread.Sleep(100);
                    continue;
                }

                Frame(a);
            }
        }
        catch (OverlayStartFailure failure)
        {
            _log.Warning("The OpenXR overlay session ended: {Detail}", failure.Detail);
            closed = failure.Detail;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "The OpenXR frame thread failed");
            closed = $"{a.RuntimeName} closed ({ex.GetType().Name}).";
        }
        finally
        {
            _tracking.Clear();
            if (!_stop)
            {
                // Closed by the runtime, not by Dispose: let go here, and say so. The companion's
                // ten-second look attaches again when the runtime is back.
                _attachment = null;
                a.Dispose();
                _status = new(OverlayRuntimeState.NotStarted, Detail: closed);
                _log.Information("The OpenXR overlay has let go: {Detail}", closed);
            }
        }
    }

    /// <summary>Handles what the runtime has said. False when it said to leave.</summary>
    private unsafe bool PumpEvents(Attachment a, out string reason)
    {
        reason = "";
        var buffer = new EventDataBuffer();

        // A few at a time, never "until empty": a runtime that never stops answering must not
        // hold the frame thread away from its frame.
        for (var i = 0; i < 32; i++)
        {
            buffer.Type = StructureType.EventDataBuffer;
            buffer.Next = null;

            var result = a.Xr.PollEvent(a.Instance, &buffer);
            if (result == Result.EventUnavailable)
                return true;

            if (result < 0)
                throw new OverlayStartFailure(OverlayRuntimeState.NotStarted, $"{a.RuntimeName} answered {result} to xrPollEvent.");

            switch (buffer.Type)
            {
                case StructureType.EventDataSessionStateChanged:
                {
                    var state = ((EventDataSessionStateChanged*)&buffer)->State;
                    _log.Debug("OpenXR session state {State}", state);
                    switch (state)
                    {
                        case SessionState.Ready:
                        {
                            var begin = new SessionBeginInfo
                            {
                                Type = StructureType.SessionBeginInfo,
                                PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo,
                            };
                            CheckXr(a.Xr.BeginSession(a.Session, &begin), a.RuntimeName, "xrBeginSession");
                            a.SessionRunning = true;
                            _log.Information("OpenXR overlay session begun on {Runtime}", a.RuntimeName);
                            break;
                        }
                        case SessionState.Stopping:
                            CheckXr(a.Xr.EndSession(a.Session), a.RuntimeName, "xrEndSession");
                            a.SessionRunning = false;
                            _log.Information("OpenXR overlay session stopped by {Runtime}", a.RuntimeName);
                            break;
                        case SessionState.Exiting or SessionState.LossPending:
                            reason = $"{a.RuntimeName} closed.";
                            return false;
                    }

                    break;
                }
                case StructureType.EventDataInstanceLossPending:
                    reason = $"{a.RuntimeName} closed.";
                    return false;
                case StructureType.EventDataMainSessionVisibilityChangedExtx:
                {
                    // Logged and nothing more. The panel is for the moderator inside VRChat, which
                    // is the main session; the quad stays up either way.
                    var visible = ((EventDataMainSessionVisibilityChangedEXTX*)&buffer)->Visible != 0;
                    _log.Debug("The main OpenXR session is {Visibility}", visible ? "visible" : "hidden");
                    break;
                }
                case StructureType.EventDataInteractionProfileChanged:
                    // Which controller the runtime settled on, from the bindings offered. The one
                    // line that says whether a held panel is possible on this headset.
                    a.Input?.LogCurrentProfiles();
                    break;
                case StructureType.EventDataEventsLost:
                    _log.Debug("OpenXR dropped events");
                    break;
            }
        }

        return true;
    }

    private unsafe void Frame(Attachment a)
    {
        var waitInfo = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        var frameState = new FrameState { Type = StructureType.FrameState };
        CheckXr(a.Xr.WaitFrame(a.Session, &waitInfo, &frameState), a.RuntimeName, "xrWaitFrame");

        ReadInput(a, frameState.PredictedDisplayTime);

        var beginInfo = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        CheckXr(a.Xr.BeginFrame(a.Session, &beginInfo), a.RuntimeName, "xrBeginFrame");

        // One of each per panel, all on this frame's stack. A panel points at exactly one of its
        // quad and its cylinder, and only the panels with something to show are counted in.
        var quads = stackalloc CompositionLayerQuad[PanelCount];
        var cylinders = stackalloc CompositionLayerCylinderKHR[PanelCount];
        var fades = stackalloc CompositionLayerColorScaleBiasKHR[PanelCount];
        var layers = stackalloc nint[PanelCount];
        var shown = 0u;

        for (var index = 0; index < PanelCount; index++)
        {
            var state = _panels[index];
            var gpu = a.Panels[index];

            // The copy is the only work that depends on content, and it happens only when the
            // compositor drew. Everything else is the same frame resubmitted.
            if (frameState.ShouldRender != 0 && state.Pending.TryTake(state.Scratch))
            {
                gpu.Uploader!.Stage(state.Scratch);

                var acquireInfo = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
                uint image;
                CheckXr(a.Xr.AcquireSwapchainImage(gpu.Swapchain, &acquireInfo, &image), a.RuntimeName, "xrAcquireSwapchainImage");

                var waitImage = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = InfiniteDuration };
                CheckXr(a.Xr.WaitSwapchainImage(gpu.Swapchain, &waitImage), a.RuntimeName, "xrWaitSwapchainImage");

                gpu.Uploader.Upload(gpu.Images[image]);

                var releaseInfo = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
                CheckXr(a.Xr.ReleaseSwapchainImage(gpu.Swapchain, &releaseInfo), a.RuntimeName, "xrReleaseSwapchainImage");

                if (!gpu.Uploaded)
                    _log.Information("First picture uploaded to OpenXR panel {Panel}", index);
                gpu.Uploaded = true;
            }

            if (frameState.ShouldRender == 0 || !state.Wanted || !state.Visible || !gpu.Uploaded)
                continue;

            var placement = state.Placement;
            var panel = Pose.From(placement.Offset);
            var subImage = new SwapchainSubImage
            {
                Swapchain = gpu.Swapchain,
                ImageRect = new Rect2Di(new Offset2Di(0, 0), new Extent2Di(state.Resolution, state.Resolution)),
                ImageArrayIndex = 0,
            };

            // Opacity, when the runtime can. The pixels are premultiplied and the layer blends on
            // their alpha, so every channel is scaled together: scaling alpha alone would leave
            // the colour at full strength over a fainter cut-out, which reads as a glow, not a
            // fade.
            fades[index] = new CompositionLayerColorScaleBiasKHR
            {
                Type = StructureType.CompositionLayerColorScaleBiasKhr,
                ColorScale = new Color4f(placement.Opacity, placement.Opacity, placement.Opacity, placement.Opacity),
                ColorBias = new Color4f(0f, 0f, 0f, 0f),
            };
            var next = a.CanFade ? &fades[index] : null;

            if (a.CanCurve && CurvedPanel.For(placement.Width, placement.Curve) is { } curved)
            {
                cylinders[index] = new CompositionLayerCylinderKHR
                {
                    Type = StructureType.CompositionLayerCylinderKhr,
                    Next = next,
                    LayerFlags = CompositionLayerFlags.BlendTextureSourceAlphaBit,
                    Space = SpaceFor(a, placement.Anchor),
                    EyeVisibility = EyeVisibility.Both,
                    SubImage = subImage,
                    Pose = OpenXrCalls.ToPosef(curved.CentreOf(panel)),
                    Radius = curved.Radius,
                    CentralAngle = curved.CentralAngle,
                    AspectRatio = 1f,
                };
                layers[shown++] = (nint)(&cylinders[index]);
            }
            else
            {
                quads[index] = new CompositionLayerQuad
                {
                    Type = StructureType.CompositionLayerQuad,
                    Next = next,
                    LayerFlags = CompositionLayerFlags.BlendTextureSourceAlphaBit,
                    Space = SpaceFor(a, placement.Anchor),
                    EyeVisibility = EyeVisibility.Both,
                    SubImage = subImage,
                    Pose = OpenXrCalls.ToPosef(panel),
                    Size = new Extent2Df(placement.Width, placement.Width),
                };
                layers[shown++] = (nint)(&quads[index]);
            }
        }

        var endInfo = new FrameEndInfo
        {
            Type = StructureType.FrameEndInfo,
            DisplayTime = frameState.PredictedDisplayTime,
            EnvironmentBlendMode = a.BlendMode,
            LayerCount = shown,
            Layers = shown > 0 ? (CompositionLayerBaseHeader**)layers : null,
        };
        CheckXr(a.Xr.EndFrame(a.Session, &endInfo), a.RuntimeName, "xrEndFrame");
    }

    /// <summary>
    /// The space the placement's anchor names: the head is VIEW, the world is LOCAL, a hand is
    /// that hand's own space — where the controller is, not where it points, so a panel worn on
    /// the wrist sits on the wrist. A hand with no action set behind it is shown on the head
    /// instead.
    /// </summary>
    private static Space SpaceFor(Attachment a, OverlayAnchor anchor)
    {
        var space = anchor switch
        {
            OverlayAnchor.World => a.LocalSpace,
            OverlayAnchor.LeftHand => a.Input?.DeviceSpace(Hand.Left) ?? default,
            OverlayAnchor.RightHand => a.Input?.DeviceSpace(Hand.Right) ?? default,
            _ => a.ViewSpace,
        };

        return space.Handle != 0 ? space : a.ViewSpace;
    }

    /// <summary>
    /// Syncs and reads the controllers for this frame and publishes them. A runtime that fails a
    /// call here loses input for the rest of the attachment, with one warning; the panel stays up.
    /// </summary>
    private void ReadInput(Attachment a, long time)
    {
        if (a.Input is null || a.InputFailed)
            return;

        try
        {
            _tracking.Publish(a.Input.Read(a.ViewSpace, a.LocalSpace, time));
        }
        catch (OverlayStartFailure failure)
        {
            a.InputFailed = true;
            _tracking.Clear();
            _log.Warning("Controller input stopped on {Runtime}: {Detail} The panel is shown but cannot be held.",
                a.RuntimeName, failure.Detail);
        }
    }

    // ---- Small helpers ----

    private static readonly Posef Identity = OpenXrCalls.IdentityPose;

    private static OverlayStartFailure NotRunning(Result result, string call)
        => new(OverlayRuntimeState.NotStarted, $"No OpenXR runtime is running ({result} from {call}).");

    private static void CheckXr(Result result, string runtimeName, string call) => OpenXrCalls.Check(result, runtimeName, call);

    private static void CheckVk(uint result, string call)
    {
        if (result != 0)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"Vulkan answered {(Vulkan.Result)(int)result} to {call}.");
    }

    private static unsafe string FixedString(byte* bytes) => OpenXrCalls.FixedString(bytes);

    private static unsafe void WriteFixedString(byte* destination, int capacity, string value)
        => OpenXrCalls.WriteFixedString(destination, capacity, value);

    /// <summary>Everything native that one attachment owns, torn down in the reverse order it was made.</summary>
    private sealed unsafe class Attachment(XR xr) : IDisposable
    {
        public XR Xr { get; } = xr;

        public Instance Instance;
        public string RuntimeName = "the OpenXR runtime";
        public ulong SystemId;
        public KhrVulkanEnable2? VulkanExt;
        public Vulkan.Vk Vk = null!;
        public Vulkan.Instance VkInstance;
        public Vulkan.PhysicalDevice PhysicalDevice;
        public Vulkan.Device Device;
        public Vulkan.Queue Queue;
        public uint QueueFamily;
        public Session Session;
        public Space ViewSpace;
        public Space LocalSpace;
        public SwapchainFormat Format = SwapchainFormat.Bgra;
        public EnvironmentBlendMode BlendMode = EnvironmentBlendMode.Opaque;
        public OpenXrInput? Input;
        public bool InputFailed;
        public bool CanFade;
        public bool CanCurve;
        public bool SessionRunning;

        /// <summary>What each panel owns on the graphics card, in the same order as the panels.</summary>
        public PanelGpu[] Panels { get; } = [new PanelGpu(), new PanelGpu()];

        /// <summary>One panel's swapchain, its images and the staging buffer that fills them.</summary>
        public sealed class PanelGpu
        {
            public Swapchain Swapchain;
            public Vulkan.Image[] Images = [];
            public VulkanUploader? Uploader;
            public bool Uploaded;
        }

        public void Dispose()
        {
            foreach (var panel in Panels)
            {
                panel.Uploader?.Dispose();
                panel.Uploader = null;
            }

            // The aim spaces belong to the session and the set to the instance; both go before
            // either parent does.
            Input?.Dispose();
            Input = null;

            foreach (var panel in Panels)
            {
                if (panel.Swapchain.Handle != 0)
                    Xr.DestroySwapchain(panel.Swapchain);
                panel.Swapchain = default;
                panel.Images = [];
                panel.Uploaded = false;
            }

            if (LocalSpace.Handle != 0)
                Xr.DestroySpace(LocalSpace);
            if (ViewSpace.Handle != 0)
                Xr.DestroySpace(ViewSpace);
            if (Session.Handle != 0)
                Xr.DestroySession(Session);
            LocalSpace = default;
            ViewSpace = default;
            Session = default;
            SessionRunning = false;

            if (Device.Handle != 0)
                Vk.DestroyDevice(Device, null);
            if (VkInstance.Handle != 0)
                Vk.DestroyInstance(VkInstance, null);
            Device = default;
            VkInstance = default;

            VulkanExt?.Dispose();
            VulkanExt = null;

            if (Instance.Handle != 0)
                Xr.DestroyInstance(Instance);
            Instance = default;
        }
    }

    /// <summary>
    /// One panel as the runtime keeps it between attachments: the picture waiting to go up, where
    /// the panel is, and whether anybody wants it drawn.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Attachment.PanelGpu"/> on purpose. A picture offered before the
    /// runtime attached, and a placement set from the settings page with no headset on, both have
    /// to survive an attachment coming and going.
    /// </remarks>
    private sealed class PanelState(int resolution)
    {
        public readonly PendingFrame Pending = new();

        /// <summary>This panel's texture, square. The two panels need not be the same size.</summary>
        public readonly int Resolution = resolution;

        public readonly byte[] Scratch = new byte[resolution * resolution * 4];

        /// <summary>Whether a runtime has started this panel and not yet let it go.</summary>
        public volatile bool Wanted;

        /// <summary>Show and Hide. A hidden panel is still attached; it is simply not submitted.</summary>
        public volatile bool Visible = true;

        // Read by the frame thread, replaced whole by Place: a reference swap is atomic.
        public OverlayPlacement Placement = OverlayPlacement.Default;
    }

    /// <summary>
    /// A second panel on the same session, as an ordinary runtime.
    /// </summary>
    /// <remarks>
    /// Starting it attaches the session if it is not up; disposing it lets the session go only if
    /// it was the last panel wanting it. Everything else is that panel's own.
    /// </remarks>
    private sealed class PanelRuntime(OpenXrOverlayRuntime owner, int panel) : IOverlayRuntime
    {
        public OverlayRuntimeStatus Status => owner._status;

        public OverlayRuntimeStatus Start() => owner.StartPanel(panel);

        public void Poll() => owner.Poll();

        public bool Submit(IOverlaySurface surface) => owner.SubmitTo(panel, surface);

        public void Show() => owner._panels[panel].Visible = true;

        public void Hide() => owner._panels[panel].Visible = false;

        public OverlayTracking ReadTracking() => owner.ReadTracking();

        public void Place(OverlayPlacement placement) => owner.PlacePanel(panel, placement);

        public void Dispose() => owner.ReleasePanel(panel);
    }
}
