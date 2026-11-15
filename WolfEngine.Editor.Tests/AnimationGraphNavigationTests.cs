using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NSubstitute;
using SharpMetal.ObjectiveCCore;
using Silk.NET.SDL;
using WolfEngine.Input;
using WolfEngine.Platform;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.Tests;

[NonParallelizable]
public sealed class AnimationGraphNavigationTests
{
	[TestCase(MouseWheelDirection.Normal)]
	[TestCase(MouseWheelDirection.Flipped)]
	public void MacScrollPreservesPrecisionAndNaturalDirectionForUi(MouseWheelDirection direction)
	{
		var input = Substitute.For<IInputSystem>();
		var ui = Substitute.For<IImGuiInputSink>();
		var handler = new MacOsInputHandler(input, ui);
		var nativeEvent = new Event
		{
			Wheel = new MouseWheelEvent
			{
				Type = (uint)EventType.Mousewheel,
				PreciseX = 0.125f,
				PreciseY = -0.25f,
				Direction = (uint)direction
			}
		};
		handler.HandleInputEvents(ref nativeEvent);

		ui.Received().AddMouseScroll(new Vector2(-0.125f, -0.25f));
		var physicalScroll = new Vector2(0.125f, -0.25f);
		input.Received().SetAxis2D(
			InputActionBinding.MouseScroll,
			direction == MouseWheelDirection.Flipped ? -physicalScroll : physicalScroll);
	}

	[Test]
	public void MomentumOptInChangesNativeApplicationPreferencesRatherThanOnlyRegistrationDefaults()
	{
		if (OperatingSystem.IsMacOS())
		{
			CheckMomentumPreference();
		}
	}

	[SupportedOSPlatform("macos")]
	private static void CheckMomentumPreference()
	{
		NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
		var foundation = NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
		var application = Marshal.ReadIntPtr(NativeLibrary.GetExport(foundation, "kCFPreferencesCurrentApplication"));
		var user = Marshal.ReadIntPtr(NativeLibrary.GetExport(foundation, "kCFPreferencesCurrentUser"));
		var host = Marshal.ReadIntPtr(NativeLibrary.GetExport(foundation, "kCFPreferencesAnyHost"));
		var key = NSStringHelper.From("AppleMomentumScrollSupported").NativePtr;
		var original = CopyPreference(key, application, user, host);
		var defaults = ObjectiveC.IntPtr_objc_msgSend(new ObjectiveCClass("NSUserDefaults"), "standardUserDefaults");
		try
		{
			SetBoolForKey(defaults, new Selector("setBool:forKey:").SelPtr, 0, key);
			MacOSGestureResponder.EnableMomentumScrolling();

			Assert.That(ReadApplicationBool(key, application, out var valid), Is.EqualTo(1));
			Assert.That(valid, Is.EqualTo(1));
		}
		finally
		{
			if (original == IntPtr.Zero)
			{
				ObjectiveC.objc_msgSend(defaults, "removeObjectForKey:", key);
			}
			else
			{
				SetObjectForKey(defaults, new Selector("setObject:forKey:").SelPtr, original, key);
				Release(original);
			}
		}
	}

	[Test]
	public void MacPinchTraversesTheNativeResponderChainAndRestoresItOnDispose()
	{
		if (OperatingSystem.IsMacOS())
		{
			CheckNativeResponder();
		}
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate double MagnificationDelegate(IntPtr receiver, IntPtr selector);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate byte PreciseDelegate(IntPtr receiver, IntPtr selector);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate ulong MomentumDelegate(IntPtr receiver, IntPtr selector);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void ScrollDelegate(IntPtr receiver, IntPtr selector, IntPtr nativeEvent);

	private static readonly MagnificationDelegate Magnification = (_, _) => 0.25;
	private static readonly MagnificationDelegate Horizontal = (_, _) => 0.125;
	private static readonly MagnificationDelegate Vertical = (_, _) => -0.25;
	private static readonly MagnificationDelegate PanX = (_, _) => _pan.X;
	private static readonly MagnificationDelegate PanY = (_, _) => _pan.Y;
	private static readonly MomentumDelegate Momentum = (_, _) => _momentum;
	private static readonly PreciseDelegate Precise = (_, _) => _preciseScroll ? (byte)1 : (byte)0;
	private static readonly ScrollDelegate ForwardedScroll = (_, _, _) => _forwardedScroll++;
	private static bool _preciseScroll;
	private static int _forwardedScroll;
	private static Vector2 _pan;
	private static ulong _momentum;

	[SupportedOSPlatform("macos")]
	private static void CheckNativeResponder()
	{
		NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
		IntPtr view = new ObjectiveCClass("NSResponder").AllocInit();
		var previousClass = AllocateClass(
			new ObjectiveCClass("NSResponder"),
			"WolfEngineTestScrollResponder_" + Guid.NewGuid().ToString("N"),
			0);
		AddMethod(
			previousClass,
			new Selector("scrollWheel:").SelPtr,
			Marshal.GetFunctionPointerForDelegate(ForwardedScroll),
			"v@:@");
		ObjectiveC.objc_registerClassPair(previousClass);
		IntPtr previous = new ObjectiveCClass(previousClass).AllocInit();
		var eventClass = AllocateClass(
			new ObjectiveCClass("NSObject"),
			"WolfEngineTestMagnificationEvent_" + Guid.NewGuid().ToString("N"),
			0);
		AddMethod(
			eventClass,
			new Selector("magnification").SelPtr,
			Marshal.GetFunctionPointerForDelegate(Magnification),
			"d@:");
		AddMethod(
			eventClass,
			new Selector("hasPreciseScrollingDeltas").SelPtr,
			Marshal.GetFunctionPointerForDelegate(Precise),
			"c@:");
		AddMethod(
			eventClass,
			new Selector("deltaX").SelPtr,
			Marshal.GetFunctionPointerForDelegate(Horizontal),
			"d@:");
		AddMethod(
			eventClass,
			new Selector("deltaY").SelPtr,
			Marshal.GetFunctionPointerForDelegate(Vertical),
			"d@:");
		AddMethod(
			eventClass,
			new Selector("scrollingDeltaX").SelPtr,
			Marshal.GetFunctionPointerForDelegate(PanX),
			"d@:");
		AddMethod(
			eventClass,
			new Selector("scrollingDeltaY").SelPtr,
			Marshal.GetFunctionPointerForDelegate(PanY),
			"d@:");
		AddMethod(
			eventClass,
			new Selector("momentumPhase").SelPtr,
			Marshal.GetFunctionPointerForDelegate(Momentum),
			"Q@:");
		ObjectiveC.objc_registerClassPair(eventClass);
		var nativeEvent = new ObjectiveCClass(eventClass).AllocInit();
		ObjectiveC.objc_msgSend(view, "setNextResponder:", previous);
		var received = 0.0f;
		var scrolls = new List<(Vector2 Wheel, Vector2? Pan)>();
		_forwardedScroll = 0;
		_momentum = 0;
		try
		{
			using (var responder = new MacOSGestureResponder(
				view,
				delta => received += delta,
				(wheel, pan) => scrolls.Add((wheel, pan))))
			{
				ObjectiveC.objc_msgSend(view, "magnifyWithEvent:", nativeEvent);
				Assert.That(received, Is.EqualTo(0.25f));
				_preciseScroll = true;
				_pan = new Vector2(2.5f, -4.5f);
				ObjectiveC.objc_msgSend(view, "scrollWheel:", nativeEvent);
				_pan *= 10.0f;
				ObjectiveC.objc_msgSend(view, "scrollWheel:", nativeEvent);
				_momentum = 1;
				_pan = new Vector2(1.25f, -2.25f);
				ObjectiveC.objc_msgSend(view, "scrollWheel:", nativeEvent);
				_momentum = 0;
				_preciseScroll = false;
				ObjectiveC.objc_msgSend(view, "scrollWheel:", nativeEvent);

				Assert.That(scrolls[0].Wheel, Is.EqualTo(new Vector2(0.125f, -0.25f)));
				Assert.That(scrolls[0].Pan, Is.EqualTo(new Vector2(2.5f, -4.5f)));
				Assert.That(scrolls[1].Pan, Is.EqualTo(new Vector2(25.0f, -45.0f)));
				Assert.That(scrolls[2].Pan, Is.EqualTo(new Vector2(1.25f, -2.25f)));
				Assert.That(scrolls[3].Wheel, Is.EqualTo(new Vector2(1.0f, -1.0f)));
				Assert.That(scrolls[3].Pan, Is.Null);
				Assert.That(_forwardedScroll, Is.EqualTo(3));

				ObjectiveC.objc_msgSend(view, "setNextResponder:", previous);
				responder.EnsureAttached();
				responder.EnsureAttached();
				ObjectiveC.objc_msgSend(view, "magnifyWithEvent:", nativeEvent);
				Assert.That(received, Is.EqualTo(0.5f));
			}

			Assert.That(ObjectiveC.IntPtr_objc_msgSend(view, "nextResponder"), Is.EqualTo(previous));
		}
		finally
		{
			ObjectiveC.objc_msgSend(view, "setNextResponder:", IntPtr.Zero);
			ObjectiveC.objc_msgSend(nativeEvent, "release");
			ObjectiveC.objc_msgSend(view, "release");
			ObjectiveC.objc_msgSend(previous, "release");
		}
	}

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_allocateClassPair")]
	private static extern IntPtr AllocateClass(IntPtr superclass, string name, nuint extraBytes);

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_addMethod")]
	private static extern byte AddMethod(IntPtr nativeClass, IntPtr selector, IntPtr implementation, string signature);

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
	private static extern void SetBoolForKey(IntPtr receiver, IntPtr selector, byte value, IntPtr key);

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
	private static extern void SetObjectForKey(IntPtr receiver, IntPtr selector, IntPtr value, IntPtr key);

	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFPreferencesGetAppBooleanValue")]
	private static extern byte ReadApplicationBool(IntPtr key, IntPtr application, out byte valid);

	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFPreferencesCopyValue")]
	private static extern IntPtr CopyPreference(IntPtr key, IntPtr application, IntPtr user, IntPtr host);

	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
	private static extern void Release(IntPtr value);
}
