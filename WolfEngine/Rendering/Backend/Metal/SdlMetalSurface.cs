using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpMetal.ObjectiveCCore;
using Silk.NET.Core.Native;
using Silk.NET.SDL;
using WolfEngine.Mathematics;
using WolfEngine.Platform;

namespace WolfEngine.Rendering.Backend.Metal;

/// <summary>
/// The macOS window the desktop editor and runtime render into: an SDL window with a Metal view, the unified title
/// bar the editor draws its menu into, and the SDL events that feed input.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class SdlMetalSurface : IMetalSurface
{
	private const int MacTitlebarDoubleClickHeight = 28;
	private const int MacTrafficLightExclusionWidth = 70;

	private static readonly Selector ScreenSelector = new("screen");
	private static readonly Selector FrameSelector = new("frame");
	private static readonly Selector SafeAreaInsetsSelector = new("safeAreaInsets");
	private static readonly Selector BackingScaleFactorSelector = new("backingScaleFactor");
	private static readonly Selector RespondsToSelectorSelector = new("respondsToSelector:");

	private readonly IMacOSInputHandler _inputHandler;
	private readonly Sdl _sdl;
	private Window* _window;
	private void* _metalView;
	private IntPtr _nativeWindow = IntPtr.Zero;
	private Int2 _requestedSize;
	private bool _hasRequestedDrawableSize;
	private bool _fullscreen;
	private Int2 _fullscreenResolution;
	private Event _event;
	private MacOSGestureResponder? _gestureResponder;

	public SdlMetalSurface(IMacOSInputHandler inputHandler)
	{
		_inputHandler = inputHandler;
		_sdl = Sdl.GetApi();
	}

	public bool CanDisableVSync => true;

	public bool CanConfigureWindow => true;

	public WindowMode WindowMode => _fullscreen ? WindowMode.Fullscreen : WindowMode.Windowed;

	public IntPtr Initialize(Int2 initialSize, bool exactDrawableSize, string title)
	{
		_requestedSize = initialSize;
		_hasRequestedDrawableSize = exactDrawableSize;
		if (_sdl.Init(Sdl.InitVideo) < 0)
		{
			throw new InvalidOperationException("Failed to initialise SDL video subsystem.");
		}

		MacOSGestureResponder.EnableMomentumScrolling();

		var titlePtr = SilkMarshal.StringToPtr(title, NativeStringEncoding.UTF8);
		try
		{
			var flags = WindowFlags.Resizable | WindowFlags.AllowHighdpi | WindowFlags.Metal;
			_window = _sdl.CreateWindow(
				(byte*)titlePtr,
				Sdl.WindowposCentered,
				Sdl.WindowposCentered,
				initialSize.X,
				initialSize.Y,
				(uint)flags);
		}
		finally
		{
			SilkMarshal.Free(titlePtr);
		}

		if (_window is null)
		{
			throw new InvalidOperationException("Failed to create SDL window.");
		}

		CreateMetalView();
		ConfigureNativeWindowChrome();
		var contentView = ObjectiveC.IntPtr_objc_msgSend(_nativeWindow, "contentView");
		if (contentView != IntPtr.Zero)
		{
			_gestureResponder = new MacOSGestureResponder(
				contentView,
				_inputHandler.HandleMagnification,
				_inputHandler.HandleNativeScroll);
			_inputHandler.UseNativeUiScroll = true;
		}

		return GetLayer();
	}

	/// <summary>
	/// The drawable size SDL reports, except that a size a host requested explicitly is kept exactly so automation
	/// captures do not depend on the display scale. That stops as soon as the user resizes the window; from then on,
	/// SDL's drawable size is authoritative. In fullscreen, a resolution below native is the drawable size, and the
	/// layer scales it up to cover the display.
	/// </summary>
	public bool TryGetDrawableSize(out Int2 size)
	{
		size = default;
		if (_window is null)
		{
			return false;
		}

		if (TryGetWindowPixelSize(out var windowPixels) == false)
		{
			return false;
		}

		if (_fullscreen)
		{
			size = DisplayResolutions.ResolveFullscreen(_fullscreenResolution, windowPixels);
			return true;
		}

		var drawableWidth = windowPixels.X;
		var drawableHeight = windowPixels.Y;

		if (_hasRequestedDrawableSize)
		{
			var windowWidth = 0;
			var windowHeight = 0;
			_sdl.GetWindowSize(_window, ref windowWidth, ref windowHeight);
			if (windowWidth == _requestedSize.X && windowHeight == _requestedSize.Y)
			{
				drawableWidth = _requestedSize.X;
				drawableHeight = _requestedSize.Y;
			}
			else
			{
				_hasRequestedDrawableSize = false;
			}
		}

		if (drawableWidth <= 0 || drawableHeight <= 0)
		{
			return false;
		}

		size = new Int2(drawableWidth, drawableHeight);
		return true;
	}

	public bool TryGetWindowSize(out Int2 size)
	{
		size = default;
		if (_window is null)
		{
			return false;
		}

		var windowWidth = 0;
		var windowHeight = 0;
		_sdl.GetWindowSize(_window, ref windowWidth, ref windowHeight);
		if (windowWidth <= 0 || windowHeight <= 0)
		{
			return false;
		}

		size = new Int2(windowWidth, windowHeight);
		return true;
	}

	public void ApplyWindowConfiguration(WindowConfiguration configuration)
	{
		if (_window is null)
		{
			return;
		}

		if (configuration.Mode == WindowMode.Fullscreen)
		{
			_fullscreenResolution = configuration.Resolution;
			if (_fullscreen == false)
			{
				SetFullscreen(true);
			}

			return;
		}

		if (_fullscreen)
		{
			SetFullscreen(false);
		}

		var size = configuration.Resolution;
		if (size.X <= 0 || size.Y <= 0)
		{
			return;
		}

		_sdl.SetWindowSize(_window, size.X, size.Y);
		_sdl.SetWindowPosition(_window, Sdl.WindowposCentered, Sdl.WindowposCentered);

		// A size asked for explicitly is rendered at exactly that many pixels, as at startup.
		_requestedSize = size;
		_hasRequestedDrawableSize = true;
	}

	/// <summary>
	/// The size a fullscreen window gets on the window's display, in pixels of its backing store, so rendering at it
	/// maps one pixel to one pixel. On displays with a camera housing, macOS keeps fullscreen windows below it, so the
	/// screen's safe area is used rather than its whole frame.
	/// </summary>
	public bool TryGetNativeResolution(out Int2 resolution)
	{
		resolution = default;
		if (_nativeWindow == IntPtr.Zero)
		{
			return false;
		}

		// The safe area is in fractional points, so the window macOS actually gives is the exact answer once there.
		if (_fullscreen && TryGetWindowPixelSize(out resolution))
		{
			return true;
		}

		var screen =ObjectiveC.IntPtr_objc_msgSend(_nativeWindow, ScreenSelector);
		if (screen == IntPtr.Zero)
		{
			return false;
		}

		var frame = MsgSendRect(screen, FrameSelector.SelPtr);
		var insets = default(NativeEdgeInsets);
		if (MsgSendRespondsToSelector(screen, RespondsToSelectorSelector.SelPtr, SafeAreaInsetsSelector.SelPtr) != 0)
		{
			insets = MsgSendEdgeInsets(screen, SafeAreaInsetsSelector.SelPtr);
		}

		var scale = MsgSendDouble(screen, BackingScaleFactorSelector.SelPtr);
		var width = (frame.Width - insets.Left - insets.Right) * scale;
		var height = (frame.Height - insets.Top - insets.Bottom) * scale;
		if (width <= 0 || height <= 0)
		{
			return false;
		}

		resolution = new Int2((int)Math.Round(width), (int)Math.Round(height));
		return true;
	}

	/// <summary>
	/// The window's size in pixels of its backing store. SDL's Metal drawable size is the layer's drawable size, which
	/// the renderer sets itself below native resolution, so it cannot tell how large the window really is.
	/// </summary>
	private bool TryGetWindowPixelSize(out Int2 size)
	{
		size = default;
		if (TryGetWindowSize(out var points) == false)
		{
			return false;
		}

		var scale = _nativeWindow == IntPtr.Zero ? 0.0 : MsgSendDouble(_nativeWindow, BackingScaleFactorSelector.SelPtr);
		if (scale <= 0.0)
		{
			var drawableWidth = 0;
			var drawableHeight = 0;
			_sdl.MetalGetDrawableSize(_window, ref drawableWidth, ref drawableHeight);
			size = new Int2(drawableWidth, drawableHeight);

			return drawableWidth > 0 && drawableHeight > 0;
		}

		size = new Int2((int)Math.Round(points.X * scale), (int)Math.Round(points.Y * scale));
		return true;
	}

	public MetalSurfaceEvents PumpEvents()
	{
		var events = MetalSurfaceEvents.None;
		_gestureResponder?.EnsureAttached();
		_inputHandler.StartInputFrame();

		while (_sdl.PollEvent(ref _event) != 0)
		{
			switch ((EventType)_event.Type)
			{
				case EventType.Quit:
					events |= MetalSurfaceEvents.CloseRequested;
					break;

				case EventType.Windowevent:
					_inputHandler.HandleInputEvents(ref _event);
					events |= ReadWindowEvent(_event);
					SyncFullscreenState();
					break;

				case EventType.Mousebuttondown:
					if (HandleMacTitlebarDoubleClick(_event.Button))
					{
						break;
					}

					_inputHandler.HandleInputEvents(ref _event);
					break;

				default:
					_inputHandler.HandleInputEvents(ref _event);
					break;
			}
		}

		_gestureResponder?.EnsureAttached();
		_inputHandler.EndInputFrame();

		return events;
	}

	public void Shutdown()
	{
		_gestureResponder?.Dispose();
		_gestureResponder = null;
		_inputHandler.UseNativeUiScroll = false;

		if (_metalView is not null)
		{
			_sdl.MetalDestroyView(_metalView);
			_metalView = null;
		}

		if (_window is not null)
		{
			// The editor automation host stays alive after shutdown_editor, with its main thread waiting for the next
			// start instead of running the Cocoa run loop. Cocoa plays the close animation and takes the window off
			// screen only while that run loop runs, so without this the destroyed window stayed on screen,
			// unresponsive, until the process was killed. Close without the animation and pump once to commit it.
			if (_nativeWindow != IntPtr.Zero)
			{
				new NSWindowInstance(_nativeWindow).DisableAnimations();
				_nativeWindow = IntPtr.Zero;
			}

			_sdl.DestroyWindow(_window);
			_window = null;
			_sdl.PumpEvents();
		}

		_sdl.Quit();
	}

	/// <summary>
	/// Uses SDL's desktop fullscreen, which on macOS moves the window into its own Space with the system animation.
	/// The window's size changes over the following frames, which arrive as resize events.
	/// </summary>
	private void SetFullscreen(bool fullscreen)
	{
		var flags = fullscreen ? (uint)WindowFlags.FullscreenDesktop : 0u;
		if (_sdl.SetWindowFullscreen(_window, flags) != 0)
		{
			Console.Error.WriteLine($"Could not {(fullscreen ? "enter" : "leave")} fullscreen: {_sdl.GetErrorS()}");
			return;
		}

		_fullscreen = fullscreen;
	}

	/// <summary>The player can also leave fullscreen through the system, such as the window's green button.</summary>
	private void SyncFullscreenState()
	{
		var flags = _sdl.GetWindowFlags(_window);
		var fullscreenDesktop = (uint)WindowFlags.FullscreenDesktop;
		_fullscreen = (flags & fullscreenDesktop) == fullscreenDesktop;
	}

	private static MetalSurfaceEvents ReadWindowEvent(Event windowEvent)
	{
		if (windowEvent.Window.Event == (byte)WindowEventID.Close)
		{
			return MetalSurfaceEvents.CloseRequested;
		}

		if (windowEvent.Window.Event is (byte)WindowEventID.Resized or (byte)WindowEventID.SizeChanged)
		{
			return MetalSurfaceEvents.Resized;
		}

		return MetalSurfaceEvents.None;
	}

	private void CreateMetalView()
	{
		_metalView = _sdl.MetalCreateView(_window);
		if (_metalView is null)
		{
			throw new InvalidOperationException("Failed to create Metal view for SDL window.");
		}
	}

	/// <summary>
	/// Extends the content under a transparent title bar. The content view changes, so the Metal view is recreated
	/// on top of it afterwards.
	/// </summary>
	private void ConfigureNativeWindowChrome()
	{
		var nsView = new IntPtr(_metalView);
		var nsWindow = ObjectiveC.IntPtr_objc_msgSend(nsView, "window");
		if (nsWindow == IntPtr.Zero)
		{
			return;
		}

		_nativeWindow = nsWindow;
		var window = new NSWindowInstance(nsWindow);
		window.EnableUnifiedTitlebarChrome(includeFullSizeContentView: true);

		_sdl.MetalDestroyView(_metalView);
		_metalView = null;
		CreateMetalView();
	}

	private IntPtr GetLayer()
	{
		var layerPtr = _sdl.MetalGetLayer(_metalView);
		if (layerPtr is null)
		{
			throw new InvalidOperationException("Failed to retrieve CAMetalLayer from SDL view.");
		}

		return new IntPtr(layerPtr);
	}

	private bool HandleMacTitlebarDoubleClick(MouseButtonEvent buttonEvent)
	{
		if (_nativeWindow == IntPtr.Zero)
		{
			return false;
		}

		if (buttonEvent.Button != Sdl.ButtonLeft || buttonEvent.Clicks < 2)
		{
			return false;
		}

		if (buttonEvent.Y > MacTitlebarDoubleClickHeight || buttonEvent.X < MacTrafficLightExclusionWidth)
		{
			return false;
		}

		ObjectiveC.objc_msgSend(_nativeWindow, "zoom:", IntPtr.Zero);
		return true;
	}

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
	private static extern double MsgSendDouble(IntPtr receiver, IntPtr selector);

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
	private static extern byte MsgSendRespondsToSelector(IntPtr receiver, IntPtr selector, IntPtr queriedSelector);

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
	private static extern NativeRect MsgSendRect(IntPtr receiver, IntPtr selector);

	[DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
	private static extern NativeEdgeInsets MsgSendEdgeInsets(IntPtr receiver, IntPtr selector);

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect
	{
		public double X;
		public double Y;
		public double Width;
		public double Height;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeEdgeInsets
	{
		public double Top;
		public double Left;
		public double Bottom;
		public double Right;
	}
}
