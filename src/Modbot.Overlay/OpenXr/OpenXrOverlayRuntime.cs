using System.Runtime.InteropServices;
using System.Text;
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
/// (<c>XR_EXTX_overlay</c>), whose one quad layer the runtime draws over VRChat's own frames.
/// The quad sits where the OpenVR panel does — a little below and to the right of where the
/// moderator is looking, in the head-locked <c>VIEW</c> space — and shows the same picture. That
/// is the entire interaction: no tracking data is read, no other session is inspected, and
/// nothing is sent anywhere.</para>
/// <para><strong>Why OpenXR at all.</strong> On Linux, WiVRn and Monado users run OpenVR games
/// through xrizer, which by design takes games only and refuses overlays. The OpenXR overlay
/// extension is how desktop overlays work on those runtimes, so it is how this one does. On a
/// machine with SteamVR the OpenVR runtime is tried first and this one is never reached, because
/// SteamVR's own OpenXR has no overlay extension (overlay-on-OpenXR spec, 3.1).</para>
/// <para><strong>A layer is submitted every frame</strong>, unlike OpenVR's set-once texture, so
/// the runtime owns a frame thread. The picture is copied into a swapchain image only when the
/// compositor drew something new; every other frame resubmits the image already there, which
/// costs one <c>xrEndFrame</c> and nothing else.</para>
/// <para><strong>Nothing is shipped and nothing is launched.</strong> The OpenXR loader and
/// Vulkan come from the machine; a missing loader is a state, not a crash, and starting never
/// starts a runtime. Absence is reported, and the companion looks again in ten seconds.</para>
/// </remarks>
public sealed class OpenXrOverlayRuntime : IOverlayRuntime
{
    public const string OverlayExtension = "XR_EXTX_overlay";

    public const string VulkanExtension = "XR_KHR_vulkan_enable2";

    /// <summary><c>XR_MAKE_VERSION(1, 0, 0)</c>: the overlay extension predates OpenXR 1.1, and every runtime speaks 1.0.</summary>
    private const ulong OpenXrApiVersion = 1UL << 48;

    /// <summary><c>XR_INFINITE_DURATION</c>.</summary>
    private const long InfiniteDuration = 0x7fffffffffffffff;

    private static XR? _xrApi;
    private static Vulkan.Vk? _vkApi;

    private readonly int _resolution;
    private readonly float _widthInMetres;
    private readonly ILogger _log;
    private readonly PendingFrame _pending = new();
    private readonly byte[] _scratch;

    private volatile Attachment? _attachment;
    private volatile OverlayRuntimeStatus _status = new(OverlayRuntimeState.NotStarted, Detail: "OpenXR has not been looked for yet.");
    private Thread? _frameThread;
    private volatile bool _stop;
    private volatile bool _visible = true;

    // Read by the frame thread, replaced whole by Place: a reference swap is atomic.
    private OverlayPlacement _placement = OverlayPlacement.Default;

    public OpenXrOverlayRuntime(
        int resolution = OverlayHost.DefaultResolution,
        float widthInMetres = OpenVrOverlayRuntime.DefaultWidthInMetres,
        ILogger? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolution, 1);

        _resolution = resolution;
        _widthInMetres = widthInMetres;
        _log = log ?? Log.ForContext<OpenXrOverlayRuntime>();
        _scratch = new byte[resolution * resolution * 4];
    }

    public OverlayRuntimeStatus Status => _status;

    /// <summary>Whether a picture handed over by <see cref="Submit"/> is still waiting for a frame.</summary>
    public bool HasPendingFrame => _pending.HasPending;

    public OverlayRuntimeStatus Start()
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
    public bool Submit(IOverlaySurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        var pixels = surface.Pixels.Span;
        if (pixels.Length != _scratch.Length)
            return false;

        _pending.Offer(pixels);
        return true;
    }

    public void Show() => _visible = true;

    public void Hide() => _visible = false;

    /// <summary>Not read yet: controller input on OpenXR comes with its action set (design §4.1).</summary>
    public OverlayTracking ReadTracking() => OverlayTracking.None;

    /// <summary>
    /// Head and world anchors are spaces the session already has; a hand anchor waits on the
    /// action set and is shown on the head meanwhile. Opacity and curve are not applied here yet.
    /// </summary>
    public void Place(OverlayPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _placement = placement.Clamped();
    }

    public void Dispose()
    {
        _stop = true;
        _frameThread?.Join(TimeSpan.FromSeconds(5));
        _frameThread = null;

        var attachment = _attachment;
        _attachment = null;
        attachment?.Dispose();

        _status = new(OverlayRuntimeState.NotStarted, Detail: "The overlay has been let go.");
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

        var attachment = new Attachment(xr);
        try
        {
            attachment.Instance = CreateInstance(xr, [OverlayExtension, VulkanExtension]);
            attachment.RuntimeName = RuntimeName(xr, attachment.Instance, out var version);
            _log.Information("OpenXR instance created on {Runtime} {Version} with {Overlay} and {Vulkan}",
                attachment.RuntimeName, version, OverlayExtension, VulkanExtension);

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

            CreateSwapchain(attachment);
            _log.Information("Swapchain of {Count} {Format} images at {Size}x{Size} created",
                attachment.Images.Length, attachment.Format.Name, _resolution);

            attachment.BlendMode = FirstBlendMode(attachment);
            _log.Debug("Environment blend mode {Mode}", attachment.BlendMode);

            attachment.Uploader = VulkanUploader.Create(
                attachment.Vk, attachment.PhysicalDevice, attachment.Device, attachment.Queue, attachment.QueueFamily,
                (uint)_resolution, (uint)_resolution, attachment.Format);
            _log.Debug("Staging buffer of {Bytes} bytes ready", attachment.Uploader.FrameLength);

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

    private unsafe void CreateSwapchain(Attachment a)
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
            Width = (uint)_resolution,
            Height = (uint)_resolution,
            FaceCount = 1,
            ArraySize = 1,
            MipCount = 1,
        };

        Swapchain swapchain;
        CheckXr(a.Xr.CreateSwapchain(a.Session, &info, &swapchain), a.RuntimeName, "xrCreateSwapchain");
        a.Swapchain = swapchain;

        uint imageCount = 0;
        CheckXr(a.Xr.EnumerateSwapchainImages(a.Swapchain, 0, &imageCount, null), a.RuntimeName, "xrEnumerateSwapchainImages");
        var images = new SwapchainImageVulkanKHR[imageCount];
        for (var i = 0; i < images.Length; i++)
            images[i].Type = StructureType.SwapchainImageVulkanKhr;

        fixed (SwapchainImageVulkanKHR* p = images)
            CheckXr(a.Xr.EnumerateSwapchainImages(a.Swapchain, imageCount, &imageCount, (SwapchainImageBaseHeader*)p), a.RuntimeName, "xrEnumerateSwapchainImages");

        a.Images = new Vulkan.Image[imageCount];
        for (var i = 0; i < imageCount; i++)
            a.Images[i] = new Vulkan.Image(images[i].Image);

        if (a.Images.Length == 0)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"{a.RuntimeName} made a swapchain with no images.");
    }

    private static unsafe EnvironmentBlendMode FirstBlendMode(Attachment a)
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

        var beginInfo = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        CheckXr(a.Xr.BeginFrame(a.Session, &beginInfo), a.RuntimeName, "xrBeginFrame");

        // The copy is the only work that depends on content, and it happens only when the
        // compositor drew. Everything else is the same frame resubmitted.
        if (frameState.ShouldRender != 0 && _pending.TryTake(_scratch))
        {
            a.Uploader!.Stage(_scratch);

            var acquireInfo = new SwapchainImageAcquireInfo { Type = StructureType.SwapchainImageAcquireInfo };
            uint index;
            CheckXr(a.Xr.AcquireSwapchainImage(a.Swapchain, &acquireInfo, &index), a.RuntimeName, "xrAcquireSwapchainImage");

            var waitImage = new SwapchainImageWaitInfo { Type = StructureType.SwapchainImageWaitInfo, Timeout = InfiniteDuration };
            CheckXr(a.Xr.WaitSwapchainImage(a.Swapchain, &waitImage), a.RuntimeName, "xrWaitSwapchainImage");

            a.Uploader.Upload(a.Images[index]);

            var releaseInfo = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
            CheckXr(a.Xr.ReleaseSwapchainImage(a.Swapchain, &releaseInfo), a.RuntimeName, "xrReleaseSwapchainImage");

            if (!a.Uploaded)
                _log.Information("First picture uploaded to the OpenXR overlay");
            a.Uploaded = true;
        }

        var placement = _placement;
        var offset = placement.Offset;
        var quad = new CompositionLayerQuad
        {
            Type = StructureType.CompositionLayerQuad,
            LayerFlags = CompositionLayerFlags.BlendTextureSourceAlphaBit,
            Space = placement.Anchor is OverlayAnchor.World ? a.LocalSpace : a.ViewSpace,
            EyeVisibility = EyeVisibility.Both,
            SubImage = new SwapchainSubImage
            {
                Swapchain = a.Swapchain,
                ImageRect = new Rect2Di(new Offset2Di(0, 0), new Extent2Di(_resolution, _resolution)),
                ImageArrayIndex = 0,
            },
            Pose = new Posef(
                new Quaternionf(offset.QX, offset.QY, offset.QZ, offset.QW),
                new Vector3f(offset.X, offset.Y, offset.Z)),
            Size = new Extent2Df(placement.Width, placement.Width),
        };
        var layer = (CompositionLayerBaseHeader*)&quad;

        var show = frameState.ShouldRender != 0 && _visible && a.Uploaded;
        var endInfo = new FrameEndInfo
        {
            Type = StructureType.FrameEndInfo,
            DisplayTime = frameState.PredictedDisplayTime,
            EnvironmentBlendMode = a.BlendMode,
            LayerCount = show ? 1u : 0u,
            Layers = show ? &layer : null,
        };
        CheckXr(a.Xr.EndFrame(a.Session, &endInfo), a.RuntimeName, "xrEndFrame");
    }

    // ---- Small helpers ----

    private static readonly Posef Identity = new(new Quaternionf(0, 0, 0, 1), new Vector3f(0, 0, 0));

    private static OverlayStartFailure NotRunning(Result result, string call)
        => new(OverlayRuntimeState.NotStarted, $"No OpenXR runtime is running ({result} from {call}).");

    private static void CheckXr(Result result, string runtimeName, string call)
    {
        if (result < 0)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"{runtimeName} answered {result} to {call}.");
    }

    private static void CheckVk(uint result, string call)
    {
        if (result != 0)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"Vulkan answered {(Vulkan.Result)(int)result} to {call}.");
    }

    private static unsafe string FixedString(byte* bytes) => Marshal.PtrToStringUTF8((nint)bytes) ?? "";

    private static unsafe void WriteFixedString(byte* destination, int capacity, string value)
    {
        var span = new Span<byte>(destination, capacity);
        span.Clear();
        Encoding.UTF8.GetBytes(value.AsSpan(), span[..(capacity - 1)]);
    }

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
        public Swapchain Swapchain;
        public Vulkan.Image[] Images = [];
        public SwapchainFormat Format = SwapchainFormat.Bgra;
        public EnvironmentBlendMode BlendMode = EnvironmentBlendMode.Opaque;
        public VulkanUploader? Uploader;
        public bool SessionRunning;
        public bool Uploaded;

        public void Dispose()
        {
            Uploader?.Dispose();
            Uploader = null;

            if (Swapchain.Handle != 0)
                Xr.DestroySwapchain(Swapchain);
            if (LocalSpace.Handle != 0)
                Xr.DestroySpace(LocalSpace);
            if (ViewSpace.Handle != 0)
                Xr.DestroySpace(ViewSpace);
            if (Session.Handle != 0)
                Xr.DestroySession(Session);
            Swapchain = default;
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
}
