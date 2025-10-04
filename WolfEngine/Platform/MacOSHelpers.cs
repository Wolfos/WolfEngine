using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;

namespace WolfEngine.Platform;

[SupportedOSPlatform("macos")]
internal sealed class MetalAppDelegate
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnApplicationWillFinishLaunchingDelegate(IntPtr id, IntPtr cmd, IntPtr notification);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnApplicationDidFinishLaunchingDelegate(IntPtr id, IntPtr cmd, IntPtr notification);

    private readonly OnApplicationWillFinishLaunchingDelegate _willFinish;
    private readonly OnApplicationDidFinishLaunchingDelegate _didFinish;

    public event Action<NSNotification> WillFinishLaunching;
    public event Action<NSNotification> DidFinishLaunching;

    public IntPtr NativePtr { get; }

    public unsafe MetalAppDelegate()
    {
        var name = Utf8StringMarshaller.ConvertToUnmanaged("WolfRendererMetalAppDelegate");
        var types = Utf8StringMarshaller.ConvertToUnmanaged("v@:#");

        _willFinish = (_, _, notif) => WillFinishLaunching?.Invoke(new NSNotification(notif));
        var willFinishPtr = Marshal.GetFunctionPointerForDelegate(_willFinish);

        _didFinish = (_, _, notif) => DidFinishLaunching?.Invoke(new NSNotification(notif));
        var didFinishPtr = Marshal.GetFunctionPointerForDelegate(_didFinish);

        var appDelegateClass = ObjectiveC.objc_allocateClassPair(new ObjectiveCClass("NSObject"), (char*)name, 0);

        ObjectiveC.class_addMethod(appDelegateClass, "applicationWillFinishLaunching:", willFinishPtr, (char*)types);
        ObjectiveC.class_addMethod(appDelegateClass, "applicationDidFinishLaunching:", didFinishPtr, (char*)types);

        ObjectiveC.objc_registerClassPair(appDelegateClass);

        NativePtr = new ObjectiveCClass(appDelegateClass).AllocInit();
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MetalViewDelegate
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnDrawInViewDelegate(IntPtr id, IntPtr cmd, IntPtr view);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void OnDrawableSizeWillChangeDelegate(IntPtr id, IntPtr cmd, IntPtr view, NSRect size);

    private readonly OnDrawInViewDelegate _drawDelegate;
    private readonly OnDrawableSizeWillChangeDelegate _resizeDelegate;

    public event Action<MTKViewInstance> DrawInMTKView;
    public event Action<MTKViewInstance, NSRect> DrawableSizeWillChange;

    public IntPtr NativePtr { get; }

    public unsafe MetalViewDelegate()
    {
        var name = Utf8StringMarshaller.ConvertToUnmanaged("WolfRendererMetalViewDelegate");
        var drawTypes = Utf8StringMarshaller.ConvertToUnmanaged("v@:#");
        var resizeTypes = Utf8StringMarshaller.ConvertToUnmanaged("v@:#{CGRect={CGPoint=dd}{CGPoint=dd}}");

        _drawDelegate = (_, _, view) => DrawInMTKView?.Invoke(new MTKViewInstance(view));
        _resizeDelegate = (_, _, view, rect) => DrawableSizeWillChange?.Invoke(new MTKViewInstance(view), rect);

        var drawPtr = Marshal.GetFunctionPointerForDelegate(_drawDelegate);
        var resizePtr = Marshal.GetFunctionPointerForDelegate(_resizeDelegate);

        var delegateClass = ObjectiveC.objc_allocateClassPair(new ObjectiveCClass("NSObject"), (char*)name, 0);

        ObjectiveC.class_addMethod(delegateClass, "drawInMTKView:", drawPtr, (char*)drawTypes);
        ObjectiveC.class_addMethod(delegateClass, "mtkView:drawableSizeWillChange:", resizePtr, (char*)resizeTypes);

        ObjectiveC.objc_registerClassPair(delegateClass);

        NativePtr = new ObjectiveCClass(delegateClass).AllocInit();
    }
}

[SupportedOSPlatform("macos")]
internal sealed class NSApplicationInstance
{
    public IntPtr NativePtr { get; }

    public NSApplicationInstance()
    {
        NativePtr = ObjectiveC.IntPtr_objc_msgSend(new ObjectiveCClass("NSApplication"), "sharedApplication");
    }

    public NSApplicationInstance(IntPtr ptr)
    {
        NativePtr = ptr;
    }

    public void Run()
    {
        ObjectiveC.objc_msgSend(NativePtr, "run");
    }

    public void ActivateIgnoringOtherApps(bool flag)
    {
        ObjectiveC.objc_msgSend(NativePtr, "activateIgnoringOtherApps:", flag);
    }

    public bool SetActivationPolicy(NSApplicationActivationPolicy activationPolicy)
    {
        return ObjectiveC.bool_objc_msgSend(NativePtr, "setActivationPolicy:", (long)activationPolicy);
    }

    public void SetDelegate(MetalAppDelegate appDelegate)
    {
        ObjectiveC.objc_msgSend(NativePtr, "setDelegate:", appDelegate.NativePtr);
    }
}

[SupportedOSPlatform("macos")]
internal sealed class NSWindowInstance
{
    public IntPtr NativePtr { get; }

    public NSWindowInstance(NSRect rect, ulong styleMask)
    {
        var windowClass = new ObjectiveCClass("NSWindow");
        NativePtr = windowClass.Alloc();
        ObjectiveC.objc_msgSend(NativePtr, "initWithContentRect:styleMask:backing:defer:", rect, styleMask, 2, false);
    }

    public NSString Title
    {
        get => new(ObjectiveC.IntPtr_objc_msgSend(NativePtr, "title"));
        set => ObjectiveC.objc_msgSend(NativePtr, "setTitle:", value);
    }

    public void SetTitle(string title)
    {
        var nsStringClass = new ObjectiveCClass("NSString");
        var nsStringPtr = ObjectiveC.IntPtr_objc_msgSend(nsStringClass.Alloc(), "initWithUTF8String:", title);
        Title = new NSString(nsStringPtr);
    }

    public void SetContentView(IntPtr view)
    {
        ObjectiveC.objc_msgSend(NativePtr, "setContentView:", view);
    }

    public void MakeKeyAndOrderFront()
    {
        ObjectiveC.objc_msgSend(NativePtr, "makeKeyAndOrderFront:", IntPtr.Zero);
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MTKViewInstance
{
    public IntPtr NativePtr { get; }

    public MTKViewInstance(IntPtr ptr)
    {
        NativePtr = ptr;
    }

    public MTKViewInstance(NSRect frameRect, MTLDevice device)
    {
        var viewClass = new ObjectiveCClass("MTKView");
        var alloc = viewClass.Alloc();
        NativePtr = ObjectiveC.IntPtr_objc_msgSend(alloc, "initWithFrame:device:", frameRect, device);
    }

    public MTLPixelFormat ColorPixelFormat
    {
        set => ObjectiveC.objc_msgSend(NativePtr, new Selector("setColorPixelFormat:atIndex:"), (ulong)value, 0ul);
    }

    public bool Paused
    {
        set => ObjectiveC.objc_msgSend(NativePtr, "setPaused:", value);
    }

    public int PreferredFramesPerSecond
    {
        set => ObjectiveC.objc_msgSend(NativePtr, "setPreferredFramesPerSecond:", value);
    }

    public MetalViewDelegate Delegate
    {
        set => ObjectiveC.objc_msgSend(NativePtr, "setDelegate:", value?.NativePtr ?? IntPtr.Zero);
    }

    private static readonly Selector DrawableSizeSelector = new("setDrawableSize:");

    public void SetDrawableSize(NSPoint size)
    {
        ObjCNative.ObjcMsgSendDrawableSize(NativePtr, DrawableSizeSelector.SelPtr, size);
    }

    public CAMetalDrawable CurrentDrawable => new(ObjectiveC.IntPtr_objc_msgSend(NativePtr, "currentDrawable"));

    public MTLRenderPassDescriptor CurrentRenderPassDescriptor => new(ObjectiveC.IntPtr_objc_msgSend(NativePtr, "currentRenderPassDescriptor"));
}

internal enum NSApplicationActivationPolicy : long
{
    Regular = 0,
    Accessory = 1,
    Prohibited = 2
}

internal static class ObjCNative
{
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    public static extern void ObjcMsgSendDrawableSize(IntPtr receiver, IntPtr selector, NSPoint size);
}

[Flags]
internal enum NSWindowStyleMask : ulong
{
    Borderless = 0,
    Titled = 1 << 0,
    Closable = 1 << 1,
    Miniaturizable = 1 << 2,
    Resizable = 1 << 3
}
