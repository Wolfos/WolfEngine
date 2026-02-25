using System.Numerics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using Silk.NET.Core.Native;
using Silk.NET.SDL;
using WolfEngine.Backend.Metal;
using WolfEngine.Mathematics;
using WolfEngine.Platform;
using WolfEngine.Profiling;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;
using AbstractionBlendMode = WolfEngine.Rendering.Abstraction.BlendMode;

namespace WolfEngine;

[SupportedOSPlatform("macos")]
internal unsafe class WolfRendererMetal : IRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialParams
    {
        public Vector4 BaseColor;
        public Vector2 MetallicRoughnessFactor;
        public Vector2 Padding;
    }

    private const string WindowTitle = "WolfEngine";
    private const ulong DefaultPackedVertexBufferBytes = 256UL * 1024UL * 1024UL;
    private const ulong DefaultPackedIndexBufferBytes = 128UL * 1024UL * 1024UL;
    private static readonly ulong MaxPackedVertexBufferBytes = ParsePositiveUlongEnvironmentVariable(
        "WOLF_MAX_PACKED_VERTEX_BYTES",
        2UL * 1024UL * 1024UL * 1024UL);
    private static readonly ulong MaxPackedIndexBufferBytes = ParsePositiveUlongEnvironmentVariable(
        "WOLF_MAX_PACKED_INDEX_BYTES",
        1UL * 1024UL * 1024UL * 1024UL);

    private readonly int _width;
    private readonly int _height;
    private readonly IShaderCompiler _shaderCompiler;
    private readonly IMacOSInputHandler _inputHandler;
    private readonly BindlessResourceRegistry _bindlessRegistry;
    private readonly GpuDrawHardeningStats _hardeningStats;
    private readonly Dictionary<Mesh, MeshResources> _meshResources = new();
    private MetalBuffer? _packedVertexBuffer;
    private MetalBuffer? _packedIndexBuffer;
    private ulong _packedVertexBufferUsedBytes;
    private ulong _packedIndexBufferUsedBytes;
    private bool _needsPackedGeometryReencode;
    private MTLTexture _depthTexture;
    private MTLDepthStencilState _depthState;
    private readonly Sdl _sdl;

    private MTLDevice _device;
    private MTLCommandQueue _commandQueue;
    private MetalDevice _gfxDevice;
    private Window* _window;
    private void* _metalView;
    private CAMetalLayer _metalLayer;
    private IntPtr _nativeWindow = IntPtr.Zero;
    private bool _isRunning;
    private bool _hasDrawableSize;
    private double _drawableWidth;
    private double _drawableHeight;
    private bool _skipFrameAfterResize;
    private bool _needsBindlessRefresh;
    private bool _loggedPackedCapacityLimit;
    private DescriptorHandle _linearSamplerHandle = DescriptorHandle.Invalid;
    private bool _isGpuCaptureActive;
    private bool _gpuCaptureWritesTraceFile;
    private string _lastGpuCapturePath = string.Empty;
    private int _presentFrameIndex;
    private int _lastDrawableAcquireFailureLogFrame = int.MinValue;
    private bool _vsyncEnabled = Screen.VSyncEnabled;
    private Action _startupCallback = static () => { };
    private Action<float> _updateCallback = static deltaTime => { };
    private Action<float> _renderCallback = static deltaTime => { };


    private static readonly Selector NextDrawableSelector = new("nextDrawable");
    private static readonly Selector DrawableSizeSelector = new("setDrawableSize:");
    private const int MacTitlebarDoubleClickHeight = 28;
    private const int MacTrafficLightExclusionWidth = 70;

    private sealed class MeshResources
    {
        public MeshResources(ulong vertexOffsetBytes, ulong indexOffsetBytes, int baseVertex, ulong indexCount)
        {
            VertexOffsetBytes = vertexOffsetBytes;
            IndexOffsetBytes = indexOffsetBytes;
            BaseVertex = baseVertex;
            IndexCount = indexCount;
        }

        public ulong VertexOffsetBytes { get; }

        public ulong IndexOffsetBytes { get; }

        public int BaseVertex { get; }

        public ulong IndexCount { get; }
    }
    
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VertexData
    {
        public Vector4 Position;
        public Vector3 Normal;
        public Vector2 UV;
        public Vector4 Tangent;
    }

    public WolfRendererMetal(
        IShaderCompiler shaderCompiler,
        IMacOSInputHandler inputHandler,
        BindlessResourceRegistry bindlessRegistry,
        GpuDrawHardeningStats hardeningStats)
    {
        _width = 1280;
        _height = 720;
        _shaderCompiler = shaderCompiler;
        _inputHandler = inputHandler;
        _bindlessRegistry = bindlessRegistry ?? throw new ArgumentNullException(nameof(bindlessRegistry));
        _hardeningStats = hardeningStats ?? throw new ArgumentNullException(nameof(hardeningStats));

        ObjectiveC.LinkMetal();
        ObjectiveC.LinkCoreGraphics();
        ObjectiveC.LinkAppKit();
        ObjectiveC.LinkMetalKit();

        _sdl = Sdl.GetApi();
    }
    
    public void Run(Action startup, Action<float> update, Action<float> render)
    {
        _startupCallback = startup ?? throw new ArgumentNullException(nameof(startup));
        _updateCallback = update ?? throw new ArgumentNullException(nameof(update));
        _renderCallback = render ?? throw new ArgumentNullException(nameof(render));

        try
        {
            CreateDevice();
            CreateCommandQueue();
            CreateDepthState();
            InitializeWindow();
            MainLoop();
        }
        finally
        {
            Shutdown();
        }
    }
    

    private void MainLoop()
    {
        _isRunning = true;
        var @event = new Event();

        _startupCallback();

        while (_isRunning)
        {
            PumpEvents(ref @event);

            // TODO: Delta time
            _updateCallback(0);
            _renderCallback(0);
        }
    }

    private void PumpEvents(ref Event @event)
    {
        while (_sdl.PollEvent(ref @event) != 0)
        {
            switch ((EventType)@event.Type)
            {
                case EventType.Quit:
                    _isRunning = false;
                    break;
                case EventType.Windowevent:
                    HandleWindowEvent(@event);
                    break;
                case EventType.Mousebuttondown:
                    if (HandleMacTitlebarDoubleClick(@event.Button))
                    {
                        break;
                    }
                    _inputHandler.HandleInputEvents(ref @event);
                    break;
                default:
                    _inputHandler.HandleInputEvents(ref @event);
                    break;
            }
        }
    }
    

    private void HandleWindowEvent(Event @event)
    {
        if (@event.Window.Event == (byte)WindowEventID.Close)
        {
            _isRunning = false;
            return;
        }

        if (@event.Window.Event is (byte)WindowEventID.Resized or (byte)WindowEventID.SizeChanged)
        {
            UpdateDrawableSize();
        }
    }

    private void InitializeWindow()
    {
        if (_sdl.Init(Sdl.InitVideo) < 0)
        {
            throw new InvalidOperationException("Failed to initialise SDL video subsystem.");
        }

        var titlePtr = SilkMarshal.StringToPtr(WindowTitle, NativeStringEncoding.UTF8);
        try
        {
            var flags = WindowFlags.Resizable | WindowFlags.AllowHighdpi | WindowFlags.Metal;
            _window = _sdl.CreateWindow((byte*)titlePtr, Sdl.WindowposCentered, Sdl.WindowposCentered, _width, _height, (uint)flags);
        }
        finally
        {
            SilkMarshal.Free(titlePtr);
        }

        if (_window is null)
        {
            throw new InvalidOperationException("Failed to create SDL window.");
        }

        _metalView = _sdl.MetalCreateView(_window);
        if (_metalView is null)
        {
            throw new InvalidOperationException("Failed to create Metal view for SDL window.");
        }

        var layerPtr = _sdl.MetalGetLayer(_metalView);
        if (layerPtr is null)
        {
            throw new InvalidOperationException("Failed to retrieve CAMetalLayer from SDL view.");
        }

        _metalLayer = new CAMetalLayer(new IntPtr(layerPtr));
        _metalLayer.Device = _device;
        _metalLayer.PixelFormat = MTLPixelFormat.BGRA8Unorm;
        _metalLayer.FramebufferOnly = false;
        _metalLayer.DisplaySyncEnabled = Screen.VSyncEnabled;
        _vsyncEnabled = Screen.VSyncEnabled;

        UpdateDrawableSize();
        ConfigureNativeWindowChrome();
        UpdateDrawableSize();
    }

    private void ConfigureNativeWindowChrome()
    {
        if (_metalView is null)
        {
            return;
        }

        var nsView = new IntPtr(_metalView);
        var nsWindow = ObjectiveC.IntPtr_objc_msgSend(nsView, "window");
        if (nsWindow == IntPtr.Zero)
        {
            return;
        }

        _nativeWindow = nsWindow;
        var window = new NSWindowInstance(nsWindow);
        window.EnableUnifiedTitlebarChrome(includeFullSizeContentView: true);
    }

    private bool HandleMacTitlebarDoubleClick(MouseButtonEvent buttonEvent)
    {
        if (_nativeWindow == IntPtr.Zero)
        {
            return false;
        }

        if (buttonEvent.Button != Sdl.ButtonLeft || buttonEvent.Clicks < 2)
        {
            return false;
        }

        if (buttonEvent.Y > MacTitlebarDoubleClickHeight || buttonEvent.X < MacTrafficLightExclusionWidth)
        {
            return false;
        }

        ObjectiveC.objc_msgSend(_nativeWindow, "zoom:", IntPtr.Zero);
        return true;
    }

    private void CreateDevice()
    {
        _device = MTLDevice.CreateSystemDefaultDevice();
        if (_device.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create the default Metal device.");
        }

        _gfxDevice = new MetalDevice(_device);
        _bindlessRegistry.EnsureInitialized(_gfxDevice);
    }

    private void CreateCommandQueue()
    {
        _commandQueue = _device.NewCommandQueue();
        if (_commandQueue.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create a Metal command queue.");
        }
    }

    private void CreateDepthState()
    {
        var descriptor = new MTLDepthStencilDescriptor();
        descriptor.DepthCompareFunction = MTLCompareFunction.Less;
        descriptor.DepthWriteEnabled = true;

        _depthState = _device.NewDepthStencilState(descriptor);
        descriptor.Dispose();
        if (_depthState.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create depth-stencil state.");
        }
    }

    private void UpdateDrawableSize()
    {
        if (_window is null)
        {
            return;
        }

        int drawableWidth = 0;
        int drawableHeight = 0;
        _sdl.MetalGetDrawableSize(_window, ref drawableWidth, ref drawableHeight);

        if (drawableWidth <= 0 || drawableHeight <= 0)
        {
            return;
        }

        if (_hasDrawableSize && NearlyEqual(drawableWidth, _drawableWidth) && NearlyEqual(drawableHeight, _drawableHeight))
        {
            return;
        }

        if (_gfxDevice is MetalDevice metalDevice)
        {
            metalDevice.WaitForIdle();
        }

        _drawableWidth = drawableWidth;
        _drawableHeight = drawableHeight;
        _hasDrawableSize = true;
        _skipFrameAfterResize = true;
        _needsBindlessRefresh = true;

        var size = new NSPoint(drawableWidth, drawableHeight);
        ObjCNative.ObjcMsgSendDrawableSize(_metalLayer.NativePtr, DrawableSizeSelector.SelPtr, size);

        var sizeInt2 = new Int2(drawableWidth, drawableHeight);
        Screen.CurrentResolution = sizeInt2;

        CreateDepthTexture(drawableWidth, drawableHeight);
        if (_gfxDevice.GlobalTable is MetalDescriptorTable metalTable)
        {
            metalTable.MarkDirty();
            metalTable.ForceEncodeForFrames(2);
        }
    }

    internal bool ConsumeBindlessRefresh()
    {
        if (_needsBindlessRefresh == false)
        {
            return false;
        }

        _needsBindlessRefresh = false;
        return true;
    }

    internal bool ConsumePackedGeometryRefresh()
    {
        if (_needsPackedGeometryReencode == false)
        {
            return false;
        }

        _needsPackedGeometryReencode = false;
        return true;
    }

    private void CreateDepthTexture(int width, int height)
    {
        if (_device.NativePtr == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        if (_depthTexture.NativePtr != IntPtr.Zero)
        {
            _depthTexture.Dispose();
            _depthTexture = default;
        }

        var descriptor = new MTLTextureDescriptor();
        descriptor.TextureType = MTLTextureType.Type2D;
        descriptor.PixelFormat = MTLPixelFormat.Depth32Float;
        descriptor.Width = (ulong)width;
        descriptor.Height = (ulong)height;
        descriptor.MipmapLevelCount = 1;
        descriptor.SampleCount = 1;
        descriptor.StorageMode = MTLStorageMode.Private;
        descriptor.Usage = MTLTextureUsage.RenderTarget;

        _depthTexture = _device.NewTexture(descriptor);
        descriptor.Dispose();
        if (_depthTexture.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create depth texture.");
        }
    }

    private void Shutdown()
    {
        if (_isGpuCaptureActive)
        {
            TryStopGpuCapture(out _);
        }

        if (_depthTexture.NativePtr != IntPtr.Zero)
        {
            _depthTexture.Dispose();
            _depthTexture = default;
        }

        if (_depthState.NativePtr != IntPtr.Zero)
        {
            _depthState.Dispose();
            _depthState = default;
        }

        (_packedVertexBuffer as IDisposable)?.Dispose();
        (_packedIndexBuffer as IDisposable)?.Dispose();
        _bindlessRegistry.UnregisterBuffer(_packedVertexBuffer);
        _bindlessRegistry.UnregisterBuffer(_packedIndexBuffer);
        _packedVertexBuffer = null;
        _packedIndexBuffer = null;
        _packedVertexBufferUsedBytes = 0;
        _packedIndexBufferUsedBytes = 0;
        _meshResources.Clear();

        if (_metalView is not null)
        {
            _sdl.MetalDestroyView(_metalView);
            _metalView = null;
        }

        if (_window is not null)
        {
            _sdl.DestroyWindow(_window);
            _window = null;
        }

        _sdl.Quit();
    }

    public IMaterialResources CreateMaterialResources(Material material)
    {
        var materialParams = new MaterialParams
        {
            BaseColor = material.Color,
            MetallicRoughnessFactor = new(material.MetallicFactor, material.RoughnessFactor),
            Padding = Vector2.Zero
        };
        var colorBufferLength = (ulong)Marshal.SizeOf<MaterialParams>();
        var colorBuffer = _device.NewBuffer(colorBufferLength, MTLResourceOptions.ResourceStorageModeShared);
        if (colorBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate material buffer.");
        }
        BufferHelper.CopyToBuffer(new[] { materialParams }, colorBuffer);

        var renderState = new RenderStateDescriptor(
            FillMode.Solid,
            CullMode.Back,
            depthTestEnabled: true,
            depthWriteEnabled: true,
            AbstractionBlendMode.Opaque);

        var pipelineKey = new PipelineKey(
            PassKind.Graphics,
            vertexEntryPoint: "vertexShader",
            pixelEntryPoint: "fragmentShader",
            computeEntryPoint: null,
            renderTargets: new(new[]
            {
                TextureFormat.Bgra8Unorm,
                TextureFormat.Rgba16Float,
                TextureFormat.Rgba8Unorm,
                TextureFormat.Rgba8Unorm
            }),
            depthStencil: new DepthStencilFormat(TextureFormat.D32Float),
            renderState: renderState,
            layout: GraphicsLayoutKind.Material);

        var shaderSource = _shaderCompiler.GetMetalSource(material.ShaderPath);
        var shaderBytes = Encoding.UTF8.GetBytes(shaderSource);
        var pipeline = _gfxDevice.GetOrCreatePipeline(pipelineKey, new ShaderBytecodeSet(shaderBytes, shaderBytes));
        var constantBuffer = new MetalBuffer($"{material.ShaderPath}_ColorBuffer", new(colorBufferLength, BufferUsage.Constant), colorBuffer);

        if (_linearSamplerHandle.IsValid == false)
        {
            var sampler = new SamplerDescriptor(FilterMode.Trilinear, AddressMode.Wrap, AddressMode.Wrap, AddressMode.Wrap);
            _linearSamplerHandle = _bindlessRegistry.GetSamplerHandle(sampler);
        }

        return new MtlMaterialResources
        {
            Pipeline = pipeline,
            ConstantBuffer = constantBuffer,
            PipelineState = default,
            ColorBuffer = colorBuffer,
            AlbedoTexture = _bindlessRegistry.GetTextureHandle(material.AlbedoTexture?.Resources),
            MetallicRoughnessTexture = _bindlessRegistry.GetTextureHandle(material.MetallicRoughnessTexture?.Resources),
            NormalTexture = _bindlessRegistry.GetTextureHandle(material.NormalTexture?.Resources),
            OcclusionTexture = _bindlessRegistry.GetTextureHandle(material.OcclusionTexture?.Resources),
            EmissiveTexture = _bindlessRegistry.GetTextureHandle(material.EmissiveTexture?.Resources),
            Sampler = _linearSamplerHandle
        };
    }

    public ITextureResources CreateTextureResources(Texture texture)
    {
        if (texture is null)
        {
            throw new ArgumentNullException(nameof(texture));
        }

        if (texture.PixelData is null || texture.PixelData.Length == 0)
        {
            throw new ArgumentException("Texture must contain pixel data.", nameof(texture));
        }

        var descriptor = new TextureDescriptor(
            texture.Width,
            texture.Height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.ShaderResource);

        var gfxTexture = _gfxDevice.CreateTexture(descriptor);
        if (gfxTexture is not MetalTexture metalTexture)
        {
            throw new InvalidOperationException("Metal renderer expected a Metal texture.");
        }

        UploadTextureData(metalTexture.Texture, texture);

        _bindlessRegistry.RegisterTexture(metalTexture);

        return new MetalTextureResources
        {
            Texture = metalTexture,
            ShaderResourceView = metalTexture.ShaderResourceView
        };
    }

    public IGfxDevice GetGfxDevice()
    {
        return _gfxDevice;
    }

    public Int2 GetFrameBufferSize()
    {
        var width = _hasDrawableSize ? (int)_drawableWidth : _width;
        var height = _hasDrawableSize ? (int)_drawableHeight : _height;
        return new Int2(width, height);
    }

    public Int2 GetWindowSize()
    {
        if (_window is null)
        {
            return new Int2(_width, _height);
        }

        int windowWidth = 0;
        int windowHeight = 0;
        _sdl.GetWindowSize(_window, ref windowWidth, ref windowHeight);
        if (windowWidth <= 0 || windowHeight <= 0)
        {
            return new Int2(_width, _height);
        }

        return new Int2(windowWidth, windowHeight);
    }

    public void BeginFrame()
    {
        var desiredVSync = Screen.VSyncEnabled;
        if (_vsyncEnabled != desiredVSync)
        {
            _metalLayer.DisplaySyncEnabled = desiredVSync;
            _vsyncEnabled = desiredVSync;
        }

        UpdateDrawableSize();
    }

    internal bool ConsumeResizeSkip()
    {
        if (_skipFrameAfterResize == false)
        {
            return false;
        }

        _skipFrameAfterResize = false;
        return true;
    }

    public void Render(
        RenderGraphResourceRegistry resourceRegistry,
        RenderGraphResourceHandle finalColor)
    {
        var finalColorTexture = resourceRegistry.GetTexture(finalColor) as MetalTexture;
        if (finalColorTexture is null || finalColorTexture.Texture.NativePtr == IntPtr.Zero)
        {
            return;
        }

        nint drawablePtr;
        using (FrameProfiler.Instance.Measure("AcquireDrawableLate"))
        {
            drawablePtr = ObjectiveC.IntPtr_objc_msgSend(_metalLayer.NativePtr, NextDrawableSelector);
        }

        if (drawablePtr == IntPtr.Zero)
        {
            if (_presentFrameIndex - _lastDrawableAcquireFailureLogFrame >= 120)
            {
                Console.WriteLine("Metal late present: nextDrawable unavailable; skipping present this frame.");
                _lastDrawableAcquireFailureLogFrame = _presentFrameIndex;
            }

            _presentFrameIndex++;
            return;
        }

        using (FrameProfiler.Instance.Measure("PresentCopy"))
        {
            var drawable = new CAMetalDrawable(drawablePtr);
            var destination = drawable.Texture;
            if (destination.NativePtr != IntPtr.Zero)
            {
                var source = finalColorTexture.Texture;
                var width = Math.Min(source.Width, destination.Width);
                var height = Math.Min(source.Height, destination.Height);

                if (width > 0 && height > 0)
                {
                    var commandList = _gfxDevice.BeginGraphics() as MetalCommandList;
                    if (commandList is not null)
                    {
                        commandList.CopyTexture(source, destination, (uint)width, (uint)height);
                        commandList.SetPresentDrawable(drawable);
                        _gfxDevice.Submit(commandList);
                    }
                }
            }
        }

        _presentFrameIndex++;
    }

    public RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height)
    {
        var drawablePtr = ObjectiveC.IntPtr_objc_msgSend(_metalLayer.NativePtr, NextDrawableSelector);
        if (drawablePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to acquire CAMetalDrawable.");
        }

        var drawable = new CAMetalDrawable(drawablePtr);
        var drawableTexture = drawable.Texture;
        var drawableWidth = (int)drawableTexture.Width;
        var drawableHeight = (int)drawableTexture.Height;

        var descriptor = new TextureDescriptor(
            Math.Max(drawableWidth, 1),
            Math.Max(drawableHeight, 1),
            TextureFormat.Bgra8Unorm,
            TextureUsage.RenderTarget,
            Vector4.Zero);

        var backbuffer = new MetalBackbufferTexture(drawable, descriptor);
        return registry.ImportTexture(backbuffer, takeOwnership: false, initialState: ResourceState.RenderTarget);
    }
    
    private static void UploadTextureData(MTLTexture texture, Texture source)
    {
        var bytesPerPixel = GetBytesPerPixel(texture.PixelFormat);
        var expectedBytes = source.Width * source.Height * bytesPerPixel;
        if (source.PixelData.Length < expectedBytes)
        {
            throw new ArgumentException(
                $"Texture pixel data is smaller than the expected size for {texture.PixelFormat}.",
                nameof(source));
        }

        var origin = new MTLOrigin { x = 0, y = 0, z = 0 };
        var size = new MTLSize { width = (ulong)source.Width, height = (ulong)source.Height, depth = 1 };
        var region = new MTLRegion { origin = origin, size = size };
        var bytesPerRow = (ulong)(source.Width * bytesPerPixel);

        fixed (byte* ptr = source.PixelData)
        {
            texture.ReplaceRegion(region, 0, (IntPtr)ptr, bytesPerRow);
        }
    }

    private static int GetBytesPerPixel(MTLPixelFormat format) => format switch
    {
        MTLPixelFormat.RGBA8Unorm => 4,
        MTLPixelFormat.BGRA8Unorm => 4,
        MTLPixelFormat.RGBA8UnormsRGB => 4,
        MTLPixelFormat.BGRA8UnormsRGB => 4,
        MTLPixelFormat.RGBA16Float => 8,
        MTLPixelFormat.RGBA32Float => 16,
        _ => throw new InvalidOperationException($"Unsupported pixel format for upload: {format}.")
    };

    private void EnsurePackedGeometryBuffers()
    {
        if (_packedVertexBuffer is null)
        {
            var vertexBuffer = _device.NewBuffer(DefaultPackedVertexBufferBytes, MTLResourceOptions.ResourceStorageModeShared);
            if (vertexBuffer.NativePtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to allocate packed vertex buffer.");
            }

            _packedVertexBuffer = new MetalBuffer("PackedMeshVertexBuffer",
                new BufferDescriptor(DefaultPackedVertexBufferBytes, BufferUsage.Vertex), vertexBuffer);
            _bindlessRegistry.RegisterBuffer(_packedVertexBuffer);
        }

        if (_packedIndexBuffer is null)
        {
            var indexBuffer = _device.NewBuffer(DefaultPackedIndexBufferBytes, MTLResourceOptions.ResourceStorageModeShared);
            if (indexBuffer.NativePtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to allocate packed index buffer.");
            }

            _packedIndexBuffer = new MetalBuffer("PackedMeshIndexBuffer",
                new BufferDescriptor(DefaultPackedIndexBufferBytes, BufferUsage.Index), indexBuffer);
            _bindlessRegistry.RegisterBuffer(_packedIndexBuffer);
        }
    }

    private static ulong GrowCapacity(ulong currentCapacity, ulong requiredCapacity, ulong minimumCapacity)
    {
        var capacity = currentCapacity > 0 ? currentCapacity : minimumCapacity;
        while (capacity < requiredCapacity)
        {
            capacity = checked(capacity * 2);
        }

        return capacity;
    }

    private void LogPackedCapacityLimitOnce(
        ulong requiredVertexBytes,
        ulong requiredIndexBytes,
        ulong targetVertexCapacity,
        ulong targetIndexCapacity)
    {
        if (_loggedPackedCapacityLimit)
        {
            return;
        }

        _loggedPackedCapacityLimit = true;
        Console.WriteLine(
            $"Packed geometry capacity cap reached. requiredVertexBytes={requiredVertexBytes}, requiredIndexBytes={requiredIndexBytes}, targetVertexCapacity={targetVertexCapacity}, targetIndexCapacity={targetIndexCapacity}, maxVertexBytes={MaxPackedVertexBufferBytes}, maxIndexBytes={MaxPackedIndexBufferBytes}.");
    }

    private static unsafe void CopyBufferBytes(MTLBuffer source, MTLBuffer destination, ulong byteCount)
    {
        if (byteCount == 0)
        {
            return;
        }

        var copyBytes = checked((int)byteCount);
        var src = new ReadOnlySpan<byte>((byte*)source.Contents.ToPointer(), copyBytes);
        var dst = new Span<byte>((byte*)destination.Contents.ToPointer(), copyBytes);
        src.CopyTo(dst);
    }

    private bool EnsurePackedGeometryCapacity(ulong requiredVertexBytes, ulong requiredIndexBytes)
    {
        EnsurePackedGeometryBuffers();
        if (_packedVertexBuffer is null || _packedIndexBuffer is null)
        {
            throw new InvalidOperationException("Packed geometry buffers are unavailable.");
        }

        var currentVertexCapacity = _packedVertexBuffer.Descriptor.SizeInBytes;
        var currentIndexCapacity = _packedIndexBuffer.Descriptor.SizeInBytes;
        var targetVertexCapacity = GrowCapacity(currentVertexCapacity, requiredVertexBytes, DefaultPackedVertexBufferBytes);
        var targetIndexCapacity = GrowCapacity(currentIndexCapacity, requiredIndexBytes, DefaultPackedIndexBufferBytes);
        if (targetVertexCapacity > MaxPackedVertexBufferBytes || targetIndexCapacity > MaxPackedIndexBufferBytes)
        {
            LogPackedCapacityLimitOnce(requiredVertexBytes, requiredIndexBytes, targetVertexCapacity, targetIndexCapacity);
            _hardeningStats.IncrementPackedCapacityFailures();
            return false;
        }

        var growVertex = targetVertexCapacity > currentVertexCapacity;
        var growIndex = targetIndexCapacity > currentIndexCapacity;
        if (growVertex == false && growIndex == false)
        {
            return true;
        }

        _gfxDevice.WaitForIdle();
        MTLBuffer newVertexMetalBuffer = default;
        MTLBuffer newIndexMetalBuffer = default;
        MetalBuffer? newVertexBuffer = null;
        MetalBuffer? newIndexBuffer = null;
        var vertexPublished = false;
        var indexPublished = false;
        try
        {
            if (growVertex)
            {
                newVertexMetalBuffer = _device.NewBuffer(targetVertexCapacity, MTLResourceOptions.ResourceStorageModeShared);
                if (newVertexMetalBuffer.NativePtr == IntPtr.Zero)
                {
                    _hardeningStats.IncrementPackedCapacityFailures();
                    return false;
                }

                CopyBufferBytes(_packedVertexBuffer.Buffer, newVertexMetalBuffer, _packedVertexBufferUsedBytes);
                newVertexBuffer = new MetalBuffer(
                    "PackedMeshVertexBuffer",
                    new BufferDescriptor(targetVertexCapacity, BufferUsage.Vertex),
                    newVertexMetalBuffer);
            }

            if (growIndex)
            {
                newIndexMetalBuffer = _device.NewBuffer(targetIndexCapacity, MTLResourceOptions.ResourceStorageModeShared);
                if (newIndexMetalBuffer.NativePtr == IntPtr.Zero)
                {
                    _hardeningStats.IncrementPackedCapacityFailures();
                    return false;
                }

                CopyBufferBytes(_packedIndexBuffer.Buffer, newIndexMetalBuffer, _packedIndexBufferUsedBytes);
                newIndexBuffer = new MetalBuffer(
                    "PackedMeshIndexBuffer",
                    new BufferDescriptor(targetIndexCapacity, BufferUsage.Index),
                    newIndexMetalBuffer);
            }

            if (newVertexBuffer is not null)
            {
                foreach (var mesh in _meshResources.Keys)
                {
                    mesh.VertexBuffer = newVertexBuffer;
                }

                _bindlessRegistry.RegisterBuffer(newVertexBuffer);
                _bindlessRegistry.UnregisterBuffer(_packedVertexBuffer);
                (_packedVertexBuffer as IDisposable)?.Dispose();
                _packedVertexBuffer = newVertexBuffer;
                vertexPublished = true;
            }

            if (newIndexBuffer is not null)
            {
                foreach (var mesh in _meshResources.Keys)
                {
                    mesh.IndexBuffer = newIndexBuffer;
                }

                _bindlessRegistry.RegisterBuffer(newIndexBuffer);
                _bindlessRegistry.UnregisterBuffer(_packedIndexBuffer);
                (_packedIndexBuffer as IDisposable)?.Dispose();
                _packedIndexBuffer = newIndexBuffer;
                indexPublished = true;
            }

            _needsPackedGeometryReencode = true;
            return true;
        }
        catch
        {
            _hardeningStats.IncrementPackedCapacityFailures();
            throw;
        }
        finally
        {
            // On transactional failure we keep old packed buffers alive and release temporary allocations.
            if (vertexPublished == false && newVertexBuffer is not null)
            {
                newVertexBuffer.Dispose();
            }
            else if (vertexPublished == false && newVertexMetalBuffer.NativePtr != IntPtr.Zero)
            {
                newVertexMetalBuffer.Dispose();
            }

            if (indexPublished == false && newIndexBuffer is not null)
            {
                newIndexBuffer.Dispose();
            }
            else if (indexPublished == false && newIndexMetalBuffer.NativePtr != IntPtr.Zero)
            {
                newIndexMetalBuffer.Dispose();
            }
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment == 0)
        {
            return value;
        }

        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    private static unsafe void CopyToBufferAtOffset<T>(ReadOnlySpan<T> source, MTLBuffer buffer, ulong byteOffset) where T : unmanaged
    {
        if (source.IsEmpty)
        {
            return;
        }

        var bytes = MemoryMarshal.AsBytes(source);
        var destination = new Span<byte>((byte*)buffer.Contents.ToPointer() + (nint)byteOffset, bytes.Length);
        bytes.CopyTo(destination);
    }

    private MeshResources UploadMesh(Mesh mesh)
    {
        if (mesh.Vertices.Length == 0)
        {
            throw new InvalidOperationException("Mesh must contain vertex data.");
        }

        if (mesh.Normals.Length != mesh.Vertices.Length ||
            mesh.UVs.Length != mesh.Vertices.Length ||
            mesh.Tangents.Length != mesh.Vertices.Length)
        {
            throw new InvalidOperationException("Mesh must contain normals, UVs, and tangents per vertex.");
        }

        EnsurePackedGeometryBuffers();

        var vertexCount = mesh.Vertices.Length;
        var vertexData = new VertexData[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var normal = mesh.Normals[i];
            vertexData[i] = new VertexData
            {
                Position = mesh.Vertices[i],
                Normal = normal,
                UV = mesh.UVs[i],
                Tangent = mesh.Tangents[i]
            };
        }

        var vertexStrideBytes = (ulong)Marshal.SizeOf<VertexData>();
        var vertexBufferLength = (ulong)(vertexData.Length * Marshal.SizeOf<VertexData>());
        var indexBufferLength = (ulong)(mesh.Indices.Length * sizeof(uint));

        if (_packedVertexBuffer is null || _packedIndexBuffer is null)
        {
            throw new InvalidOperationException("Packed mesh geometry buffers were not initialized.");
        }

        // baseVertex addressing requires offsets aligned to full vertex strides.
        var vertexOffsetBytes = AlignUp(_packedVertexBufferUsedBytes, vertexStrideBytes);
        var indexOffsetBytes = AlignUp(_packedIndexBufferUsedBytes, sizeof(uint));
        if (EnsurePackedGeometryCapacity(vertexOffsetBytes + vertexBufferLength, indexOffsetBytes + indexBufferLength) == false)
        {
            _hardeningStats.IncrementFallbackProxySubstitutions();
            throw new InvalidOperationException(
                $"Packed geometry capacity exceeded for mesh upload. requiredVertexBytes={vertexOffsetBytes + vertexBufferLength}, requiredIndexBytes={indexOffsetBytes + indexBufferLength}.");
        }
        if (_packedVertexBuffer is null || _packedIndexBuffer is null)
        {
            throw new InvalidOperationException("Packed geometry buffers were lost during growth.");
        }

        CopyToBufferAtOffset<VertexData>(vertexData, _packedVertexBuffer.Buffer, vertexOffsetBytes);
        CopyToBufferAtOffset<uint>(mesh.Indices, _packedIndexBuffer.Buffer, indexOffsetBytes);

        mesh.VertexBuffer = _packedVertexBuffer;
        mesh.IndexBuffer = _packedIndexBuffer;
        mesh.StrideInBytes = (uint)vertexStrideBytes;
        mesh.IndexCount = (uint)mesh.Indices.Length;
        mesh.PackedVertexOffsetBytes = vertexOffsetBytes;
        mesh.PackedIndexOffsetBytes = indexOffsetBytes;
        mesh.PackedBaseVertex = checked((int)(vertexOffsetBytes / vertexStrideBytes));

        _packedVertexBufferUsedBytes = vertexOffsetBytes + vertexBufferLength;
        _packedIndexBufferUsedBytes = indexOffsetBytes + indexBufferLength;

        return new MeshResources(vertexOffsetBytes, indexOffsetBytes, mesh.PackedBaseVertex, (ulong)mesh.Indices.Length);
    }

    public void EnsureMeshResources(Mesh mesh)
    {
        if (_meshResources.TryGetValue(mesh, out var resources) == false)
        {
            try
            {
                resources = UploadMesh(mesh);
            }
            catch (InvalidOperationException ex)
            {
                _hardeningStats.IncrementPackedCapacityFailures();
                _hardeningStats.IncrementFallbackProxySubstitutions();
                EnsurePackedGeometryBuffers();
                if (_packedVertexBuffer is null || _packedIndexBuffer is null)
                {
                    throw;
                }

                mesh.VertexBuffer = _packedVertexBuffer;
                mesh.IndexBuffer = _packedIndexBuffer;
                mesh.StrideInBytes = (uint)Marshal.SizeOf<VertexData>();
                mesh.IndexCount = 0;
                mesh.PackedVertexOffsetBytes = 0;
                mesh.PackedIndexOffsetBytes = 0;
                mesh.PackedBaseVertex = 0;
                resources = new MeshResources(0, 0, 0, 0);
                if (_loggedPackedCapacityLimit == false)
                {
                    _loggedPackedCapacityLimit = true;
                    Console.WriteLine($"GpuDraw packed geometry fallback proxy activated: {ex.Message}");
                }
            }

            _meshResources[mesh] = resources;
        } 
    }

    public bool SupportsGpuCapture => true;

    public bool IsGpuCaptureActive => _isGpuCaptureActive;

    public string LastGpuCapturePath => _lastGpuCapturePath;

    public bool TryStartGpuCapture(string outputPath, out string error)
    {
        if (_isGpuCaptureActive)
        {
            error = "GPU capture is already active.";
            return false;
        }

        if (_commandQueue.NativePtr == IntPtr.Zero)
        {
            error = "Metal command queue is not initialized.";
            return false;
        }

        try
        {
            var captureManager = MTLCaptureManager.SharedCaptureManager;
            if (captureManager.NativePtr == IntPtr.Zero)
            {
                error = "MTLCaptureManager is unavailable.";
                return false;
            }

            var supportsFileTrace = captureManager.SupportsDestination(MTLCaptureDestination.GPUTraceDocument);
            var supportsDeveloperTools = captureManager.SupportsDestination(MTLCaptureDestination.DeveloperTools);
            if (supportsFileTrace == false && supportsDeveloperTools == false)
            {
                error = "This system does not support Metal capture for this process.";
                return false;
            }

            var fullPath = supportsFileTrace
                ? NormalizeCapturePath(outputPath)
                : string.Empty;
            var captureDescriptor = new MTLCaptureDescriptor();
            try
            {
                captureDescriptor.Destination = supportsFileTrace
                    ? MTLCaptureDestination.GPUTraceDocument
                    : MTLCaptureDestination.DeveloperTools;

                if (supportsFileTrace)
                {
                    using var nsPath = NSStringHelper.From(fullPath);
                    captureDescriptor.OutputURL = NSURL.FileURLWithPath(nsPath);
                }
                captureDescriptor.CaptureObject = new NSObject(_commandQueue.NativePtr);

                var captureError = new NSError(IntPtr.Zero);
                var started = captureManager.StartCapture(captureDescriptor, ref captureError);
                if (started == false || captureError != IntPtr.Zero)
                {
                    var details = captureError != IntPtr.Zero
                        ? captureError.LocalizedDescription.ToManagedString()
                        : "Metal returned an unknown error while starting capture.";
                    error = $"Failed to start GPU capture: {details}";
                    return false;
                }
            }
            finally
            {
                captureDescriptor.Dispose();
            }

            _isGpuCaptureActive = true;
            _gpuCaptureWritesTraceFile = supportsFileTrace;
            _lastGpuCapturePath = supportsFileTrace ? fullPath : string.Empty;
            error = supportsFileTrace
                ? string.Empty
                : "GPU capture started with DeveloperTools destination (no .gputrace file output on this system).";
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to start GPU capture: {ex.Message}";
            return false;
        }
    }

    public bool TryStopGpuCapture(out string error)
    {
        if (_isGpuCaptureActive == false)
        {
            error = "GPU capture is not active.";
            return false;
        }

        try
        {
            var captureManager = MTLCaptureManager.SharedCaptureManager;
            if (captureManager.NativePtr == IntPtr.Zero)
            {
                error = "MTLCaptureManager is unavailable.";
                return false;
            }

            captureManager.StopCapture();
            _isGpuCaptureActive = false;
            _gpuCaptureWritesTraceFile = false;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to stop GPU capture: {ex.Message}";
            return false;
        }
    }
    
    private static bool NearlyEqual(double a, double b)
    {
        const double epsilon = 0.5;
        return Math.Abs(a - b) < epsilon;
    }

    private static string NormalizeCapturePath(string outputPath)
    {
        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Path.GetTempPath(), "WolfEngineCaptures", $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.gputrace")
            : outputPath.Trim();
        if (Path.HasExtension(path) == false)
        {
            path += ".gputrace";
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) == false)
        {
            Directory.CreateDirectory(directory);
        }

        return fullPath;
    }

    private static ulong ParsePositiveUlongEnvironmentVariable(string name, ulong fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (ulong.TryParse(raw, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return fallback;
    }
}
