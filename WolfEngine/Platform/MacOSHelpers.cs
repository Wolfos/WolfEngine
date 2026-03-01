using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using WolfEngine.Utility;

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

    public void SetMainMenu(NSMenu menu)
    {
        ObjectiveC.objc_msgSend(NativePtr, "setMainMenu:", menu.NativePtr);
    }

    public void Terminate()
    {
        ObjectiveC.objc_msgSend(NativePtr, "terminate:", NativePtr);
    }
}

[SupportedOSPlatform("macos")]
internal sealed class NSWindowInstance
{
    private static readonly Selector StyleMaskSelector = new("styleMask");
    private static readonly Selector SetStyleMaskSelector = new("setStyleMask:");
    private const ulong FullSizeContentViewStyleMask = 1UL << 15;

    public IntPtr NativePtr { get; }

    public NSWindowInstance(NSRect rect, ulong styleMask)
    {
        var windowClass = new ObjectiveCClass("NSWindow");
        NativePtr = windowClass.Alloc();
        ObjectiveC.objc_msgSend(NativePtr, "initWithContentRect:styleMask:backing:defer:", rect, styleMask, 2, false);
    }

    public NSWindowInstance(IntPtr ptr)
    {
        NativePtr = ptr;
    }

    public NSString Title
    {
        get => new(ObjectiveC.IntPtr_objc_msgSend(NativePtr, "title"));
        set => ObjectiveC.objc_msgSend(NativePtr, "setTitle:", value);
    }

    public void SetTitle(string title)
    {
        Title = NSStringHelper.From(title);
    }

    public void SetContentView(IntPtr view)
    {
        ObjectiveC.objc_msgSend(NativePtr, "setContentView:", view);
    }

    public void MakeKeyAndOrderFront()
    {
        ObjectiveC.objc_msgSend(NativePtr, "makeKeyAndOrderFront:", IntPtr.Zero);
    }

    public void SetDelegate(NSWindowDelegate @delegate)
    {
        ObjectiveC.objc_msgSend(NativePtr, "setDelegate:", @delegate.NativePtr);
    }

    public void EnableUnifiedTitlebarChrome(bool includeFullSizeContentView = false)
    {
        if (includeFullSizeContentView)
        {
            var styleMask = ObjCNative.ObjcMsgSendULong(NativePtr, StyleMaskSelector.SelPtr);
            styleMask |= FullSizeContentViewStyleMask;
            ObjCNative.ObjcMsgSendSetULong(NativePtr, SetStyleMaskSelector.SelPtr, styleMask);
        }

        ObjectiveC.objc_msgSend(NativePtr, "setTitleVisibility:", (long)NSWindowTitleVisibility.Hidden);
        ObjectiveC.objc_msgSend(NativePtr, "setTitlebarAppearsTransparent:", true);
        ObjectiveC.objc_msgSend(NativePtr, "setMovableByWindowBackground:", false);
        SetTitle(string.Empty);
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

[SupportedOSPlatform("macos")]
internal static class ObjCNative
{
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    public static extern void ObjcMsgSendDrawableSize(IntPtr receiver, IntPtr selector, NSPoint size);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    public static extern long ObjcMsgSendLong(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    public static extern ulong ObjcMsgSendULong(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    public static extern void ObjcMsgSendSetULong(IntPtr receiver, IntPtr selector, ulong value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    public static extern byte ObjcMsgSendBool(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void ObjcMsgSendSetBoolNative(IntPtr receiver, IntPtr selector, byte value);

    public static void ObjcMsgSendSetBool(IntPtr receiver, IntPtr selector, bool value)
    {
        ObjcMsgSendSetBoolNative(receiver, selector, value ? (byte)1 : (byte)0);
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_retain")]
    public static extern IntPtr ObjcRetain(IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_release")]
    public static extern void ObjcRelease(IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_autoreleasePoolPush")]
    public static extern IntPtr ObjcAutoreleasePoolPush();

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_autoreleasePoolPop")]
    public static extern void ObjcAutoreleasePoolPop(IntPtr pool);
}

[SupportedOSPlatform("macos")]
internal static class NSStringHelper
{
    public static NSString From(string value)
    {
        var nsStringClass = new ObjectiveCClass("NSString");
        var alloc = nsStringClass.Alloc();
        var ptr = ObjectiveC.IntPtr_objc_msgSend(alloc, "initWithUTF8String:", value);
        return new NSString(ptr);
    }
}

[SupportedOSPlatform("macos")]
internal static class NSStringExtensions
{
    public static string ToManagedString(this NSString nsString, string fallback = "")
    {
        if (nsString.NativePtr == IntPtr.Zero)
        {
            return fallback;
        }

        var utf8Ptr = ObjectiveC.IntPtr_objc_msgSend(nsString.NativePtr, new Selector("UTF8String"));
        if (utf8Ptr == IntPtr.Zero)
        {
            return fallback;
        }

        return Marshal.PtrToStringUTF8(utf8Ptr) ?? fallback;
    }
}

[SupportedOSPlatform("macos")]
internal sealed class NSWindowDelegate
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WindowWillCloseDelegate(IntPtr id, IntPtr cmd, IntPtr notification);

    private readonly WindowWillCloseDelegate _willClose;

    public event Action WindowWillClose;

    public IntPtr NativePtr { get; }

    public unsafe NSWindowDelegate()
    {
        byte* name = Utf8StringMarshaller.ConvertToUnmanaged("WolfEngineWindowDelegate");
        byte* types = Utf8StringMarshaller.ConvertToUnmanaged("v@:@");

        _willClose = (_, _, _) => WindowWillClose?.Invoke();
        var willClosePtr = Marshal.GetFunctionPointerForDelegate(_willClose);

        var @class = ObjectiveC.objc_allocateClassPair(new ObjectiveCClass("NSObject"), (char*)name, 0);
        ObjectiveC.class_addMethod(@class, "windowWillClose:", willClosePtr, (char*)types);
        ObjectiveC.objc_registerClassPair(@class);

        NativePtr = new ObjectiveCClass(@class).AllocInit();
    }
}

[SupportedOSPlatform("macos")]
internal sealed class NSMenu
{
    public IntPtr NativePtr { get; }

    public NSMenu()
    {
        var menuClass = new ObjectiveCClass("NSMenu");
        NativePtr = ObjectiveC.IntPtr_objc_msgSend(menuClass.Alloc(), new Selector("init"));
    }

    public NSMenu(string title)
    {
        var menuClass = new ObjectiveCClass("NSMenu");
        var alloc = menuClass.Alloc();
        NativePtr = ObjectiveC.IntPtr_objc_msgSend(alloc, new Selector("initWithTitle:"), title);
    }

    public void AddItem(NSMenuItem item)
    {
        ObjectiveC.objc_msgSend(NativePtr, new Selector("addItem:"), item.NativePtr);
    }
}

[SupportedOSPlatform("macos")]
internal sealed class NSMenuItem
{
    public const ulong CommandKeyMask = 1UL << 20;

    public IntPtr NativePtr { get; }

    public NSMenuItem()
    {
        var itemClass = new ObjectiveCClass("NSMenuItem");
        NativePtr = ObjectiveC.IntPtr_objc_msgSend(itemClass.Alloc(), new Selector("init"));
    }

    public void SetSubmenu(NSMenu submenu)
    {
        ObjectiveC.objc_msgSend(NativePtr, new Selector("setSubmenu:"), submenu.NativePtr);
    }

    public void SetTarget(IntPtr target)
    {
        ObjectiveC.objc_msgSend(NativePtr, new Selector("setTarget:"), target);
    }

    public void SetTitle(string title)
    {
        var nsTitle = NSStringHelper.From(title);
        ObjectiveC.objc_msgSend(NativePtr, new Selector("setTitle:"), nsTitle);
    }

    public void SetAction(Selector action)
    {
        ObjectiveC.objc_msgSend(NativePtr, new Selector("setAction:"), action);
    }

    public void SetKeyEquivalent(string key)
    {
        var nsKey = NSStringHelper.From(key);
        ObjectiveC.objc_msgSend(NativePtr, new Selector("setKeyEquivalent:"), nsKey);
    }
}

[SupportedOSPlatform("macos")]
internal static class MacOSFileDialog
{
    private const long ModalResponseOk = 1;

    public static string? OpenFile(FileDialogOptions options)
    {
        var panelClass = new ObjectiveCClass("NSOpenPanel");
        var panel = ObjectiveC.IntPtr_objc_msgSend(panelClass, "openPanel");

        ObjectiveC.objc_msgSend(panel, "setCanChooseFiles:", true);
        ObjectiveC.objc_msgSend(panel, "setCanChooseDirectories:", false);
        ObjectiveC.objc_msgSend(panel, "setAllowsMultipleSelection:", false);

        if (string.IsNullOrWhiteSpace(options.Title) == false)
        {
            var title = NSStringHelper.From(options.Title);
            ObjectiveC.objc_msgSend(panel, "setTitle:", title);
        }

        if (string.IsNullOrWhiteSpace(options.InitialDirectory) == false)
        {
            var urlClass = new ObjectiveCClass("NSURL");
            var nsPath = NSStringHelper.From(options.InitialDirectory).ToString();
            var url = ObjectiveC.IntPtr_objc_msgSend(urlClass, "fileURLWithPath:", nsPath);
            ObjectiveC.objc_msgSend(panel, "setDirectoryURL:", url);
        }

        if (options.AllowedExtensions is not null && options.AllowedExtensions.Length > 0)
        {
            var allowed = CreateAllowedFileTypes(options.AllowedExtensions);
            if (allowed != IntPtr.Zero)
            {
                ObjectiveC.objc_msgSend(panel, "setAllowedFileTypes:", allowed);
            }
        }

        var response = ObjCNative.ObjcMsgSendLong(panel, new Selector("runModal").SelPtr);
        if (response != ModalResponseOk)
        {
            return null;
        }

        var urlPtr = ObjectiveC.IntPtr_objc_msgSend(panel, "URL");
        if (urlPtr == IntPtr.Zero)
        {
            return null;
        }

        var pathPtr = ObjectiveC.IntPtr_objc_msgSend(urlPtr, "path");
        return new NSString(pathPtr).ToManagedString();
    }

    private static IntPtr CreateAllowedFileTypes(string[] extensions)
    {
        var arrayClass = new ObjectiveCClass("NSMutableArray");
        var array = ObjectiveC.IntPtr_objc_msgSend(arrayClass.Alloc(), new Selector("init"));

        var hasEntries = false;
        for (var i = 0; i < extensions.Length; i++)
        {
            var entry = extensions[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var trimmed = entry.Trim().TrimStart('.');
            if (trimmed.Length == 0)
            {
                continue;
            }

            var nsEntry = NSStringHelper.From(trimmed);
            ObjectiveC.objc_msgSend(array, new Selector("addObject:"), nsEntry);
            hasEntries = true;
        }

        return hasEntries ? array : IntPtr.Zero;
    }
}

[SupportedOSPlatform("macos")]
public static class BufferHelper
{
	public static unsafe void CopyToBuffer<T>(T[] source, MTLBuffer buffer)
	{
		CopyToBuffer<T>((ReadOnlySpan<T>)source.AsSpan(), buffer);
	}

	public static unsafe void CopyToBuffer<T>(ReadOnlySpan<T> source, MTLBuffer buffer)
	{
		if (source.IsEmpty)
		{
			return;
		}

		var span = new Span<T>(buffer.Contents.ToPointer(), source.Length);
		source.CopyTo(span);
	}
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

internal enum NSWindowTitleVisibility : long
{
    Visible = 0,
    Hidden = 1
}
