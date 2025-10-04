using System;
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
    private MTLDevice _device;
    private MTLCommandQueue _commandQueue;
    private readonly MTLClearColor _clearColor = new() { red = 0.392, green = 0.584, blue = 0.929, alpha = 1.0 };
    private bool _isUpdatingDrawableSize;
    private bool _hasDrawableSize;
    private double _drawableWidth;
    private double _drawableHeight;

    private const string WindowTitle = "Hello Metal!";

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
        _device = MTLDevice.CreateSystemDefaultDevice();
        if (_device.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create the default Metal device.");
        }

        _commandQueue = _device.NewCommandQueue();
        if (_commandQueue.NativePtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create a Metal command queue.");
        }

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

        var app = new NSApplicationInstance(notification.Object);
        app.ActivateIgnoringOtherApps(true);
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

    private static bool NearlyEqual(double a, double b)
    {
        const double epsilon = 0.5;
        return Math.Abs(a - b) < epsilon;
    }
}
