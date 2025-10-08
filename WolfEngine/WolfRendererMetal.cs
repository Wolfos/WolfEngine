using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using WolfEngine.Platform;

namespace WolfEngine;

[SupportedOSPlatform("macos")]
public class WolfRendererMetal: IRenderer
{
    private readonly int _width;
    private readonly int _height;
    private readonly IShaderCompiler _shaderCompiler;

    private readonly NSApplicationInstance _application;
    private readonly MetalAppDelegate _appDelegate;

    private NSWindowInstance _window;
    private MTKViewInstance _view;
    private MetalViewDelegate _viewDelegate;
    private NSWindowDelegate _windowDelegate;
    private NSMenu _mainMenu;
    private NSMenuItem _appMenuItem;
    private NSMenu _appMenu;
    private NSMenuItem _quitMenuItem;
    private MTLDevice _device;
    private MTLCommandQueue _commandQueue;
    private readonly MTLClearColor _clearColor = new() { red = 0.392, green = 0.584, blue = 0.929, alpha = 1.0 };
    private bool _isUpdatingDrawableSize;
    private bool _hasDrawableSize;
    private double _drawableWidth;
    private double _drawableHeight;

    private const string WindowTitle = "WolfEngine";
    private readonly ConcurrentQueue<RenderCommand> _pendingCommands = new();
    private readonly Dictionary<Mesh, MeshResources> _meshResources = new();
    private readonly Dictionary<Material, MaterialResources> _materialResources = new();
    private readonly List<DrawInstruction> _drawCommands = new();
    private Action _updateCallback = static () => { };

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
        if (!OperatingSystem.IsMacOS())
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

        _application = new NSApplicationInstance();
        _appDelegate = new MetalAppDelegate();
        _appDelegate.WillFinishLaunching += OnApplicationWillFinishLaunching;
        _appDelegate.DidFinishLaunching += OnApplicationDidFinishLaunching;

        _application.SetDelegate(_appDelegate);
    }

	public void SubmitCommand(RenderCommand command)
	{
		_pendingCommands.Enqueue(command);
	}

	public void Run(Action update)
	{
		_updateCallback = update ?? throw new ArgumentNullException(nameof(update));
		_application.Run();
	}

    private void OnApplicationWillFinishLaunching(NSNotification notification)
    {
        var app = new NSApplicationInstance(notification.Object);
        app.SetActivationPolicy(NSApplicationActivationPolicy.Regular);
    }

    private void OnApplicationDidFinishLaunching(NSNotification notification)
    {
        CreateDevice();
        
        CreateView();
        SetupMenu();
        CreateCommandQueue();

        ProcessPendingCommands();
        ArenaAllocator.RenderCommands.Reset();

        var app = new NSApplicationInstance(notification.Object);
        app.ActivateIgnoringOtherApps(true);
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
	    colorAttachment.PixelFormat = MTLPixelFormat.BGRA8UnormsRGB;
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

    private void CreateView()
    {
	    var contentRect = new NSRect(0, 0, _width, _height);
	    var style = NSWindowStyleMask.Titled | NSWindowStyleMask.Resizable | NSWindowStyleMask.Closable | NSWindowStyleMask.Miniaturizable;
	    _window = new NSWindowInstance(contentRect, (ulong)style);
	    _view = new MTKViewInstance(contentRect, _device)
	    {
		    ColorPixelFormat = MTLPixelFormat.BGRA8Unorm,
		    Paused = false,
		    PreferredFramesPerSecond = 120
	    };

	    _viewDelegate = new MetalViewDelegate();
	    _viewDelegate.DrawInMTKView += Draw;
	    _viewDelegate.DrawableSizeWillChange += ResizeDrawable;
	    _view.Delegate = _viewDelegate;
	    
	    _window.SetContentView(_view.NativePtr);
	    _window.SetTitle(WindowTitle);
	    _window.MakeKeyAndOrderFront();

	    _windowDelegate = new NSWindowDelegate();
	    _windowDelegate.WindowWillClose += OnWindowWillClose;
	    _window.SetDelegate(_windowDelegate);
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
		var colorBufferLength = (ulong)(Marshal.SizeOf<Vector4>());
		var colorBuffer = _device.NewBuffer(colorBufferLength, MTLResourceOptions.ResourceStorageModeManaged);
		if (colorBuffer.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to allocate material buffer.");
		}
		BufferHelper.CopyToBuffer(color, colorBuffer);
		colorBuffer.DidModifyRange(new NSRange { location = 0, length = colorBufferLength });

		return new MaterialResources(pipeline, colorBuffer);
	}

	private MeshResources EnsureMeshResources(Mesh mesh)
	{
		if (!_meshResources.TryGetValue(mesh, out var resources))
		{
			resources = UploadMesh(mesh);
			_meshResources[mesh] = resources;
		}

		return resources;
	}

	private MaterialResources EnsureMaterialResources(Material material)
	{
		if (!_materialResources.TryGetValue(material, out var resources))
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

	private void Draw(MTKViewInstance view)
	{
		_updateCallback();
		ProcessPendingCommands();

		if (_commandQueue.NativePtr == IntPtr.Zero)
		{
			Cleanup();
			return;
		}

		if (_drawCommands.Count == 0)
		{
			Cleanup();
			return;
		}

		var renderPassDescriptor = view.CurrentRenderPassDescriptor;
		if (renderPassDescriptor.NativePtr == IntPtr.Zero)
		{
			Cleanup();
			return;
		}

		var colorAttachment = renderPassDescriptor.ColorAttachments.Object(0);
		colorAttachment.LoadAction = MTLLoadAction.Clear;
		colorAttachment.StoreAction = MTLStoreAction.Store;
		colorAttachment.ClearColor = _clearColor;
		renderPassDescriptor.ColorAttachments.SetObject(colorAttachment, 0);

		var drawable = view.CurrentDrawable;
		if (drawable.NativePtr == IntPtr.Zero)
		{
			Cleanup();
			return;
		}

		var commandBuffer = _commandQueue.CommandBuffer();
		var encoder = commandBuffer.RenderCommandEncoder(renderPassDescriptor);

		foreach (var drawCommand in _drawCommands)
		{
			var meshResources = EnsureMeshResources(drawCommand.Mesh);
			var materialResources = EnsureMaterialResources(drawCommand.Material);

			encoder.SetRenderPipelineState(materialResources.PipelineState);
			encoder.SetVertexBuffer(meshResources.VertexBuffer, 0, 0);
			unsafe
			{
			    var transformCopy = drawCommand.Transform;
			    var transformPtr = stackalloc Matrix4x4[1];
			    transformPtr[0] = transformCopy;
			    var transformSize = (ulong)sizeof(Matrix4x4);
			    encoder.SetVertexBytes((IntPtr)transformPtr, transformSize, 1);
			}
			encoder.SetFragmentBuffer(materialResources.ColorBuffer, 0, 0);
			encoder.DrawIndexedPrimitives(MTLPrimitiveType.Triangle, meshResources.IndexCount, MTLIndexType.UInt32, meshResources.IndexBuffer, 0);
		}

		encoder.EndEncoding();
		commandBuffer.PresentDrawable(drawable);
		commandBuffer.Commit();
		
		Cleanup();
		return;

		void Cleanup()
		{
			_drawCommands.Clear();
			ArenaAllocator.RenderCommands.Reset();
		}
	}

    private void ResizeDrawable(MTKViewInstance view, NSRect rect)
    {
        var size = rect.Size;
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        var width = size.X;
        var height = size.Y;

        if (_isUpdatingDrawableSize)
        {
            return;
        }

        if (_hasDrawableSize && NearlyEqual(width, _drawableWidth) && NearlyEqual(height, _drawableHeight))
        {
            return;
        }

        _isUpdatingDrawableSize = true;
        try
        {
            view.SetDrawableSize(size);
            _drawableWidth = width;
            _drawableHeight = height;
            _hasDrawableSize = true;
        }
        finally
        {
            _isUpdatingDrawableSize = false;
        }
    }

    private void SetupMenu()
    {
        _mainMenu = new NSMenu();
        _appMenuItem = new NSMenuItem();
        _mainMenu.AddItem(_appMenuItem);

        _appMenu = new NSMenu(WindowTitle);
        var quitTitle = $"Quit {WindowTitle}";
        _quitMenuItem = new NSMenuItem();
        _quitMenuItem.SetTitle(quitTitle);
        _quitMenuItem.SetAction(new Selector("terminate:"));
        _quitMenuItem.SetTarget(IntPtr.Zero); // let NSApp handle terminate:
        _quitMenuItem.SetKeyEquivalent("q");
        _appMenu.AddItem(_quitMenuItem);

        _appMenuItem.SetSubmenu(_appMenu);
        _application.SetMainMenu(_mainMenu);
    }

    private void OnWindowWillClose()
    {
        _application.Terminate();
    }

    private static bool NearlyEqual(double a, double b)
    {
        const double epsilon = 0.5;
        return Math.Abs(a - b) < epsilon;
    }
}
