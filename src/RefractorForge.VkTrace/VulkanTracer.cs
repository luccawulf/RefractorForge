using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using RefractorForge.Render;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace RefractorForge.VkTrace;

/// <summary>
/// The live traced view's kernel on Vulkan hardware ray queries (<c>VK_KHR_ray_query</c>): the level's triangles in
/// an acceleration structure the driver builds, walked by the card's RT cores, and <see cref="LiveTraceKernel"/>'s
/// lighting run over it in a compute shader (<c>Shaders/trace.comp</c>).
///
/// <para><b>Why.</b> The OpenGL kernel walks its own BVH in the shader, because OpenGL cannot reach RT hardware; on a
/// 4K viewport that was about five frames a second on an RTX 4060. The lighting is not the cost - the walk is.</para>
///
/// <para><b>No window, no OpenGL.</b> A frame comes back as two CPU-visible pictures at the traced size - the light
/// (display multiplier, with the camera distance in alpha) and the surface's own colour - which the editor uploads
/// to OpenGL textures and its existing composite draws; a tool can write them to a file. So the whole thing runs and
/// is tested headlessly.</para>
///
/// <para><b>Which card.</b> Only a DISCRETE device that reports ray queries is ever used; an integrated GPU is never
/// touched (a sustained load on this machine's has ended in a bugcheck), and the Direct3D 12 translation devices are
/// skipped. Without such a card <see cref="Available"/> is false and <see cref="Status"/> says why.</para>
/// </summary>
public sealed unsafe class VulkanTracer : IDisposable
{
    private const int UboBytes = 224 + 4 * 16 * LiveTraceKernel.MaxLamps;

    private readonly Vk _vk;
    private Instance _instance;
    private PhysicalDevice _phys;
    private Device _device;
    private Queue _queue;
    private uint _family;
    private KhrAccelerationStructure? _accel;
    private CommandPool _pool;
    private CommandBuffer _cmd;
    private Fence _fence;
    private PhysicalDeviceMemoryProperties _memProps;

    private DescriptorSetLayout _setLayout;
    private PipelineLayout _pipeLayout;
    private Pipeline _pipeline;
    private DescriptorPool _descPool;
    private DescriptorSet _set;
    private Sampler _sampler;

    // The scene.
    private Buf _verts, _tris, _map, _cuts, _mask, _blasBuf, _tlasBuf, _instances;
    private AccelerationStructureKHR _blas, _tlas;
    private Img _ground;
    private bool _hasGround;
    private int _opaque, _terrainTris;
    private bool _sceneSet;

    // The frame.
    private Img _accum, _light, _albedo;
    private Buf _lightOut, _albedoOut, _ubo;
    private int _w, _h;
    private uint _sample;
    // Rows traced per submission. A frame that is one long GPU command is what Windows resets a driver for (two
    // seconds without an answer - and the first frame on a card whose memory is full has taken that long, with the
    // driver paging textures out to make room). So a frame goes over in bands, each waited for; the band starts
    // small after anything was allocated and grows to the whole frame once frames are quick.
    private int _bandRows = 64;
    /// <summary>Submissions the last frame was split into, and the longest of them in milliseconds.</summary>
    public int LastBands { get; private set; }
    public double LastMaxBandMs { get; private set; }
    /// <summary>The ground picture is kept at most this many texels across on the card (it is 8192 on a big level:
    /// a quarter of a gigabyte the card usually does not have to spare beside the level's own textures).</summary>
    public const int MaxGroundSide = 4096;

    /// <summary>A discrete card with ray queries was found and everything was created on it.</summary>
    public bool Available { get; private set; }
    /// <summary>Why not, when <see cref="Available"/> is false; the card's name when it is.</summary>
    public string Status { get; private set; } = "";
    public string DeviceName { get; private set; } = "";
    /// <summary>Samples in the running picture.</summary>
    public int Samples { get; private set; }
    /// <summary>Milliseconds the last <see cref="Trace"/> took, submit to finished, the copy back included.</summary>
    public double LastGpuMs { get; private set; }
    public int Width => _w;
    public int Height => _h;
    public bool HasScene => _sceneSet;
    /// <summary>Triangles in the scene on the card.</summary>
    public int Triangles { get; private set; }

    private struct Buf { public VkBuffer Buffer; public DeviceMemory Memory; public ulong Size; public void* Mapped; }
    private struct Img { public Image Image; public DeviceMemory Memory; public ImageView View; public Format Format; public int W, H; }

    /// <summary>One traced frame, valid until the next call into the tracer.</summary>
    /// <param name="Light">Width x Height x 4 half floats, row 0 the BOTTOM row: rgb the display multiplier (what
    /// multiplies the surface colour), a the distance from the camera, negative for sky.</param>
    /// <param name="Albedo">Width x Height x 4 bytes: the surface's own colour; alpha 255 on terrain (the ground
    /// picture), about 128 on an object (its triangle's mean colour), 0 for sky.</param>
    public readonly record struct Frame(nint Light, nint Albedo, int Width, int Height);

    public VulkanTracer()
    {
        // Overlays, capture hooks and vendor switchers install themselves as implicit layers. None has any business
        // in a compute-only instance, and one misbehaving would take the editor down with it.
        Environment.SetEnvironmentVariable("VK_LOADER_LAYERS_DISABLE", "~implicit~");
        _vk = Vk.GetApi();
        try { Init(); }
        catch (Exception ex)
        {
            Available = false;
            Status = "Vulkan could not be set up: " + ex.Message;
        }
    }

    private static void Check(Result r, string what)
    {
        if (r != Result.Success) throw new InvalidOperationException($"{what} failed ({r})");
    }

    // ---- set-up ---------------------------------------------------------------------------------------------------

    private void Init()
    {
        var app = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version12 };
        var ici = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
        Check(_vk.CreateInstance(&ici, null, out _instance), "creating the Vulkan instance");

        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, &count, null);
        if (count == 0) { Status = "No Vulkan device on this machine."; return; }
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices) _vk.EnumeratePhysicalDevices(_instance, &count, p);

        string seen = "";
        bool found = false;
        foreach (var pd in devices)
        {
            _vk.GetPhysicalDeviceProperties(pd, out var props);
            string name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "";
            seen += (seen.Length > 0 ? "; " : "") + name;
            if (props.DeviceType != PhysicalDeviceType.DiscreteGpu) continue;              // never an integrated GPU
            if (name.Contains("Direct3D", StringComparison.OrdinalIgnoreCase)) continue;   // the D3D12 translation layer
            if (!HasExtensions(pd, "VK_KHR_acceleration_structure", "VK_KHR_ray_query", "VK_KHR_deferred_host_operations")) continue;
            var rq = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr };
            var asf = new PhysicalDeviceAccelerationStructureFeaturesKHR { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr, PNext = &rq };
            var f12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &asf };
            var f2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &f12 };
            _vk.GetPhysicalDeviceFeatures2(pd, &f2);
            if (!rq.RayQuery || !asf.AccelerationStructure || !f12.BufferDeviceAddress) continue;
            int fam = ComputeFamily(pd);
            if (fam < 0) continue;
            _phys = pd; _family = (uint)fam; DeviceName = name; found = true;
            break;
        }
        if (!found)
        {
            Status = "No dedicated graphics card with hardware ray tracing was found (saw: " + seen + ").";
            return;
        }
        _vk.GetPhysicalDeviceMemoryProperties(_phys, out _memProps);

        float prio = 1f;
        var qci = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = _family, QueueCount = 1, PQueuePriorities = &prio };
        var rqOn = new PhysicalDeviceRayQueryFeaturesKHR { SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr, RayQuery = true };
        var asOn = new PhysicalDeviceAccelerationStructureFeaturesKHR { SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr, AccelerationStructure = true, PNext = &rqOn };
        var v12On = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, BufferDeviceAddress = true, PNext = &asOn };
        var feat = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &v12On };
        var exts = new[] { "VK_KHR_acceleration_structure", "VK_KHR_ray_query", "VK_KHR_deferred_host_operations" };
        byte** extPtr = (byte**)SilkMarshal.StringArrayToPtr(exts);
        try
        {
            var dci = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo, PNext = &feat, QueueCreateInfoCount = 1, PQueueCreateInfos = &qci,
                EnabledExtensionCount = (uint)exts.Length, PpEnabledExtensionNames = extPtr,
            };
            Check(_vk.CreateDevice(_phys, &dci, null, out _device), "creating the Vulkan device");
        }
        finally { SilkMarshal.Free((nint)extPtr); }
        _vk.GetDeviceQueue(_device, _family, 0, out _queue);
        if (!_vk.TryGetDeviceExtension(_instance, _device, out _accel) || _accel is null)
            throw new InvalidOperationException("the acceleration-structure extension did not load");

        var pci = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = _family, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
        Check(_vk.CreateCommandPool(_device, &pci, null, out _pool), "creating the command pool");
        var cai = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        Check(_vk.AllocateCommandBuffers(_device, &cai, out _cmd), "allocating the command buffer");
        var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(_vk.CreateFence(_device, &fci, null, out _fence), "creating the fence");

        var sci = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge,
            MipmapMode = SamplerMipmapMode.Nearest, MaxLod = 0f,
        };
        Check(_vk.CreateSampler(_device, &sci, null, out _sampler), "creating the sampler");

        CreatePipeline();
        _ubo = CreateBuffer(UboBytes, BufferUsageFlags.UniformBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, map: true);
        Available = true;
        Status = DeviceName;
    }

    private bool HasExtensions(PhysicalDevice pd, params string[] wanted)
    {
        uint n = 0;
        _vk.EnumerateDeviceExtensionProperties(pd, (byte*)null, &n, null);
        var props = new ExtensionProperties[n];
        fixed (ExtensionProperties* p = props) _vk.EnumerateDeviceExtensionProperties(pd, (byte*)null, &n, p);
        var have = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
            fixed (ExtensionProperties* p = &props[i]) have.Add(SilkMarshal.PtrToString((nint)p->ExtensionName) ?? "");
        return wanted.All(have.Contains);
    }

    private int ComputeFamily(PhysicalDevice pd)
    {
        uint n = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(pd, &n, null);
        var fams = new QueueFamilyProperties[n];
        fixed (QueueFamilyProperties* p = fams) _vk.GetPhysicalDeviceQueueFamilyProperties(pd, &n, p);
        for (int i = 0; i < n; i++)
            if ((fams[i].QueueFlags & QueueFlags.ComputeBit) != 0) return i;
        return -1;
    }

    private void CreatePipeline()
    {
        var types = new[]
        {
            DescriptorType.AccelerationStructureKhr, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer, DescriptorType.StorageBuffer,
            DescriptorType.StorageBuffer, DescriptorType.StorageImage, DescriptorType.StorageImage, DescriptorType.StorageImage,
            DescriptorType.CombinedImageSampler, DescriptorType.UniformBuffer,
        };
        var bindings = stackalloc DescriptorSetLayoutBinding[types.Length];
        for (int i = 0; i < types.Length; i++)
            bindings[i] = new DescriptorSetLayoutBinding { Binding = (uint)i, DescriptorType = types[i], DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };
        var lci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)types.Length, PBindings = bindings };
        Check(_vk.CreateDescriptorSetLayout(_device, &lci, null, out _setLayout), "creating the descriptor layout");

        var sizes = stackalloc DescriptorPoolSize[5];
        sizes[0] = new DescriptorPoolSize { Type = DescriptorType.AccelerationStructureKhr, DescriptorCount = 1 };
        sizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 4 };
        sizes[2] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 3 };
        sizes[3] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        sizes[4] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 };
        var dpci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 5, PPoolSizes = sizes };
        Check(_vk.CreateDescriptorPool(_device, &dpci, null, out _descPool), "creating the descriptor pool");
        var layout = _setLayout;
        var dai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        Check(_vk.AllocateDescriptorSets(_device, &dai, out _set), "allocating the descriptor set");

        byte[] spirv;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("trace.spv")
                       ?? throw new InvalidOperationException("the compiled shader (trace.spv) is not in the assembly"))
        {
            spirv = new byte[s.Length];
            s.ReadExactly(spirv);
        }
        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var smci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Check(_vk.CreateShaderModule(_device, &smci, null, out module), "loading the shader");
        }
        var plci = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &layout };
        Check(_vk.CreatePipelineLayout(_device, &plci, null, out _pipeLayout), "creating the pipeline layout");
        nint entry = SilkMarshal.StringToPtr("main");
        try
        {
            var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = (byte*)entry };
            var cpci = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = _pipeLayout };
            Check(_vk.CreateComputePipelines(_device, default, 1, &cpci, null, out _pipeline), "creating the compute pipeline");
        }
        finally
        {
            SilkMarshal.Free(entry);
            _vk.DestroyShaderModule(_device, module, null);
        }
    }

    // ---- memory, buffers, images --------------------------------------------------------------------------------

    private uint MemoryType(uint typeBits, MemoryPropertyFlags want)
    {
        for (int i = 0; i < _memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << i)) != 0 && (_memProps.MemoryTypes[i].PropertyFlags & want) == want) return (uint)i;
        throw new InvalidOperationException("no suitable memory type on the card");
    }

    private Buf CreateBuffer(long size, BufferUsageFlags usage, MemoryPropertyFlags props, bool map = false)
    {
        var b = new Buf { Size = (ulong)Math.Max(size, 16) };
        var bci = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = b.Size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Check(_vk.CreateBuffer(_device, &bci, null, out b.Buffer), "creating a buffer");
        _vk.GetBufferMemoryRequirements(_device, b.Buffer, out var req);
        var flags = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };
        var mai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = MemoryType(req.MemoryTypeBits, props) };
        if ((usage & BufferUsageFlags.ShaderDeviceAddressBit) != 0) mai.PNext = &flags;
        Check(_vk.AllocateMemory(_device, &mai, null, out b.Memory), $"allocating {size / 1048576.0:0.0} MB on the card");
        Check(_vk.BindBufferMemory(_device, b.Buffer, b.Memory, 0), "binding buffer memory");
        if (map)
        {
            void* p;
            Check(_vk.MapMemory(_device, b.Memory, 0, b.Size, 0, &p), "mapping a buffer");
            b.Mapped = p;
        }
        return b;
    }

    private void Destroy(ref Buf b)
    {
        if (b.Buffer.Handle != 0) _vk.DestroyBuffer(_device, b.Buffer, null);
        if (b.Memory.Handle != 0) _vk.FreeMemory(_device, b.Memory, null);
        b = default;
    }

    private ulong Address(in Buf b)
    {
        var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = b.Buffer };
        return _vk.GetBufferDeviceAddress(_device, &info);
    }

    /// <summary>A device-local buffer holding <paramref name="data"/>, filled through a staging buffer.</summary>
    private Buf CreateFilled<T>(ReadOnlySpan<T> data, BufferUsageFlags usage) where T : unmanaged
    {
        long bytes = (long)data.Length * sizeof(T);
        var dst = CreateBuffer(bytes, usage | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit);
        if (bytes == 0) return dst;
        var staging = CreateBuffer(bytes, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, map: true);
        fixed (T* src = data) System.Buffer.MemoryCopy(src, staging.Mapped, (long)staging.Size, bytes);
        Begin();
        var region = new BufferCopy { Size = (ulong)bytes };
        _vk.CmdCopyBuffer(_cmd, staging.Buffer, dst.Buffer, 1, &region);
        Submit();
        Destroy(ref staging);
        return dst;
    }

    private Img CreateImage(int w, int h, Format format, ImageUsageFlags usage)
    {
        var img = new Img { Format = format, W = w, H = h };
        var ici = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format, Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1, ArrayLayers = 1, Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal, Usage = usage,
            SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined,
        };
        Check(_vk.CreateImage(_device, &ici, null, out img.Image), "creating an image");
        _vk.GetImageMemoryRequirements(_device, img.Image, out var req);
        var mai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = MemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        Check(_vk.AllocateMemory(_device, &mai, null, out img.Memory), $"allocating a {w} x {h} image on the card");
        Check(_vk.BindImageMemory(_device, img.Image, img.Memory, 0), "binding image memory");
        var vci = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = img.Image, ViewType = ImageViewType.Type2D, Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        Check(_vk.CreateImageView(_device, &vci, null, out img.View), "creating an image view");
        return img;
    }

    private void Destroy(ref Img i)
    {
        if (i.View.Handle != 0) _vk.DestroyImageView(_device, i.View, null);
        if (i.Image.Handle != 0) _vk.DestroyImage(_device, i.Image, null);
        if (i.Memory.Handle != 0) _vk.FreeMemory(_device, i.Memory, null);
        i = default;
    }

    private void Transition(in Img img, ImageLayout from, ImageLayout to)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier, OldLayout = from, NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = img.Image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit,
        };
        _vk.CmdPipelineBarrier(_cmd, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 0, null, 1, &barrier);
    }

    private void Begin()
    {
        Check(_vk.ResetCommandBuffer(_cmd, 0), "resetting the command buffer");
        var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(_vk.BeginCommandBuffer(_cmd, &bi), "beginning commands");
    }

    private void Submit()
    {
        Check(_vk.EndCommandBuffer(_cmd), "ending commands");
        var cmd = _cmd; var fence = _fence;
        var si = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        Check(_vk.QueueSubmit(_queue, 1, &si, fence), "submitting to the card");
        // Thirty seconds: far beyond any frame, short enough that a hung driver ends in an error and not a frozen editor.
        var r = _vk.WaitForFences(_device, 1, &fence, true, 30_000_000_000UL);
        if (r != Result.Success) throw new InvalidOperationException($"the card did not finish in time ({r})");
        Check(_vk.ResetFences(_device, 1, &fence), "resetting the fence");
    }

    // ---- the scene ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Put a level on the card: its triangles into an acceleration structure (opaque ones in one geometry, cut-out
    /// ones - leaves, fences - in a second the shader tests against their masks), and the ground picture.
    /// </summary>
    public void SetScene(LiveTraceScene scene, Texture2D? ground)
    {
        if (!Available) return;
        _vk.DeviceWaitIdle(_device);
        DestroyScene();
        int n = scene.TriCount;
        if (n == 0) throw new InvalidOperationException("the scene has no triangles");

        // Acceleration-structure order: every opaque triangle, then every cut-out one.
        var order = new int[n];
        int opaque = 0;
        for (int t = 0; t < n; t++) if (scene.Tris[t * 16 + 15] < 0f) order[opaque++] = t;
        int k = opaque;
        for (int t = 0; t < n; t++) if (scene.Tris[t * 16 + 15] >= 0f) order[k++] = t;
        var verts = new float[n * 9];
        for (int i = 0; i < n; i++)
        {
            int o = order[i] * 16, d = i * 9;
            float ax = scene.Tris[o], ay = scene.Tris[o + 1], az = scene.Tris[o + 2];
            verts[d] = ax; verts[d + 1] = ay; verts[d + 2] = az;
            verts[d + 3] = ax + scene.Tris[o + 4]; verts[d + 4] = ay + scene.Tris[o + 5]; verts[d + 5] = az + scene.Tris[o + 6];
            verts[d + 6] = ax + scene.Tris[o + 8]; verts[d + 7] = ay + scene.Tris[o + 9]; verts[d + 8] = az + scene.Tris[o + 10];
        }
        _opaque = opaque; _terrainTris = scene.TerrainTris; Triangles = n;

        const BufferUsageFlags input = BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit;
        _verts = CreateFilled<float>(verts, input);
        _tris = CreateFilled<float>(scene.Tris, BufferUsageFlags.StorageBufferBit);
        _map = CreateFilled<int>(order, BufferUsageFlags.StorageBufferBit);
        _cuts = CreateFilled<float>(scene.Cuts.Length > 0 ? scene.Cuts : new float[8], BufferUsageFlags.StorageBufferBit);
        _mask = CreateFilled<uint>(scene.MaskBits.Length > 0 ? scene.MaskBits : new uint[4], BufferUsageFlags.StorageBufferBit);

        BuildBottom(n, opaque);
        BuildTop();

        // The ground picture.
        _hasGround = ground is not null;
        int gw = ground?.Width ?? 1, gh = ground?.Height ?? 1;
        byte[] pixels = ground?.Rgba ?? new byte[] { 115, 107, 84, 255 };
        while (Math.Max(gw, gh) > MaxGroundSide && gw % 2 == 0 && gh % 2 == 0) pixels = Halve(pixels, ref gw, ref gh);
        _ground = CreateImage(gw, gh, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);
        var staging = CreateBuffer(pixels.LongLength, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, map: true);
        fixed (byte* src = pixels) System.Buffer.MemoryCopy(src, staging.Mapped, (long)staging.Size, pixels.LongLength);
        Begin();
        Transition(_ground, ImageLayout.Undefined, ImageLayout.TransferDstOptimal);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D((uint)gw, (uint)gh, 1),
        };
        _vk.CmdCopyBufferToImage(_cmd, staging.Buffer, _ground.Image, ImageLayout.TransferDstOptimal, 1, &region);
        Transition(_ground, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal);
        Submit();
        Destroy(ref staging);

        _sceneSet = true;
        if (_w > 0) WriteDescriptors();
        Reset();
    }

    // A 2 x 2 box filter: the picture at half the size.
    private static byte[] Halve(byte[] src, ref int w, ref int h)
    {
        int nw = w / 2, nh = h / 2, sw = w;
        var dst = new byte[nw * nh * 4];
        Parallel.For(0, nh, y =>
        {
            for (int x = 0; x < nw; x++)
            {
                int a = ((y * 2) * sw + x * 2) * 4, b = a + 4, c = a + sw * 4, d = c + 4, o = (y * nw + x) * 4;
                for (int k = 0; k < 4; k++) dst[o + k] = (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) >> 2);
            }
        });
        w = nw; h = nh;
        return dst;
    }

    private void BuildBottom(int total, int opaque)
    {
        ulong vertexAddress = Address(_verts);
        int cut = total - opaque;
        int geoms = (opaque > 0 ? 1 : 0) + (cut > 0 ? 1 : 0);
        var geometry = stackalloc AccelerationStructureGeometryKHR[2];
        var ranges = stackalloc AccelerationStructureBuildRangeInfoKHR[2];
        var counts = stackalloc uint[2];
        int g = 0;
        void Add(int first, int n, bool isOpaque)
        {
            var tri = new AccelerationStructureGeometryTrianglesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr, VertexFormat = Format.R32G32B32Sfloat,
                VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertexAddress + (ulong)first * 36UL },
                VertexStride = 12, MaxVertex = (uint)(n * 3 - 1), IndexType = IndexType.NoneKhr,
            };
            geometry[g] = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr, GeometryType = GeometryTypeKHR.TrianglesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR { Triangles = tri },
                Flags = isOpaque ? GeometryFlagsKHR.OpaqueBitKhr : 0,
            };
            ranges[g] = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = (uint)n };
            counts[g] = (uint)n;
            g++;
        }
        // Geometry 0 is the opaque set whenever there is one - the shader's triangleOf() relies on it.
        if (opaque > 0) Add(0, opaque, true);
        if (cut > 0) Add(opaque, cut, false);

        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr, Type = AccelerationStructureTypeKHR.BottomLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr, Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = (uint)geoms, PGeometries = geometry,
        };
        _blas = BuildStructure(ref build, counts, ranges, AccelerationStructureTypeKHR.BottomLevelKhr, out _blasBuf);
    }

    private void BuildTop()
    {
        var blasInfo = new AccelerationStructureDeviceAddressInfoKHR { SType = StructureType.AccelerationStructureDeviceAddressInfoKhr, AccelerationStructure = _blas };
        ulong blasAddress = _accel!.GetAccelerationStructureDeviceAddress(_device, &blasInfo);
        // One instance, written out by hand (VkAccelerationStructureInstanceKHR, 64 bytes): an identity 3 x 4
        // transform, custom index 0 with mask 0xFF, no hit-group offset with the "do not cull by facing" flag, and
        // the bottom-level structure's address.
        var raw = new byte[64];
        fixed (byte* p = raw)
        {
            float* m = (float*)p;
            m[0] = 1f; m[5] = 1f; m[10] = 1f;
            *(uint*)(p + 48) = 0xFFu << 24;
            *(uint*)(p + 52) = 0x1u << 24;          // VK_GEOMETRY_INSTANCE_TRIANGLE_FACING_CULL_DISABLE_BIT_KHR
            *(ulong*)(p + 56) = blasAddress;
        }
        _instances = CreateFilled<byte>(raw, BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit);
        var inst = new AccelerationStructureGeometryInstancesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryInstancesDataKhr, ArrayOfPointers = false,
            Data = new DeviceOrHostAddressConstKHR { DeviceAddress = Address(_instances) },
        };
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr, GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR { Instances = inst },
        };
        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr, Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr, Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = 1, PGeometries = &geometry,
        };
        uint one = 1;
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = 1 };
        _tlas = BuildStructure(ref build, &one, &range, AccelerationStructureTypeKHR.TopLevelKhr, out _tlasBuf);
    }

    private AccelerationStructureKHR BuildStructure(ref AccelerationStructureBuildGeometryInfoKHR build, uint* counts,
        AccelerationStructureBuildRangeInfoKHR* ranges, AccelerationStructureTypeKHR type, out Buf storage)
    {
        var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        fixed (AccelerationStructureBuildGeometryInfoKHR* pb = &build)
            _accel!.GetAccelerationStructureBuildSizes(_device, AccelerationStructureBuildTypeKHR.DeviceKhr, pb, counts, &sizes);
        storage = CreateBuffer((long)sizes.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit, MemoryPropertyFlags.DeviceLocalBit);
        var ci = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr, Buffer = storage.Buffer, Size = sizes.AccelerationStructureSize, Type = type,
        };
        Check(_accel!.CreateAccelerationStructure(_device, &ci, null, out var structure), "creating an acceleration structure");
        // The scratch buffer is over-allocated and its address rounded up: the build wants it aligned (256 covers
        // every driver's minAccelerationStructureScratchOffsetAlignment).
        var scratch = CreateBuffer((long)sizes.BuildScratchSize + 256,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit, MemoryPropertyFlags.DeviceLocalBit);
        build.DstAccelerationStructure = structure;
        build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = (Address(scratch) + 255UL) & ~255UL };
        Begin();
        fixed (AccelerationStructureBuildGeometryInfoKHR* pb = &build)
            _accel.CmdBuildAccelerationStructures(_cmd, 1, pb, &ranges);
        Submit();
        Destroy(ref scratch);
        return structure;
    }

    private void DestroyScene()
    {
        if (_tlas.Handle != 0) _accel!.DestroyAccelerationStructure(_device, _tlas, null);
        if (_blas.Handle != 0) _accel!.DestroyAccelerationStructure(_device, _blas, null);
        _tlas = default; _blas = default;
        Destroy(ref _tlasBuf); Destroy(ref _blasBuf); Destroy(ref _instances);
        Destroy(ref _verts); Destroy(ref _tris); Destroy(ref _map); Destroy(ref _cuts); Destroy(ref _mask);
        Destroy(ref _ground);
        _sceneSet = false;
    }

    // ---- the frame ------------------------------------------------------------------------------------------------

    /// <summary>Start the picture again: the camera, the light or the level changed.</summary>
    public void Reset() { Samples = 0; _sample = 0; }

    private void EnsureTargets(int w, int h)
    {
        if (w == _w && h == _h && _accum.Image.Handle != 0) return;
        _vk.DeviceWaitIdle(_device);
        Destroy(ref _accum); Destroy(ref _light); Destroy(ref _albedo);
        Destroy(ref _lightOut); Destroy(ref _albedoOut);
        _w = w; _h = h;
        _accum = CreateImage(w, h, Format.R32G32B32A32Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit);
        _light = CreateImage(w, h, Format.R16G16B16A16Sfloat, ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit);
        _albedo = CreateImage(w, h, Format.R8G8B8A8Unorm, ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit);
        const MemoryPropertyFlags host = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit;
        _lightOut = CreateReadback((long)w * h * 8, host);
        _albedoOut = CreateReadback((long)w * h * 4, host);
        Begin();
        Transition(_accum, ImageLayout.Undefined, ImageLayout.General);
        Transition(_light, ImageLayout.Undefined, ImageLayout.General);
        Transition(_albedo, ImageLayout.Undefined, ImageLayout.General);
        Submit();
        WriteDescriptors();
        Reset();
        _bandRows = 64;                     // new memory on the card: go carefully until a frame has been timed
    }

    // Reading a frame back out of write-combined memory is many times slower than out of cached memory, so the
    // cached kind is asked for first and the plain kind only where a driver has none.
    private Buf CreateReadback(long size, MemoryPropertyFlags preferred)
    {
        try { return CreateBuffer(size, BufferUsageFlags.TransferDstBit, preferred, map: true); }
        catch (InvalidOperationException)
        {
            return CreateBuffer(size, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, map: true);
        }
    }

    private void WriteDescriptors()
    {
        if (!_sceneSet || _accum.Image.Handle == 0) return;
        var writes = stackalloc WriteDescriptorSet[10];
        var tlas = _tlas;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &tlas,
        };
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstSet = _set, DstBinding = 0, DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        var bufs = stackalloc DescriptorBufferInfo[5];
        bufs[0] = new DescriptorBufferInfo { Buffer = _tris.Buffer, Range = Vk.WholeSize };
        bufs[1] = new DescriptorBufferInfo { Buffer = _map.Buffer, Range = Vk.WholeSize };
        bufs[2] = new DescriptorBufferInfo { Buffer = _cuts.Buffer, Range = Vk.WholeSize };
        bufs[3] = new DescriptorBufferInfo { Buffer = _mask.Buffer, Range = Vk.WholeSize };
        bufs[4] = new DescriptorBufferInfo { Buffer = _ubo.Buffer, Range = UboBytes };
        for (int i = 0; i < 4; i++)
            writes[1 + i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = (uint)(1 + i), DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufs[i],
            };
        var imgs = stackalloc DescriptorImageInfo[4];
        imgs[0] = new DescriptorImageInfo { ImageView = _accum.View, ImageLayout = ImageLayout.General };
        imgs[1] = new DescriptorImageInfo { ImageView = _light.View, ImageLayout = ImageLayout.General };
        imgs[2] = new DescriptorImageInfo { ImageView = _albedo.View, ImageLayout = ImageLayout.General };
        imgs[3] = new DescriptorImageInfo { ImageView = _ground.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal, Sampler = _sampler };
        for (int i = 0; i < 3; i++)
            writes[5 + i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = (uint)(5 + i), DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage, PImageInfo = &imgs[i],
            };
        writes[8] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = 8, DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &imgs[3],
        };
        writes[9] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = 9, DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &bufs[4],
        };
        _vk.UpdateDescriptorSets(_device, 10, writes, 0, null);
    }

    private void WriteParams(LiveTraceKernel.Params p, int samples, bool reset)
    {
        var span = new Span<byte>(_ubo.Mapped, UboBytes);
        span.Clear();
        float* f = (float*)_ubo.Mapped;
        int* i = (int*)_ubo.Mapped;
        // The matrix goes over exactly as the OpenGL kernel is handed it: System.Numerics' sixteen floats in memory
        // order, which GLSL reads as columns.
        var m = p.InvViewProj;
        new ReadOnlySpan<float>(&m, 16).CopyTo(new Span<float>(f, 16));
        void V4(int at, Vector3 v, float w) { f[at / 4] = v.X; f[at / 4 + 1] = v.Y; f[at / 4 + 2] = v.Z; f[at / 4 + 3] = w; }
        V4(64, p.CamPos, 0f);
        i[20] = p.Width; i[21] = p.Height; i[22] = unchecked((int)_sample); i[23] = reset ? 1 : 0;
        int lamps = Math.Min(p.Lamps.Length, LiveTraceKernel.MaxLamps);
        i[24] = _opaque; i[25] = _terrainTris; i[26] = p.Bounces; i[27] = lamps;
        V4(112, p.ToSun, p.SunTanHalf);
        V4(128, p.SunIrradiance, 0f);
        V4(144, p.SkyZenith, 0f);
        V4(160, p.SkyHorizon, 0f);
        V4(176, p.GroundRadiance, 0f);
        f[48] = p.Exposure; f[49] = p.Gamma; f[50] = p.BounceGain; f[51] = p.WorldSize;
        i[52] = _hasGround ? 1 : 0; i[53] = samples;
        const int la = 224 / 4, stride = LiveTraceKernel.MaxLamps * 4;
        for (int l = 0; l < lamps; l++)
        {
            var lamp = p.Lamps[l];
            int a = la + l * 4, b = a + stride, c = b + stride, d = c + stride;
            f[a] = lamp.Position.X; f[a + 1] = lamp.Position.Y; f[a + 2] = lamp.Position.Z; f[a + 3] = lamp.Radius;
            f[b] = lamp.Colour.X; f[b + 1] = lamp.Colour.Y; f[b + 2] = lamp.Colour.Z; f[b + 3] = lamp.Intensity;
            f[c] = lamp.SpotDir.X; f[c + 1] = lamp.SpotDir.Y; f[c + 2] = lamp.SpotDir.Z; f[c + 3] = lamp.CosOuter;
            f[d] = lamp.CosInner; f[d + 1] = lamp.Falloff; f[d + 2] = lamp.SourceSize; f[d + 3] = lamp.Flags;
        }
    }

    /// <summary>
    /// Trace <paramref name="samples"/> more samples a pixel into the running picture and bring the result back.
    /// </summary>
    /// <param name="p">The view and the light; its Width and Height are the traced size.</param>
    /// <param name="samples">Light samples per pixel this call, 1 to 64. The camera ray is traced once.</param>
    public Frame Trace(LiveTraceKernel.Params p, int samples = 1)
    {
        if (!Available || !_sceneSet) throw new InvalidOperationException("the tracer has no scene");
        samples = Math.Clamp(samples, 1, 64);
        EnsureTargets(p.Width, p.Height);
        bool reset = Samples == 0;
        if (reset) _sample = 0;
        WriteParams(p, samples, reset);

        long t0 = Stopwatch.GetTimestamp();
        int rows = Math.Clamp(_bandRows, 8, _h);
        rows = (rows + 7) / 8 * 8;
        int bands = 0; double longest = 0;
        for (int row = 0; row < _h; row += rows)
        {
            long tb = Stopwatch.GetTimestamp();
            ((int*)_ubo.Mapped)[54] = row;                       // uFlags.z: this band's first row
            int n = Math.Min(rows, _h - row);
            bool last = row + rows >= _h;
            Begin();
            _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Compute, _pipeline);
            var set = _set;
            _vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Compute, _pipeLayout, 0, 1, &set, 0, null);
            _vk.CmdDispatch(_cmd, (uint)((_w + 7) / 8), (uint)((n + 7) / 8), 1);
            if (last)
            {
                var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.TransferReadBit };
                _vk.CmdPipelineBarrier(_cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.TransferBit, 0, 1, &barrier, 0, null, 0, null);
                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D((uint)_w, (uint)_h, 1),
                };
                _vk.CmdCopyImageToBuffer(_cmd, _light.Image, ImageLayout.General, _lightOut.Buffer, 1, &region);
                _vk.CmdCopyImageToBuffer(_cmd, _albedo.Image, ImageLayout.General, _albedoOut.Buffer, 1, &region);
            }
            Submit();
            bands++;
            longest = Math.Max(longest, Stopwatch.GetElapsedTime(tb).TotalMilliseconds);
        }
        LastGpuMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        LastBands = bands; LastMaxBandMs = longest;
        // Aim each band at about 40 ms: a quick frame becomes one band, a slow one many short ones.
        double perRow = LastGpuMs / Math.Max(_h, 1);
        int want = perRow <= 1e-6 ? _h : (int)(40.0 / perRow);
        // Grow at most fourfold a frame, so one quick frame after a stall does not bet the next on a single command.
        _bandRows = Math.Clamp(Math.Min(want, _bandRows * 4), 16, Math.Max(_h, 16));

        _sample += (uint)samples;
        Samples += samples;
        return new Frame((nint)_lightOut.Mapped, (nint)_albedoOut.Mapped, _w, _h);
    }

    /// <summary>The running sum, for tests: Width x Height x 4 floats (summed light, sample count), row 0 the bottom.</summary>
    public float[] ReadAccum()
    {
        if (_accum.Image.Handle == 0) return Array.Empty<float>();
        long bytes = (long)_w * _h * 16;
        var tmp = CreateBuffer(bytes, BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, map: true);
        Begin();
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D((uint)_w, (uint)_h, 1),
        };
        _vk.CmdCopyImageToBuffer(_cmd, _accum.Image, ImageLayout.General, tmp.Buffer, 1, &region);
        Submit();
        var result = new float[_w * _h * 4];
        new ReadOnlySpan<float>(tmp.Mapped, result.Length).CopyTo(result);
        Destroy(ref tmp);
        return result;
    }

    public void Dispose()
    {
        if (_device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);
            DestroyScene();
            Destroy(ref _accum); Destroy(ref _light); Destroy(ref _albedo);
            Destroy(ref _lightOut); Destroy(ref _albedoOut); Destroy(ref _ubo);
            if (_pipeline.Handle != 0) _vk.DestroyPipeline(_device, _pipeline, null);
            if (_pipeLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, _pipeLayout, null);
            if (_descPool.Handle != 0) _vk.DestroyDescriptorPool(_device, _descPool, null);
            if (_setLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
            if (_sampler.Handle != 0) _vk.DestroySampler(_device, _sampler, null);
            if (_fence.Handle != 0) _vk.DestroyFence(_device, _fence, null);
            if (_pool.Handle != 0) _vk.DestroyCommandPool(_device, _pool, null);
            _vk.DestroyDevice(_device, null);
            _device = default;
        }
        if (_instance.Handle != 0) { _vk.DestroyInstance(_instance, null); _instance = default; }
        Available = false;
    }
}
