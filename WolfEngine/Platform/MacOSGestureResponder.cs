using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMetal.ObjectiveCCore;

namespace WolfEngine.Platform;

/// <summary>
/// Preserves Cocoa pinch and precise scroll information that SDL2's wheel events omit.
/// Other events, including scrolling for gameplay, continue through SDL's responder.
/// Must be attached and disposed on the window's main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSGestureResponder : IDisposable
{
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void EventDelegate(IntPtr receiver, IntPtr selector, IntPtr nativeEvent);

	private static readonly ConcurrentDictionary<IntPtr, MacOSGestureResponder> Responders = new();
	private static readonly EventDelegate Magnify = OnMagnify;
	private static readonly EventDelegate Scroll = OnScroll;
	private static readonly IntPtr ResponderClass = RegisterClass();
	private static readonly Selector Magnification = new("magnification");
	private static readonly Selector Precise = new("hasPreciseScrollingDeltas");
	private static readonly Selector DeltaX = new("deltaX");
	private static readonly Selector DeltaY = new("deltaY");
	private static readonly Selector ScrollingDeltaX = new("scrollingDeltaX");
	private static readonly Selector ScrollingDeltaY = new("scrollingDeltaY");
	private static readonly Selector MomentumPhase = new("momentumPhase");
	private readonly Action<float> _magnify;
	private readonly Action<Vector2, Vector2?> _scroll;
	private readonly IntPtr _view;
	private IntPtr _previousResponder;
	private IntPtr _responder;

	public static void EnableMomentumScrolling()
	{
		// SDL2 opts out during SDL_Init. AppKit needs an explicit application preference on
		// recent macOS; changing NSRegistrationDomain alone does not enable momentum events.
		var defaults = ObjectiveC.IntPtr_objc_msgSend(new ObjectiveCClass("NSUserDefaults"), "standardUserDefaults");
		SetBoolForKey(
			defaults,
			new Selector("setBool:forKey:").SelPtr,
			1,
			NSStringHelper.From("AppleMomentumScrollSupported").NativePtr);
	}

	public MacOSGestureResponder(
		IntPtr view,
		Action<float> magnify,
		Action<Vector2, Vector2?> scroll)
	{
		_view = view;
		_magnify = magnify;
		_scroll = scroll;
		_responder = new ObjectiveCClass(ResponderClass).AllocInit();
		Responders[_responder] = this;
		EnsureAttached();
	}

	public void EnsureAttached()
	{
		var next = ObjectiveC.IntPtr_objc_msgSend(_view, "nextResponder");
		if (_responder == IntPtr.Zero || next == _responder) return;

		// SDL rebuilds the responder chain when the window changes fullscreen/style.
		_previousResponder = next;
		ObjectiveC.objc_msgSend(_responder, "setNextResponder:", next);
		ObjectiveC.objc_msgSend(_view, "setNextResponder:", _responder);
	}

	public void Dispose()
	{
		if (_responder == IntPtr.Zero) return;

		if (ObjectiveC.IntPtr_objc_msgSend(_view, "nextResponder") == _responder)
		{
			ObjectiveC.objc_msgSend(_view, "setNextResponder:", _previousResponder);
		}

		Responders.TryRemove(_responder, out _);
		ObjectiveC.objc_msgSend(_responder, "release");
		_responder = IntPtr.Zero;
	}

	private static void OnMagnify(IntPtr receiver, IntPtr selector, IntPtr nativeEvent)
	{
		if (Responders.TryGetValue(receiver, out var responder))
		{
			responder._magnify((float)ReadDouble(nativeEvent, Magnification.SelPtr));
		}
	}

	private static void OnScroll(IntPtr receiver, IntPtr selector, IntPtr nativeEvent)
	{
		if (Responders.TryGetValue(receiver, out var responder) == false) return;

		var wheel = new Vector2(
			(float)ReadDouble(nativeEvent, DeltaX.SelPtr),
			(float)ReadDouble(nativeEvent, DeltaY.SelPtr));
		Vector2? pan = null;
		if (ObjCNative.ObjcMsgSendBool(nativeEvent, Precise.SelPtr) != 0)
		{
			pan = new Vector2(
				(float)ReadDouble(nativeEvent, ScrollingDeltaX.SelPtr),
				(float)ReadDouble(nativeEvent, ScrollingDeltaY.SelPtr));
		}
		else
		{
			wheel = new Vector2(RoundWheel(wheel.X), RoundWheel(wheel.Y));
		}

		responder._scroll(wheel, pan);
		if (ObjCNative.ObjcMsgSendULong(nativeEvent, MomentumPhase.SelPtr) == 0)
		{
			// Preserve gameplay's direct scroll input; inertial events are for the editor UI.
			ObjectiveC.objc_msgSend(responder._previousResponder, "scrollWheel:", nativeEvent);
		}
	}

	private static float RoundWheel(float delta)
	{
		return delta > 0.0f ? MathF.Ceiling(delta) : MathF.Floor(delta);
	}

	private static unsafe IntPtr RegisterClass()
	{
		fixed (byte* name = "WolfEngineGestureResponder\0"u8)
		fixed (byte* signature = "v@:@\0"u8)
		{
			var nativeClass = ObjectiveC.objc_allocateClassPair(
				new ObjectiveCClass("NSResponder"),
				(char*)name,
				0);
			ObjectiveC.class_addMethod(
				nativeClass,
				"magnifyWithEvent:",
				Marshal.GetFunctionPointerForDelegate(Magnify),
				(char*)signature);
			ObjectiveC.class_addMethod(
				nativeClass,
				"scrollWheel:",
				Marshal.GetFunctionPointerForDelegate(Scroll),
				(char*)signature);
			ObjectiveC.objc_registerClassPair(nativeClass);

			return nativeClass;
		}
	}

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
	private static extern double ReadDouble(IntPtr receiver, IntPtr selector);

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
	private static extern void SetBoolForKey(IntPtr receiver, IntPtr selector, byte value, IntPtr key);
}
