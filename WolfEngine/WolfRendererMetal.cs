using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using Silk.NET.Core.Native;
using Silk.NET.SDL;
using WolfEngine.Platform;

namespace WolfEngine;

[SupportedOSPlatform("macos")]
public unsafe class WolfRendererMetal : IRenderer
{
    private const string WindowTitle = "WolfEngine";
    private const uint SdlQuitEvent = 0x100;
    private const uint SdlWindowEvent = 0x200;

    private readonly int _width;
    private readonly int _height;
    private readonly IShaderCompiler _shaderCompiler;
    private readonly ConcurrentQueue<RenderCommand> _pendingCommands = new();
    private readonly Dictionary<Mesh, MeshResources> _meshResources = new();
    private readonly Dictionary<Material, MaterialResources> _materialResources = new();
    private readonly List<DrawInstruction> _drawCommands = new();
    private readonly MTLClearColor _clearColor = new() { red = 0.392, green = 0.584, blue = 0.929, alpha = 1.0 };
    private readonly Sdl _sdl;

    private MTLDevice _device;
    private MTLCommandQueue _commandQueue;
    private Window* _window;
    private void* _metalView;
    private CAMetalLayer _metalLayer;
    private bool _isRunning;
    private bool _hasDrawableSize;
    private double _drawableWidth;
    private double _drawableHeight;
    private Action _updateCallback = static () => { };

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

    private sealed class MaterialResources
    {
        public MaterialResources(MTLRenderPipelineState pipelineState, MTLBuffer colorBuffer)
        {
            PipelineState = pipelineState;
            ColorBuffer = colorBuffer;
        }

        public MTLRenderPipelineState PipelineState { get; }

        public MTLBuffer ColorBuffer { get; }
    }

    private readonly struct DrawInstruction
    {
        public DrawInstruction(Mesh mesh, Material material, Matrix4x4 transform)
        {
            Mesh = mesh;
            Material = material;
            Transform = transform;
        }

        public Mesh Mesh { get; }

        public Material Material { get; }

        public Matrix4x4 Transform { get; }
    }

    public WolfRendererMetal(int screenWidth, int screenHeight, IShaderCompiler shaderCompiler)
    {
        if (OperatingSystem.IsMacOS() == false)
        {
            throw new PlatformNotSupportedException("Metal renderer is only supported on macOS.");
        }

        _width = screenWidth;
        _height = screenHeight;
        _shaderCompiler = shaderCompiler;

        ObjectiveC.LinkMetal();
        ObjectiveC.LinkCoreGraphics();
        ObjectiveC.LinkAppKit();
        ObjectiveC.LinkMetalKit();

        _sdl = Sdl.GetApi();
    }

    public void SubmitCommand(RenderCommand command)
    {
        _pendingCommands.Enqueue(command);
    }

    public void Run(Action update)
    {
        _updateCallback = update ?? throw new ArgumentNullException(nameof(update));

        try
        {
            CreateDevice();
            CreateCommandQueue();
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

        while (_isRunning)
        {
            PumpEvents(ref @event);

            _updateCallback();
            ProcessPendingCommands();
            var rendered = RenderFrame();

            if (rendered == false)
            {
                _sdl.Delay(1);
            }
        }
    }

    private void PumpEvents(ref Event @event)
    {
        while (_sdl.PollEvent(ref @event) != 0)
        {
            switch (@event.Type)
            {
                case SdlQuitEvent:
                    _isRunning = false;
                    break;
                case SdlWindowEvent:
                    HandleWindowEvent(@event);
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
        _metalLayer.FramebufferOnly = true;
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
    }

    private void CreateCommandQueue()
    {
        _commandQueue = _device.NewCommandQueue();
        if (_commandQueue.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create a Metal command queue.");
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
    }

    private bool RenderFrame()
    {
        if (_commandQueue.NativePtr == IntPtr.Zero)
        {
            return false;
        }

        if (_drawCommands.Count == 0)
        {
            ArenaAllocator.RenderCommands.Reset();
            return false;
        }

        UpdateDrawableSize();

        var drawablePtr = ObjectiveC.IntPtr_objc_msgSend(_metalLayer.NativePtr, NextDrawableSelector);
        if (drawablePtr == IntPtr.Zero)
        {
            return false;
        }

        using var renderPassDescriptor = new MTLRenderPassDescriptor();
        var drawable = new CAMetalDrawable(drawablePtr);

        var colorAttachment = renderPassDescriptor.ColorAttachments.Object(0);
        colorAttachment.Texture = drawable.Texture;
        colorAttachment.LoadAction = MTLLoadAction.Clear;
        colorAttachment.StoreAction = MTLStoreAction.Store;
        colorAttachment.ClearColor = _clearColor;
        renderPassDescriptor.ColorAttachments.SetObject(colorAttachment, 0);

        var commandBuffer = _commandQueue.CommandBuffer();
        var encoder = commandBuffer.RenderCommandEncoder(renderPassDescriptor);

        if (_hasDrawableSize)
        {
            var viewport = new MTLViewport
            {
                originX = 0,
                originY = 0,
                width = _drawableWidth,
                height = _drawableHeight,
                znear = 0,
                zfar = 1
            };
            encoder.SetViewport(viewport);
        }

        foreach (var drawCommand in _drawCommands)
        {
            var meshResources = EnsureMeshResources(drawCommand.Mesh);
            var materialResources = EnsureMaterialResources(drawCommand.Material);

            encoder.SetRenderPipelineState(materialResources.PipelineState);
            encoder.SetVertexBuffer(meshResources.VertexBuffer, 0, 0);
#pragma warning disable CA2014
            var transformCopy = drawCommand.Transform;
            var transformPtr = stackalloc Matrix4x4[1];
            transformPtr[0] = transformCopy;
            var transformSize = (ulong)sizeof(Matrix4x4);
            encoder.SetVertexBytes((IntPtr)transformPtr, transformSize, 1);
#pragma warning restore CA2014
            encoder.SetFragmentBuffer(materialResources.ColorBuffer, 0, 0);
            encoder.DrawIndexedPrimitives(MTLPrimitiveType.Triangle, meshResources.IndexCount, MTLIndexType.UInt32, meshResources.IndexBuffer, 0);
        }

        encoder.EndEncoding();
        commandBuffer.PresentDrawable(drawable);
        commandBuffer.Commit();

        _drawCommands.Clear();
        ArenaAllocator.RenderCommands.Reset();
        return true;
    }

    private void Shutdown()
    {
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

    private MTLLibrary CreateShaderLibrary(Material material)
    {
        var libraryError = new NSError(IntPtr.Zero);
        var shaderSource = material.ShaderSource;
        var library = _device.NewLibrary(NSStringHelper.From(shaderSource), new(IntPtr.Zero), ref libraryError);
        if (libraryError != IntPtr.Zero)
        {
            var description = libraryError.LocalizedDescription.ToManagedString("Unknown error");
            throw new Exception($"Failed to create library! {description}");
        }

        return library;
    }

    private MaterialResources CreateMaterialResources(Material material)
    {
        var library = CreateShaderLibrary(material);
        var pipeline = CreateRenderPipeline(library);

        var color = new[] { material.Color };
        var colorBufferLength = (ulong)Marshal.SizeOf<Vector4>();
        var colorBuffer = _device.NewBuffer(colorBufferLength, MTLResourceOptions.ResourceStorageModeManaged);
        if (colorBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate material buffer.");
        }
        BufferHelper.CopyToBuffer(color, colorBuffer);
        colorBuffer.DidModifyRange(new NSRange { location = 0, length = colorBufferLength });

        return new MaterialResources(pipeline, colorBuffer);
    }

    private MTLRenderPipelineState CreateRenderPipeline(MTLLibrary shaderLibrary)
    {
        var vertexShader = shaderLibrary.NewFunction(NSStringHelper.From("vertexShader"));
        var fragmentShader = shaderLibrary.NewFunction(NSStringHelper.From("fragmentShader"));

        var pipeline = new MTLRenderPipelineDescriptor();
        pipeline.VertexFunction = vertexShader;
        pipeline.FragmentFunction = fragmentShader;
        pipeline.VertexDescriptor = CreateVertexDescriptor();

        var colorAttachment = pipeline.ColorAttachments.Object(0);
        colorAttachment.PixelFormat = MTLPixelFormat.BGRA8Unorm;
        pipeline.ColorAttachments.SetObject(colorAttachment, 0);

        var pipelineStateError = new NSError(IntPtr.Zero);
        var pipelineState = _device.NewRenderPipelineState(pipeline, ref pipelineStateError);
        if (pipelineStateError != IntPtr.Zero)
        {
            throw new Exception($"Failed to create render pipeline state! {pipelineStateError.LocalizedDescription.ToManagedString()}");
        }

        return pipelineState;
    }

    private static MTLVertexDescriptor CreateVertexDescriptor()
    {
        var descriptor = new MTLVertexDescriptor();

        var attributes = descriptor.Attributes;
        var positionAttribute = attributes.Object(0);
        positionAttribute.Format = MTLVertexFormat.Float4;
        positionAttribute.Offset = 0;
        positionAttribute.BufferIndex = 0;
        attributes.SetObject(positionAttribute, 0);

        var layouts = descriptor.Layouts;
        var layout = layouts.Object(0);
        layout.Stride = (ulong)Marshal.SizeOf<Vector4>();
        layout.StepFunction = MTLVertexStepFunction.PerVertex;
        layout.StepRate = 1;
        layouts.SetObject(layout, 0);

        return descriptor;
    }

    private MeshResources UploadMesh(Mesh mesh)
    {
        if (mesh.Vertices.Length == 0)
        {
            throw new InvalidOperationException("Mesh must contain vertex data.");
        }

        var vertexBufferLength = (ulong)(mesh.Vertices.Length * Marshal.SizeOf<Vector4>());
        var vertexBuffer = _device.NewBuffer(vertexBufferLength, MTLResourceOptions.ResourceStorageModeManaged);
        if (vertexBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate vertex buffer.");
        }
        BufferHelper.CopyToBuffer(mesh.Vertices, vertexBuffer);
        vertexBuffer.DidModifyRange(new NSRange { location = 0, length = vertexBufferLength });

        var indexBufferLength = (ulong)(mesh.Indices.Length * sizeof(uint));
        var indexBuffer = _device.NewBuffer(indexBufferLength, MTLResourceOptions.ResourceStorageModeManaged);
        if (indexBuffer.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate index buffer.");
        }
        BufferHelper.CopyToBuffer(mesh.Indices, indexBuffer);
        indexBuffer.DidModifyRange(new NSRange { location = 0, length = indexBufferLength });

        return new MeshResources(vertexBuffer, indexBuffer, (ulong)mesh.Indices.Length);
    }

    private MeshResources EnsureMeshResources(Mesh mesh)
    {
        if (_meshResources.TryGetValue(mesh, out var resources) == false)
        {
            resources = UploadMesh(mesh);
            _meshResources[mesh] = resources;
        }

        return resources;
    }

    private MaterialResources EnsureMaterialResources(Material material)
    {
        if (_materialResources.TryGetValue(material, out var resources) == false)
        {
            resources = CreateMaterialResources(material);
            _materialResources[material] = resources;
        }

        return resources;
    }

    private void ProcessPendingCommands()
    {
        while (_pendingCommands.TryDequeue(out var command))
        {
            switch (command.Type)
            {
                case RenderCommandType.CreateMesh:
                    HandleCreateMeshCommand(command);
                    break;
                case RenderCommandType.CreateMaterial:
                    HandleCreateMaterialCommand(command);
                    break;
                case RenderCommandType.DrawMesh:
                    HandleDrawMeshCommand(command);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command.Type), command.Type, "Unsupported render command type.");
            }
        }
    }

    private void HandleCreateMeshCommand(RenderCommand command)
    {
        var payload = command.ReadPayload<RenderCommand.CreateMeshPayload>();
        if (payload.MeshHandle.Target is not Mesh mesh)
        {
            throw new InvalidOperationException("Mesh payload target was null.");
        }
        payload.MeshHandle.Free();
        EnsureMeshResources(mesh);
    }

    private void HandleCreateMaterialCommand(RenderCommand command)
    {
        var payload = command.ReadPayload<RenderCommand.CreateMaterialPayload>();
        if (payload.MaterialHandle.Target is not Material material)
        {
            throw new InvalidOperationException("Material payload target was null.");
        }
        payload.MaterialHandle.Free();
        EnsureMaterialResources(material);
    }

    private void HandleDrawMeshCommand(RenderCommand command)
    {
        var payload = command.ReadPayload<RenderCommand.DrawMeshPayload>();
        if (payload.MeshHandle.Target is not Mesh mesh)
        {
            throw new InvalidOperationException("Mesh payload target was null.");
        }
        if (payload.MaterialHandle.Target is not Material material)
        {
            throw new InvalidOperationException("Material payload target was null.");
        }
        payload.MeshHandle.Free();
        payload.MaterialHandle.Free();
        EnsureMeshResources(mesh);
        EnsureMaterialResources(material);
        _drawCommands.Add(new DrawInstruction(mesh, material, payload.Transform));
    }

    private static bool NearlyEqual(double a, double b)
    {
        const double epsilon = 0.5;
        return Math.Abs(a - b) < epsilon;
    }
}
