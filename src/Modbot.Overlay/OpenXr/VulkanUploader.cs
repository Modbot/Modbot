using Modbot.Overlay.OpenVr;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Modbot.Overlay.OpenXr;

/// <summary>A step of the OpenXR start-up that did not succeed, in the words the status will carry.</summary>
internal sealed class OverlayStartFailure(OverlayRuntimeState state, string detail) : Exception(detail)
{
    public OverlayRuntimeState State { get; } = state;

    public string Detail { get; } = detail;
}

/// <summary>
/// Moves one picture from ordinary memory into a swapchain image: a host-visible staging buffer,
/// one command buffer, two layout transitions and a copy.
/// </summary>
/// <remarks>
/// <para>This is the only work that depends on content, and it happens only when the compositor
/// drew — a few times a minute, not ninety times a second — so the copy waits on a fence rather
/// than pipelining. Simple and measurable beats clever here.</para>
/// <para>The image arrives in <c>COLOR_ATTACHMENT_OPTIMAL</c> and must go back the same way: that
/// is what <c>XR_KHR_vulkan_enable2</c> promises at acquire and requires at release. The queue is
/// the one the session was bound to, and it is only touched between <c>xrBeginFrame</c> and
/// <c>xrEndFrame</c>, which is the runtime's condition for sharing it.</para>
/// </remarks>
internal sealed unsafe class VulkanUploader : IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _pool;
    private readonly CommandBuffer _commands;
    private readonly Fence _fence;
    private readonly Buffer _staging;
    private readonly DeviceMemory _memory;
    private readonly byte* _mapped;
    private readonly uint _width;
    private readonly uint _height;
    private readonly SwapchainFormat _format;

    private VulkanUploader(
        Vk vk, Device device, Queue queue, CommandPool pool, CommandBuffer commands, Fence fence,
        Buffer staging, DeviceMemory memory, byte* mapped, uint width, uint height, SwapchainFormat format)
    {
        _vk = vk;
        _device = device;
        _queue = queue;
        _pool = pool;
        _commands = commands;
        _fence = fence;
        _staging = staging;
        _memory = memory;
        _mapped = mapped;
        _width = width;
        _height = height;
        _format = format;
    }

    /// <summary>Bytes in one frame: four a pixel, tightly packed.</summary>
    public int FrameLength => (int)(_width * _height * 4);

    public static VulkanUploader Create(
        Vk vk, PhysicalDevice physicalDevice, Device device, Queue queue, uint queueFamily,
        uint width, uint height, SwapchainFormat format)
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = queueFamily,
        };
        Check(vk.CreateCommandPool(device, &poolInfo, null, out var pool), "vkCreateCommandPool");

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(vk.AllocateCommandBuffers(device, &allocInfo, out var commands), "vkAllocateCommandBuffers");

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(vk.CreateFence(device, &fenceInfo, null, out var fence), "vkCreateFence");

        var size = (ulong)width * height * 4;
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        Check(vk.CreateBuffer(device, &bufferInfo, null, out var staging), "vkCreateBuffer");

        vk.GetBufferMemoryRequirements(device, staging, out var requirements);
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memoryProperties);

        const MemoryPropertyFlags wanted = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        var memoryType = uint.MaxValue;
        for (var i = 0u; i < memoryProperties.MemoryTypeCount; i++)
        {
            if ((requirements.MemoryTypeBits & (1u << (int)i)) != 0
                && (memoryProperties.MemoryTypes[(int)i].PropertyFlags & wanted) == wanted)
            {
                memoryType = i;
                break;
            }
        }

        if (memoryType == uint.MaxValue)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, "The graphics device has no memory the CPU can write into.");

        var memoryInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryType,
        };
        Check(vk.AllocateMemory(device, &memoryInfo, null, out var memory), "vkAllocateMemory");
        Check(vk.BindBufferMemory(device, staging, memory, 0), "vkBindBufferMemory");

        void* mapped;
        Check(vk.MapMemory(device, memory, 0, size, 0, &mapped), "vkMapMemory");

        return new VulkanUploader(vk, device, queue, pool, commands, fence, staging, memory, (byte*)mapped, width, height, format);
    }

    /// <summary>The staging memory, so a pending picture can be copied straight into it.</summary>
    public Span<byte> Staging => new(_mapped, FrameLength);

    /// <summary>Copies BGRA pixels into the staging memory in the swapchain's channel order.</summary>
    public void Stage(ReadOnlySpan<byte> bgra) => _format.CopyPixels(bgra, Staging);

    /// <summary>Copies what is in the staging memory into the image, and waits until it is there.</summary>
    public void Upload(Image image)
    {
        Check(_vk.ResetCommandBuffer(_commands, 0), "vkResetCommandBuffer");

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(_vk.BeginCommandBuffer(_commands, &begin), "vkBeginCommandBuffer");

        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = ImageLayout.ColorAttachmentOptimal,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        _vk.CmdPipelineBarrier(
            _commands,
            PipelineStageFlags.ColorAttachmentOutputBit,
            PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &toTransfer);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(_width, _height, 1),
        };
        _vk.CmdCopyBufferToImage(_commands, _staging, image, ImageLayout.TransferDstOptimal, 1, &region);

        var toAttachment = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ColorAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        _vk.CmdPipelineBarrier(
            _commands,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.ColorAttachmentOutputBit,
            0, 0, null, 0, null, 1, &toAttachment);

        Check(_vk.EndCommandBuffer(_commands), "vkEndCommandBuffer");

        var commands = _commands;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commands,
        };
        Check(_vk.QueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit");

        var fence = _fence;
        Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
        Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences");
    }

    private static void Check(Result result, string call)
    {
        if (result != Result.Success)
            throw new OverlayStartFailure(OverlayRuntimeState.Refused, $"Vulkan answered {result} to {call}.");
    }

    public void Dispose()
    {
        _vk.DeviceWaitIdle(_device);
        _vk.UnmapMemory(_device, _memory);
        _vk.DestroyBuffer(_device, _staging, null);
        _vk.FreeMemory(_device, _memory, null);
        _vk.DestroyFence(_device, _fence, null);
        _vk.DestroyCommandPool(_device, _pool, null);
    }
}
