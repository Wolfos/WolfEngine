using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using WolfEngine.Platform;

namespace WolfEngine;

[SupportedOSPlatform("macos")]
public class WolfRendererMetal
{
    private readonly int _width;
    private readonly int _height;

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
    private MTLLibrary _defaultLibrary;
    private MTLRenderPipelineState _pipelineState;


    private const string WindowTitle = "Hello Metal!";
    
    private Vector3[] _triangleVertices =
    [
	    new(-0.5f, -0.5f, 0.0f),
	    new( 0.5f, -0.5f, 0.0f),
	    new( 0.0f,  0.5f, 0.0f)
    ];

    private const string _shaderSource =
	    "\n#include <metal_stdlib>\nusing namespace metal;\n\nvertex float4\nvertexShader(uint vertexID [[vertex_id]],\n             constant simd::float3* vertexPositions)\n{\n    float4 vertexOutPositions = float4(vertexPositions[vertexID][0],\n                                       vertexPositions[vertexID][1],\n                                       vertexPositions[vertexID][2],\n                                       1.0f);\n    return vertexOutPositions;\n}\n\nfragment float4 fragmentShader(float4 vertexOutPositions [[stage_in]]) {\n    return float4(182.0f/255.0f, 240.0f/255.0f, 228.0f/255.0f, 1.0f);\n}";

    public WolfRendererMetal(int screenWidth, int screenHeight)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Metal renderer is only supported on macOS.");
        }

        _width = screenWidth;
        _height = screenHeight;

        ObjectiveC.LinkMetal();
        ObjectiveC.LinkCoreGraphics();
        ObjectiveC.LinkAppKit();
        ObjectiveC.LinkMetalKit();

        _application = new NSApplicationInstance();
        _appDelegate = new MetalAppDelegate();
        _appDelegate.WillFinishLaunching += OnApplicationWillFinishLaunching;
        _appDelegate.DidFinishLaunching += OnApplicationDidFinishLaunching;

        _application.SetDelegate(_appDelegate);
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
        
        CreateTriangle();
        CreateDefaultLibrary();
        CreateCommandQueue();

        CreateRenderPipeline();

        var app = new NSApplicationInstance(notification.Object);
        app.ActivateIgnoringOtherApps(true);
    }

    private void CreateRenderPipeline()
    {
	    var vertexShader = _defaultLibrary.NewFunction(new NSString(NSStringHelper.Create("vertexShader")));
	    var fragmentShader = _defaultLibrary.NewFunction(new NSString(NSStringHelper.Create("fragmentShader")));

	    var pipeline = new MTLRenderPipelineDescriptor();
	    pipeline.VertexFunction = vertexShader;
	    pipeline.FragmentFunction = fragmentShader;
        
	    var colorAttachment = pipeline.ColorAttachments.Object(0);
	    colorAttachment.PixelFormat = MTLPixelFormat.BGRA8UnormsRGB;
	    pipeline.ColorAttachments.SetObject(colorAttachment, 0);
        
	    var pipelineStateError = new NSError(IntPtr.Zero);
	    _pipelineState = _device.NewRenderPipelineState(pipeline, ref pipelineStateError);
	    if (pipelineStateError != IntPtr.Zero)
	    {
		    throw new Exception($"Failed to create render pipeline state! {pipelineStateError.LocalizedDescription}");
	    }
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

    private void CreateTriangle()
	{
		
		
		var length = (ulong)(_triangleVertices.Length * Marshal.SizeOf<Vector3>());
		_vertexBuffer = _device.NewBuffer(length, MTLResourceOptions.ResourceStorageModeManaged);
		BufferHelper.CopyToBuffer(_triangleVertices, _vertexBuffer);
	}

	private void CreateDefaultLibrary()
	{
		_defaultLibrary = _device.NewDefaultLibrary();
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
		encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, (ulong)_triangleVertices.Length);
		
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
