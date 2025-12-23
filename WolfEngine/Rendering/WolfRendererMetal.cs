using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using Silk.NET.Core.Native;
using Silk.NET.SDL;
using WolfEngine.Backend.Metal;
using WolfEngine.Mathematics;
using WolfEngine.Platform;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.Metal;
using AbstractionBlendMode = WolfEngine.Rendering.Abstraction.BlendMode;

namespace WolfEngine;

[SupportedOSPlatform("macos")]
internal unsafe class WolfRendererMetal : IRenderer
{
    private const string WindowTitle = "WolfEngine";

    private readonly int _width;
    private readonly int _height;
    private readonly IShaderCompiler _shaderCompiler;
    private readonly IMacOSInputHandler _inputHandler;
    private readonly Dictionary<Mesh, MeshResources> _meshResources = new();
    private MTLTexture _depthTexture;
    private MTLDepthStencilState _depthState;
    private readonly Sdl _sdl;

    private MTLDevice _device;
    private MTLCommandQueue _commandQueue;
    private MetalDevice _gfxDevice;
    private Window* _window;
    private void* _metalView;
    private CAMetalLayer _metalLayer;
    private CAMetalDrawable _currentDrawable;
    private bool _isRunning;
    private bool _hasDrawableSize;
    private double _drawableWidth;
    private double _drawableHeight;
    private DescriptorHandle _linearSamplerHandle = DescriptorHandle.Invalid;
    private Action _startupCallback = static () => { };
    private Action<float> _updateCallback = static deltaTime => { };
    private Action<float> _renderCallback = static deltaTime => { };


    private static readonly Selector NextDrawableSelector = new("nextDrawable");
    private static readonly Selector DrawableSizeSelector = new("setDrawableSize:");

    private sealed class MeshResources
    {
        public MeshResources(MTLBuffer vertexBuffer, MTLBuffer indexBuffer, ulong indexCount)
        {
            VertexBuffer = vertexBuffer;
            IndexBuffer = indexBuffer;
            IndexCount = indexCount;
        }

        public MTLBuffer VertexBuffer { get; }

        public MTLBuffer IndexBuffer { get; }

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

    public WolfRendererMetal(IShaderCompiler shaderCompiler, IMacOSInputHandler inputHandler)
    {
        _width = 1280;
        _height = 720;
        _shaderCompiler = shaderCompiler;
        _inputHandler = inputHandler;

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
        _metalLayer.DisplaySyncEnabled = true;

        UpdateDrawableSize();
    }

    private void CreateDevice()
    {
        _device = MTLDevice.CreateSystemDefaultDevice();
        if (_device.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create the default Metal device.");
        }

        _gfxDevice = new MetalDevice(_device);
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

        _drawableWidth = drawableWidth;
        _drawableHeight = drawableHeight;
        _hasDrawableSize = true;

        var size = new NSPoint(drawableWidth, drawableHeight);
        ObjCNative.ObjcMsgSendDrawableSize(_metalLayer.NativePtr, DrawableSizeSelector.SelPtr, size);

        if (_gfxDevice is ITexturePoolDevice poolDevice)
        {
            poolDevice.ClearTexturePool();
        }

        CreateDepthTexture(drawableWidth, drawableHeight);
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
        var color = new[] { material.Color };
        var colorBufferLength = (ulong)Marshal.SizeOf<Vector4>();
        var colorBuffer = _device.NewBuffer(colorBufferLength, MTLResourceOptions.ResourceStorageModeShared);
        if (colorBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate material buffer.");
        }
        BufferHelper.CopyToBuffer(color, colorBuffer);

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
            _linearSamplerHandle = _gfxDevice.GlobalTable.AllocateSampler(sampler);
        }

        return new MtlMaterialResources
        {
            Pipeline = pipeline,
            ConstantBuffer = constantBuffer,
            PipelineState = default,
            ColorBuffer = colorBuffer,
            AlbedoTexture = material.AlbedoTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
            MetallicRoughnessTexture = material.MetallicRoughnessTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
            NormalTexture = material.NormalTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
            OcclusionTexture = material.OcclusionTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
            EmissiveTexture = material.EmissiveTexture?.Resources?.ShaderResourceView ?? DescriptorHandle.Invalid,
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
        UpdateDrawableSize();
    }

    public void Render(
        RenderGraphResourceRegistry resourceRegistry,
        RenderGraphResourceHandle backBuffer)
    {
        // TODO: DirectX needs this method but Metal does not, can that be more elegant?
    }

    public RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry, int width, int height)
    {
        var drawablePtr = ObjectiveC.IntPtr_objc_msgSend(_metalLayer.NativePtr, NextDrawableSelector);
        if (drawablePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to acquire CAMetalDrawable.");
        }

        _currentDrawable = new CAMetalDrawable(drawablePtr);

        var descriptor = new TextureDescriptor(
            Math.Max(width, 1),
            Math.Max(height, 1),
            TextureFormat.Bgra8Unorm,
            TextureUsage.RenderTarget,
            Vector4.Zero);

        var backbuffer = new MetalBackbufferTexture(_currentDrawable, descriptor);
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

        var vertexBufferLength = (ulong)(vertexData.Length * Marshal.SizeOf<VertexData>());
        var vertexBuffer = _device.NewBuffer(vertexBufferLength, MTLResourceOptions.ResourceStorageModeShared);
        if (vertexBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate vertex buffer.");
        }
        BufferHelper.CopyToBuffer(vertexData, vertexBuffer);

        var indexBufferLength = (ulong)(mesh.Indices.Length * sizeof(uint));
        var indexBuffer = _device.NewBuffer(indexBufferLength, MTLResourceOptions.ResourceStorageModeShared);
        if (indexBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate index buffer.");
        }
        BufferHelper.CopyToBuffer(mesh.Indices, indexBuffer);

        var vertexBufferAbstraction = new MetalBuffer("MeshVertexBuffer", new(vertexBufferLength, BufferUsage.Vertex), vertexBuffer);
        var indexBufferAbstraction = new MetalBuffer("MeshIndexBuffer", new(indexBufferLength, BufferUsage.Index), indexBuffer);

        mesh.VertexBuffer = vertexBufferAbstraction;
        mesh.IndexBuffer = indexBufferAbstraction;
        mesh.StrideInBytes = (uint)Marshal.SizeOf<VertexData>();
        mesh.IndexCount = (uint)mesh.Indices.Length;

        return new MeshResources(vertexBuffer, indexBuffer, (ulong)mesh.Indices.Length);
    }

    public void EnsureMeshResources(Mesh mesh)
    {
        if (_meshResources.TryGetValue(mesh, out var resources) == false)
        {
            resources = UploadMesh(mesh);
            _meshResources[mesh] = resources;
        } 
    }
    
    private static bool NearlyEqual(double a, double b)
    {
        const double epsilon = 0.5;
        return Math.Abs(a - b) < epsilon;
    }
}