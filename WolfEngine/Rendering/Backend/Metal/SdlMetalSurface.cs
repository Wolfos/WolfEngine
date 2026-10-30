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
	private const string WindowTitle = "WolfEngine";
	private const int MacTitlebarDoubleClickHeight = 28;
	private const int MacTrafficLightExclusionWidth = 70;

	private readonly IMacOSInputHandler _inputHandler;
	private readonly Sdl _sdl;
	private Window* _window;
	private void* _metalView;
	private IntPtr _nativeWindow = IntPtr.Zero;
	private Int2 _requestedSize;
	private bool _hasRequestedDrawableSize;
	private Event _event;
	private MacOSGestureResponder? _gestureResponder;

	public SdlMetalSurface(IMacOSInputHandler inputHandler)
	{
		_inputHandler = inputHandler;
		_sdl = Sdl.GetApi();
	}

	public bool CanDisableVSync => true;

	public IntPtr Initialize(Int2 initialSize, bool exactDrawableSize)
	{
		_requestedSize = initialSize;
		_hasRequestedDrawableSize = exactDrawableSize;
		if (_sdl.Init(Sdl.InitVideo) < 0)
		{
			throw new InvalidOperationException("Failed to initialise SDL video subsystem.");
		}

		MacOSGestureResponder.EnableMomentumScrolling();

		var titlePtr = SilkMarshal.StringToPtr(WindowTitle, NativeStringEncoding.UTF8);
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
	/// SDL's drawable size is authoritative.
	/// </summary>
	public bool TryGetDrawableSize(out Int2 size)
	{
		size = default;
		if (_window is null)
		{
			return false;
		}

		var drawableWidth = 0;
		var drawableHeight = 0;
		_sdl.MetalGetDrawableSize(_window, ref drawableWidth, ref drawableHeight);

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
			_sdl.DestroyWindow(_window);
			_window = null;
		}

		_sdl.Quit();
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
}
