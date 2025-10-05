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
    private NSWindowDelegate _windowDelegate = null!;
    private NSMenu _mainMenu = null!;
    private NSMenuItem _appMenuItem = null!;
    private NSMenu _appMenu = null!;
    private NSMenuItem _quitMenuItem = null!;
    private MTLDevice _device;
    private MTLCommandQueue _commandQueue;
    private readonly MTLClearColor _clearColor = new() { red = 0.392, green = 0.584, blue = 0.929, alpha = 1.0 };
    private bool _isUpdatingDrawableSize;
    private bool _hasDrawableSize;
    private double _drawableWidth;
    private double _drawableHeight;

    private MTLBuffer _vertexBuffer;
    private MTLBuffer _indexBuffer;
    private MTLLibrary _shaderLibrary;
    private MTLRenderPipelineState _pipelineState;


    private const string WindowTitle = "WolfEngine";

    private Mesh _mesh = null!;
    private string _shaderPath = null!;
    private ulong _vertexCount;
    private ulong _indexCount;
    private bool _meshLoaded;

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

	public void LoadMesh(Mesh mesh, string shaderPath)
	{
		_mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
		_shaderPath = string.IsNullOrWhiteSpace(shaderPath)
			? throw new ArgumentException("Shader path cannot be empty.", nameof(shaderPath))
			: shaderPath;
		_vertexCount = (ulong)_mesh.Vertices.Length;
		_indexCount = (ulong)_mesh.Indices.Length;
		_meshLoaded = true;
	}

	public void Run()
	{
		if (!_meshLoaded)
		{
			throw new InvalidOperationException("LoadMesh must be called before running the renderer.");
		}

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
        
        UploadMesh();
        CreateDefaultLibrary();
        CreateCommandQueue();

        CreateRenderPipeline();

        var app = new NSApplicationInstance(notification.Object);
        app.ActivateIgnoringOtherApps(true);
    }

	private void CreateRenderPipeline()
	{
	    var vertexShader = _shaderLibrary.NewFunction(NSStringHelper.From("vertexShader"));
	    var fragmentShader = _shaderLibrary.NewFunction(NSStringHelper.From("fragmentShader"));

	    var pipeline = new MTLRenderPipelineDescriptor();
	    pipeline.VertexFunction = vertexShader;
	    pipeline.FragmentFunction = fragmentShader;
	    pipeline.VertexDescriptor = CreateVertexDescriptor();
        
	    var colorAttachment = pipeline.ColorAttachments.Object(0);
	    colorAttachment.PixelFormat = MTLPixelFormat.BGRA8UnormsRGB;
	    pipeline.ColorAttachments.SetObject(colorAttachment, 0);
        
	    var pipelineStateError = new NSError(IntPtr.Zero);
	    _pipelineState = _device.NewRenderPipelineState(pipeline, ref pipelineStateError);
	    if (pipelineStateError != IntPtr.Zero)
	    {
		    throw new Exception($"Failed to create render pipeline state! {pipelineStateError.LocalizedDescription.ToManagedString()}");
	    }
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

    private void UploadMesh()
	{
		if (!_meshLoaded)
		{
			throw new InvalidOperationException("Mesh data is not loaded.");
		}

		var vertexBufferLength = (ulong)(_mesh.Vertices.Length * Marshal.SizeOf<Vector4>());
		_vertexBuffer = _device.NewBuffer(vertexBufferLength, MTLResourceOptions.ResourceStorageModeManaged);
		if (_vertexBuffer.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to allocate vertex buffer.");
		}
		BufferHelper.CopyToBuffer(_mesh.Vertices, _vertexBuffer);
		_vertexBuffer.DidModifyRange(new NSRange { location = 0, length = vertexBufferLength });

		var indexBufferLength = (ulong)(_mesh.Indices.Length * sizeof(uint));
		_indexBuffer = _device.NewBuffer(indexBufferLength, MTLResourceOptions.ResourceStorageModeManaged);
		if (_indexBuffer.NativePtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to allocate index buffer.");
		}
		BufferHelper.CopyToBuffer(_mesh.Indices, _indexBuffer);
		_indexBuffer.DidModifyRange(new NSRange { location = 0, length = indexBufferLength });
	}

	private void CreateDefaultLibrary()
	{
		var libraryError = new NSError(IntPtr.Zero);
		if (!_meshLoaded)
		{
			throw new InvalidOperationException("Mesh data is not loaded.");
		}

		var shaderSource = _shaderCompiler.GetShader(_shaderPath);
		_shaderLibrary = _device.NewLibrary(NSStringHelper.From(shaderSource), new(IntPtr.Zero), ref libraryError);
		if (libraryError != IntPtr.Zero)
		{
			var description = libraryError.LocalizedDescription.ToManagedString("Unknown error");
			throw new Exception($"Failed to create library! {description}");
		}
	}

    private void Draw(MTKViewInstance view)
	{
		if (_commandQueue.NativePtr == IntPtr.Zero)
		{
			return;
		}

		var renderPassDescriptor = view.CurrentRenderPassDescriptor;
		if (renderPassDescriptor.NativePtr == IntPtr.Zero)
		{
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
			return;
		}

		var commandBuffer = _commandQueue.CommandBuffer();
		var encoder = commandBuffer.RenderCommandEncoder(renderPassDescriptor);
		encoder.SetRenderPipelineState(_pipelineState);
		encoder.SetVertexBuffer(_vertexBuffer, 0, 0);
		encoder.DrawIndexedPrimitives(MTLPrimitiveType.Triangle, _indexCount, MTLIndexType.UInt32, _indexBuffer, 0);
		
		encoder.EndEncoding();
		commandBuffer.PresentDrawable(drawable);
		commandBuffer.Commit();
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
