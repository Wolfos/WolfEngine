using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using WolfEngine.Mathematics;
using WolfEngine.Input;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.D3D12;
using WolfEngine.Rendering.UI;
using AbstractionFillMode = WolfEngine.Rendering.Abstraction.FillMode;
using AbstractionCullMode = WolfEngine.Rendering.Abstraction.CullMode;
using AbstractionDepthStencilFormat = WolfEngine.Rendering.Abstraction.DepthStencilFormat;
using D3DVertexBufferView = Silk.NET.Direct3D12.VertexBufferView;
using D3DIndexBufferView = Silk.NET.Direct3D12.IndexBufferView;
using Range = Silk.NET.Direct3D12.Range;

namespace WolfEngine;

public unsafe class WolfRendererD3D : IRenderer
{
private const int FrameCount = 2;

private readonly float[] _backgroundColour = [0.392f, 0.584f, 0.929f, 1.0f];
	
private sealed class MeshResources
{
		public MeshResources(
			ComPtr<ID3D12Resource> vertexBuffer,
			ComPtr<ID3D12Resource> indexBuffer,
			D3DVertexBufferView vertexView,
			D3DIndexBufferView indexView,
			uint indexCount)
		{
			VertexBuffer = vertexBuffer;
			IndexBuffer = indexBuffer;
			VertexView = vertexView;
			IndexView = indexView;
			IndexCount = indexCount;
		}

		public ComPtr<ID3D12Resource> VertexBuffer { get; }

		public ComPtr<ID3D12Resource> IndexBuffer { get; }

		public D3DVertexBufferView VertexView { get; }

		public D3DIndexBufferView IndexView { get; }

		public uint IndexCount { get; }
	}

	private struct VertexData
	{
		public Vector4 Position;
		public Vector3 Normal;
		public float Padding;
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

	private readonly int _width;
	private readonly int _height;
	private readonly IShaderCompiler _shaderCompiler;
	private readonly IArenaAllocator _arenaAllocator;
	private readonly IInputSystem _inputSystem;
	private IWindow _window = null!;
	private IInputContext _inputContext = null!;
	private Action _startupCallback = static () => { };
	private Action<float> _updateCallback = static deltaTime => { };
	private Action<float> _renderCallback = static deltaTime => { };
	private bool _isInitialized;

	private DXGI _dxgi = null!;
	private D3D12 _d3d12 = null!;
	private ComPtr<IDXGIFactory2> _factory;
	private ComPtr<IDXGISwapChain3> _swapchain;
	private ComPtr<ID3D12Device> _device;
	private ComPtr<IDXGIAdapter> _adapter = default;
	private ComPtr<ID3D12CommandQueue> _commandQueue;
	private D3D12Device _gfxDevice = null!;

	private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
	private uint _rtvDescriptorSize;
	private readonly CpuDescriptorHandle[] _rtvCpuHandles = new CpuDescriptorHandle[FrameCount];
	private readonly ulong[] _frameFenceValues = new ulong[FrameCount];
	private readonly ComPtr<ID3D12Resource>[] _renderTargets = new ComPtr<ID3D12Resource>[FrameCount];
	private readonly ComPtr<ID3D12CommandAllocator>[] _commandAllocators =
		new ComPtr<ID3D12CommandAllocator>[FrameCount];

	private ComPtr<ID3D12Fence> _fence;
	private ulong _fenceValue;
	private nint _fenceEvent = nint.Zero;
	private readonly Dictionary<Mesh, MeshResources> _meshResources = new();

	private uint _backbufferIndex;
	private nint _windowHandle;
	private Int2 _framebufferSize = Int2.Zero;
	private readonly List<IKeyboard> _keyboards = new();
	private readonly List<IMouse> _mice = new();
	private Vector2 _lastMousePosition;
	private bool _hasMousePosition;
	private readonly IImGuiInputSink _imguiInputSink;
	private readonly bool[] _imguiMouseButtons = new bool[5];
	private Vector2 _imguiMousePosition;
	private Vector2 _imguiMouseWheel;
	private D3D12ImGuiRenderer _imguiRenderer = null!;

	public WolfRendererD3D(IShaderCompiler shaderCompiler, IArenaAllocator arenaAllocator, IInputSystem inputSystem, ImGuiUiSystem imguiSystem)
	{
		_width = 1280;
		_height = 720;
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_arenaAllocator = arenaAllocator ?? throw new ArgumentNullException(nameof(arenaAllocator));
		_inputSystem = inputSystem ?? throw new ArgumentNullException(nameof(inputSystem));
		_imguiInputSink = imguiSystem ?? throw new ArgumentNullException(nameof(imguiSystem));
	}

	public void Run(Action startup, Action<float> update, Action<float> render)
	{
		_startupCallback = startup;
		_updateCallback = update;
		_renderCallback = render;

		InitializeWindow();

		var window = _window ?? throw new InvalidOperationException("Window was not initialised.");

		window.Load += OnWindowLoad;
		window.Update += OnWindowUpdate;
		window.Render += OnWindowRender;
		window.FramebufferResize += OnWindowFramebufferResize;
		window.Closing += OnWindowClosing;

		try
		{
			window.Run();
		}
		finally
		{
			Dispose();
		}
	}


	private void OnWindowLoad()
	{
		RetrieveNativeHandle();
		UpdateFramebufferSize();

		OnLoad();

		if (_window is null)
		{
			throw new InvalidOperationException("Window was not initialised.");
		}

		_inputContext = _window.CreateInput();
		HookKeyboards();
		HookMice();

		_startupCallback();
		// Command processing is now handled by the render graph
		_isInitialized = true;
	}

	private void OnWindowUpdate(double deltaTime)
	{
		PollGamepads();

		_updateCallback((float) deltaTime);
		OnUpdate((float) deltaTime);
	}

	private void OnWindowRender(double deltaTime)
	{
		if (_isInitialized == false)
		{
			return;
		}

		if (_framebufferSize.X <= 0 || _framebufferSize.Y <= 0)
		{
			return;
		}

		_renderCallback((float) deltaTime);
	}

	private void OnWindowFramebufferResize(Vector2D<int> newSize)
	{
		if (_isInitialized == false)
		{
			if (newSize.X > 0 && newSize.Y > 0)
			{
				_framebufferSize = new(newSize.X, newSize.Y);
			}

			return;
		}

		OnFramebufferResize(new(newSize.X, newSize.Y));
	}

	private void OnWindowClosing()
	{
		_isInitialized = false;
	}

	private void HookKeyboards()
	{
		_keyboards.Clear();
		if (_inputContext is null)
		{
			return;
		}

		foreach (var keyboard in _inputContext.Keyboards)
		{
			_keyboards.Add(keyboard);
			keyboard.KeyDown += HandleKeyDown;
			keyboard.KeyUp += HandleKeyUp;
			keyboard.KeyChar += HandleKeyChar;
		}
	}

	private void HookMice()
	{
		_mice.Clear();
		if (_inputContext is null)
		{
			return;
		}

		foreach (var mouse in _inputContext.Mice)
		{
			_mice.Add(mouse);
			mouse.MouseMove += HandleMouseMove;
			mouse.Scroll += HandleMouseScroll;
			mouse.MouseDown += HandleMouseDown;
			mouse.MouseUp += HandleMouseUp;
		}
	}

	private void HandleKeyDown(IKeyboard keyboard, Key key, int keycode)
	{
		if (TryMapKey(key, out var binding))
		{
			_inputSystem.SetButton(binding, true);
		}
		if (TryConvertKey(key, out var imguiKey))
		{
			_imguiInputSink.SetKey(imguiKey, true);
		}

		if (key == Key.Escape)
		{
			_window?.Close();
		}
	}

	private void HandleKeyUp(IKeyboard keyboard, Key key, int keycode)
	{
		if (TryMapKey(key, out var binding))
		{
			_inputSystem.SetButton(binding, false);
		}
		if (TryConvertKey(key, out var imguiKey))
		{
			_imguiInputSink.SetKey(imguiKey, false);
		}
	}

	private void HandleKeyChar(IKeyboard keyboard, char keyChar)
	{
		_imguiInputSink.AddChar(keyChar);
	}

	private void HandleMouseMove(IMouse mouse, Vector2 position)
	{
		var current = position;
		_inputSystem.SetAxis2D(InputActionBinding.MousePosition, current);
		_imguiMousePosition = current;
		_imguiInputSink.SetMousePosition(current);

		if (_hasMousePosition)
		{
			var delta = current - _lastMousePosition;
			_inputSystem.SetAxis2D(InputActionBinding.MouseDelta, delta);
		}

		_lastMousePosition = current;
		_hasMousePosition = true;
	}

	private void HandleMouseScroll(IMouse mouse, ScrollWheel scrollWheel)
	{
		var scroll = new Vector2((float) scrollWheel.X, (float) scrollWheel.Y);
		_inputSystem.SetAxis2D(InputActionBinding.MouseScroll, scroll);
		_imguiMouseWheel += scroll;
		_imguiInputSink.AddMouseScroll(scroll);
	}

	private void HandleMouseDown(IMouse mouse, MouseButton button)
	{
		if (TryMapMouseButton(button, out var binding))
		{
			_inputSystem.SetButton(binding, true);
		}

		if (button == MouseButton.Left) _imguiMouseButtons[0] = true;
		if (button == MouseButton.Right) _imguiMouseButtons[1] = true;
		if (button == MouseButton.Middle) _imguiMouseButtons[2] = true;
		NotifyImGuiMouseButton(button, true);
	}

	private void HandleMouseUp(IMouse mouse, MouseButton button)
	{
		if (TryMapMouseButton(button, out var binding))
		{
			_inputSystem.SetButton(binding, false);
		}

		if (button == MouseButton.Left) _imguiMouseButtons[0] = false;
		if (button == MouseButton.Right) _imguiMouseButtons[1] = false;
		if (button == MouseButton.Middle) _imguiMouseButtons[2] = false;
		NotifyImGuiMouseButton(button, false);
	}

	private void NotifyImGuiMouseButton(MouseButton button, bool down)
	{
		var index = button switch
		{
			MouseButton.Left => 0,
			MouseButton.Right => 1,
			MouseButton.Middle => 2,
			MouseButton.Button4 => 3,
			MouseButton.Button5 => 4,
			_ => -1
		};

		if (index >= 0)
		{
			_imguiInputSink.SetMouseButton(index, down);
		}
	}

	private static bool TryConvertKey(Key key, out ImGuiKey imguiKey)
	{
		imguiKey = key switch
		{
			Key.Tab => ImGuiKey.Tab,
			Key.ShiftLeft => ImGuiKey.LeftShift,
			Key.ShiftRight => ImGuiKey.RightShift,
			Key.ControlLeft => ImGuiKey.LeftCtrl,
			Key.ControlRight => ImGuiKey.RightCtrl,
			Key.AltLeft => ImGuiKey.LeftAlt,
			Key.AltRight => ImGuiKey.RightAlt,
			Key.SuperLeft => ImGuiKey.LeftSuper,
			Key.SuperRight => ImGuiKey.RightSuper,
			Key.Menu => ImGuiKey.Menu,
			Key.Up => ImGuiKey.UpArrow,
			Key.Down => ImGuiKey.DownArrow,
			Key.Left => ImGuiKey.LeftArrow,
			Key.Right => ImGuiKey.RightArrow,
			Key.Escape => ImGuiKey.Escape,
			Key.Enter => ImGuiKey.Enter,
			Key.Space => ImGuiKey.Space,
			Key.Backspace => ImGuiKey.Backspace,
			Key.Insert => ImGuiKey.Insert,
			Key.Delete => ImGuiKey.Delete,
			Key.Home => ImGuiKey.Home,
			Key.End => ImGuiKey.End,
			Key.PageUp => ImGuiKey.PageUp,
			Key.PageDown => ImGuiKey.PageDown,
			Key.A => ImGuiKey.A,
			Key.C => ImGuiKey.C,
			Key.V => ImGuiKey.V,
			Key.X => ImGuiKey.X,
			Key.Y => ImGuiKey.Y,
			Key.Z => ImGuiKey.Z,
			Key.Number0 => ImGuiKey._0,
			Key.Number1 => ImGuiKey._1,
			Key.Number2 => ImGuiKey._2,
			Key.Number3 => ImGuiKey._3,
			Key.Number4 => ImGuiKey._4,
			Key.Number5 => ImGuiKey._5,
			Key.Number6 => ImGuiKey._6,
			Key.Number7 => ImGuiKey._7,
			Key.Number8 => ImGuiKey._8,
			Key.Number9 => ImGuiKey._9,
			Key.F1 => ImGuiKey.F1,
			Key.F2 => ImGuiKey.F2,
			Key.F3 => ImGuiKey.F3,
			Key.F4 => ImGuiKey.F4,
			Key.F5 => ImGuiKey.F5,
			Key.F6 => ImGuiKey.F6,
			Key.F7 => ImGuiKey.F7,
			Key.F8 => ImGuiKey.F8,
			Key.F9 => ImGuiKey.F9,
			Key.F10 => ImGuiKey.F10,
			Key.F11 => ImGuiKey.F11,
			Key.F12 => ImGuiKey.F12,
			Key.GraveAccent => ImGuiKey.GraveAccent,
			Key.Minus => ImGuiKey.Minus,
			Key.Equal => ImGuiKey.Equal,
			Key.LeftBracket => ImGuiKey.LeftBracket,
			Key.RightBracket => ImGuiKey.RightBracket,
			Key.Semicolon => ImGuiKey.Semicolon,
			Key.Apostrophe => ImGuiKey.Apostrophe,
			Key.BackSlash => ImGuiKey.Backslash,
			Key.Comma => ImGuiKey.Comma,
			Key.Period => ImGuiKey.Period,
			Key.Slash => ImGuiKey.Slash,
			Key.Keypad0 => ImGuiKey.Keypad0,
			Key.Keypad1 => ImGuiKey.Keypad1,
			Key.Keypad2 => ImGuiKey.Keypad2,
			Key.Keypad3 => ImGuiKey.Keypad3,
			Key.Keypad4 => ImGuiKey.Keypad4,
			Key.Keypad5 => ImGuiKey.Keypad5,
			Key.Keypad6 => ImGuiKey.Keypad6,
			Key.Keypad7 => ImGuiKey.Keypad7,
			Key.Keypad8 => ImGuiKey.Keypad8,
			Key.Keypad9 => ImGuiKey.Keypad9,
			Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
			Key.KeypadDivide => ImGuiKey.KeypadDivide,
			Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
			Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
			Key.KeypadAdd => ImGuiKey.KeypadAdd,
			Key.KeypadEnter => ImGuiKey.KeypadEnter,
			_ => ImGuiKey.None
		};

		return imguiKey != ImGuiKey.None;
	}

	private void InitializeWindow()
	{
		var options = WindowOptions.Default;
		options.Title = "WolfEngine";
		options.Size = new(_width, _height);
		options.API = GraphicsAPI.None;

		_window = Window.Create(options);
	}

	private void RetrieveNativeHandle()
	{
		if (_window is null)
		{
			throw new InvalidOperationException("Window was not initialised.");
		}

		var win32 = _window.Native.Win32;
		if (win32 is null)
		{
			throw new InvalidOperationException("Direct3D renderer requires a Win32 window handle.");
		}

		_windowHandle = win32.Value.Hwnd;
		if (_windowHandle == nint.Zero)
		{
			throw new InvalidOperationException("Windowing subsystem reported a null window handle.");
		}
	}

	private void UpdateFramebufferSize()
	{
		if (_window is null)
		{
			return;
		}

		var size = _window.FramebufferSize;
		if (size.X > 0 && size.Y > 0)
		{
			_framebufferSize = new(size.X, size.Y);
		}
	}

	private void PollGamepads()
	{
		if (_inputContext is null)
		{
			return;
		}

		foreach (var gamepad in _inputContext.Gamepads)
		{
			foreach (var button in gamepad.Buttons)
			{
				if (TryMapGamepadButton(button.Name, out var binding))
				{
					_inputSystem.SetButton(binding, button.Pressed);
				}
			}

			if (gamepad.Thumbsticks.Count > 0)
			{
				var leftThumb = gamepad.Thumbsticks[0];
				var leftStick = new Vector2((float) leftThumb.X, (float) leftThumb.Y);
				_inputSystem.SetAxis2D(InputActionBinding.GamepadLeftStick, leftStick);
			}

			if (gamepad.Thumbsticks.Count > 1)
			{
				var rightThumb = gamepad.Thumbsticks[1];
				var rightStick = new Vector2((float) rightThumb.X, (float) rightThumb.Y);
				_inputSystem.SetAxis2D(InputActionBinding.GamepadRightStick, rightStick);
			}

			if (gamepad.Triggers.Count > 0)
			{
				_inputSystem.SetAxis1D(InputActionBinding.GamepadLeftTrigger, gamepad.Triggers[0].Position);
			}

			if (gamepad.Triggers.Count > 1)
			{
				_inputSystem.SetAxis1D(InputActionBinding.GamepadRightTrigger, gamepad.Triggers[1].Position);
			}
		}
	}

	private static bool TryMapMouseButton(MouseButton button, out InputActionBinding binding)
	{
		binding = button switch
		{
			MouseButton.Left => InputActionBinding.MouseButtonLeft,
			MouseButton.Right => InputActionBinding.MouseButtonRight,
			MouseButton.Middle => InputActionBinding.MouseButtonMiddle,
			MouseButton.Button4 => InputActionBinding.MouseButton4,
			MouseButton.Button5 => InputActionBinding.MouseButton5,
			_ => InputActionBinding.None
		};

		return binding != InputActionBinding.None;
	}

	private static bool TryMapGamepadButton(ButtonName name, out InputActionBinding binding)
	{
		binding = name switch
		{
			ButtonName.A => InputActionBinding.GamepadFaceSouth,
			ButtonName.B => InputActionBinding.GamepadFaceEast,
			ButtonName.X => InputActionBinding.GamepadFaceWest,
			ButtonName.Y => InputActionBinding.GamepadFaceNorth,
			ButtonName.LeftBumper => InputActionBinding.GamepadLeftBumper,
			ButtonName.RightBumper => InputActionBinding.GamepadRightBumper,
			ButtonName.LeftStick => InputActionBinding.GamepadLeftStickButton,
			ButtonName.RightStick => InputActionBinding.GamepadRightStickButton,
			ButtonName.DPadUp => InputActionBinding.GamepadDpadUp,
			ButtonName.DPadDown => InputActionBinding.GamepadDpadDown,
			ButtonName.DPadLeft => InputActionBinding.GamepadDpadLeft,
			ButtonName.DPadRight => InputActionBinding.GamepadDpadRight,
			ButtonName.Back => InputActionBinding.GamepadBack,
			ButtonName.Start => InputActionBinding.GamepadStart,
			ButtonName.Home => InputActionBinding.GamepadGuide,
			_ => InputActionBinding.None
		};

		return binding != InputActionBinding.None;
	}

	private static bool TryMapKey(Key key, out InputActionBinding binding)
	{
		binding = key switch
		{
			Key.A => InputActionBinding.KeyA,
			Key.B => InputActionBinding.KeyB,
			Key.C => InputActionBinding.KeyC,
			Key.D => InputActionBinding.KeyD,
			Key.E => InputActionBinding.KeyE,
			Key.F => InputActionBinding.KeyF,
			Key.G => InputActionBinding.KeyG,
			Key.H => InputActionBinding.KeyH,
			Key.I => InputActionBinding.KeyI,
			Key.J => InputActionBinding.KeyJ,
			Key.K => InputActionBinding.KeyK,
			Key.L => InputActionBinding.KeyL,
			Key.M => InputActionBinding.KeyM,
			Key.N => InputActionBinding.KeyN,
			Key.O => InputActionBinding.KeyO,
			Key.P => InputActionBinding.KeyP,
			Key.Q => InputActionBinding.KeyQ,
			Key.R => InputActionBinding.KeyR,
			Key.S => InputActionBinding.KeyS,
			Key.T => InputActionBinding.KeyT,
			Key.U => InputActionBinding.KeyU,
			Key.V => InputActionBinding.KeyV,
			Key.W => InputActionBinding.KeyW,
			Key.X => InputActionBinding.KeyX,
			Key.Y => InputActionBinding.KeyY,
			Key.Z => InputActionBinding.KeyZ,
			Key.Number0 => InputActionBinding.Key0,
			Key.Number1 => InputActionBinding.Key1,
			Key.Number2 => InputActionBinding.Key2,
			Key.Number3 => InputActionBinding.Key3,
			Key.Number4 => InputActionBinding.Key4,
			Key.Number5 => InputActionBinding.Key5,
			Key.Number6 => InputActionBinding.Key6,
			Key.Number7 => InputActionBinding.Key7,
			Key.Number8 => InputActionBinding.Key8,
			Key.Number9 => InputActionBinding.Key9,
			Key.F1 => InputActionBinding.KeyF1,
			Key.F2 => InputActionBinding.KeyF2,
			Key.F3 => InputActionBinding.KeyF3,
			Key.F4 => InputActionBinding.KeyF4,
			Key.F5 => InputActionBinding.KeyF5,
			Key.F6 => InputActionBinding.KeyF6,
			Key.F7 => InputActionBinding.KeyF7,
			Key.F8 => InputActionBinding.KeyF8,
			Key.F9 => InputActionBinding.KeyF9,
			Key.F10 => InputActionBinding.KeyF10,
			Key.F11 => InputActionBinding.KeyF11,
			Key.F12 => InputActionBinding.KeyF12,
			Key.Escape => InputActionBinding.KeyEscape,
			Key.Tab => InputActionBinding.KeyTab,
			Key.CapsLock => InputActionBinding.KeyCapsLock,
			Key.ShiftLeft => InputActionBinding.KeyLeftShift,
			Key.ShiftRight => InputActionBinding.KeyRightShift,
			Key.ControlLeft => InputActionBinding.KeyLeftControl,
			Key.ControlRight => InputActionBinding.KeyRightControl,
			Key.AltLeft => InputActionBinding.KeyLeftAlt,
			Key.AltRight => InputActionBinding.KeyRightAlt,
			Key.SuperLeft => InputActionBinding.KeyLeftSuper,
			Key.SuperRight => InputActionBinding.KeyRightSuper,
			Key.Menu => InputActionBinding.KeyMenu,
			Key.Space => InputActionBinding.KeySpace,
			Key.Enter => InputActionBinding.KeyEnter,
			Key.Backspace => InputActionBinding.KeyBackspace,
			Key.Insert => InputActionBinding.KeyInsert,
			Key.Delete => InputActionBinding.KeyDelete,
			Key.Home => InputActionBinding.KeyHome,
			Key.End => InputActionBinding.KeyEnd,
			Key.PageUp => InputActionBinding.KeyPageUp,
			Key.PageDown => InputActionBinding.KeyPageDown,
			Key.Up => InputActionBinding.KeyArrowUp,
			Key.Down => InputActionBinding.KeyArrowDown,
			Key.Left => InputActionBinding.KeyArrowLeft,
			Key.Right => InputActionBinding.KeyArrowRight,
			Key.Minus => InputActionBinding.KeyMinus,
			Key.Equal => InputActionBinding.KeyEquals,
			Key.LeftBracket => InputActionBinding.KeyLeftBracket,
			Key.RightBracket => InputActionBinding.KeyRightBracket,
			Key.BackSlash => InputActionBinding.KeyBackslash,
			Key.Semicolon => InputActionBinding.KeySemicolon,
			Key.Apostrophe => InputActionBinding.KeyApostrophe,
			Key.GraveAccent => InputActionBinding.KeyGrave,
			Key.Comma => InputActionBinding.KeyComma,
			Key.Period => InputActionBinding.KeyPeriod,
			Key.Slash => InputActionBinding.KeySlash,
			Key.PrintScreen => InputActionBinding.KeyPrintScreen,
			Key.ScrollLock => InputActionBinding.KeyScrollLock,
			Key.Pause => InputActionBinding.KeyPause,
			Key.NumLock => InputActionBinding.KeyNumLock,
			Key.Keypad0 => InputActionBinding.KeyNumpad0,
			Key.Keypad1 => InputActionBinding.KeyNumpad1,
			Key.Keypad2 => InputActionBinding.KeyNumpad2,
			Key.Keypad3 => InputActionBinding.KeyNumpad3,
			Key.Keypad4 => InputActionBinding.KeyNumpad4,
			Key.Keypad5 => InputActionBinding.KeyNumpad5,
			Key.Keypad6 => InputActionBinding.KeyNumpad6,
			Key.Keypad7 => InputActionBinding.KeyNumpad7,
			Key.Keypad8 => InputActionBinding.KeyNumpad8,
			Key.Keypad9 => InputActionBinding.KeyNumpad9,
			Key.KeypadDivide => InputActionBinding.KeyNumpadDivide,
			Key.KeypadMultiply => InputActionBinding.KeyNumpadMultiply,
			Key.KeypadSubtract => InputActionBinding.KeyNumpadSubtract,
			Key.KeypadAdd => InputActionBinding.KeyNumpadAdd,
			Key.KeypadDecimal => InputActionBinding.KeyNumpadDecimal,
			Key.KeypadEnter => InputActionBinding.KeyNumpadEnter,
			_ => InputActionBinding.None
		};

		return binding != InputActionBinding.None;
	}

	private void OnLoad()
	{
#pragma warning disable CS0618
		_dxgi = DXGI.GetApi();
#pragma warning restore CS0618
		_d3d12 = D3D12.GetApi();
		EnableDebugLayerIfRequested();

		CreateDeviceAndQueue();
		CreateSwapchain();
		CreateRtvHeapAndTargets();
		CreateCommandAllocatorsAndList();
		CreateSyncObjects();
		_imguiRenderer = new D3D12ImGuiRenderer(_device, _shaderCompiler);
	}

	private void CreateDeviceAndQueue()
	{
		SilkMarshal.ThrowHResult(
			_d3d12.CreateDevice(
				_adapter,
				D3DFeatureLevel.Level122,
				out _device));
		var commandQueueDescription = new CommandQueueDesc(
			type: CommandListType.Direct,
			priority: (int) CommandQueuePriority.Normal,
			flags: CommandQueueFlags.None);

		SilkMarshal.ThrowHResult(_device.CreateCommandQueue(in commandQueueDescription, out _commandQueue));

		_gfxDevice = new(_device, _commandQueue);
	}

	private void EnableDebugLayerIfRequested()
	{
		if (GraphicsConfig.EnableD3DDebugLayer == false)
		{
			return;
		}

		if (OperatingSystem.IsWindows() == false)
		{
			return;
		}

		try
		{
			ComPtr<ID3D12Debug> debug = default;
			var hr = _d3d12.GetDebugInterface(out debug);
			if (hr >= 0 && debug.Handle is not null)
			{
				debug.EnableDebugLayer();
			}

			debug.Dispose();
		}
		catch
		{
			// Swallow exceptions; debug layer enable is best-effort.
		}
	}

	public IMaterialResources CreateMaterialResources(Material material)
	{
		if (material is null)
		{
			throw new ArgumentNullException(nameof(material));
		}

		var vertexShaderBytes = _shaderCompiler.GetDxil(material.ShaderPath, "vertexShader", "vs_6_0");
		var pixelShaderBytes = _shaderCompiler.GetDxil(material.ShaderPath, "fragmentShader", "ps_6_0");

		var colorSize = Align((ulong) Unsafe.SizeOf<Vector4>(),
			D3D12.ConstantBufferDataPlacementAlignment);
		var uploadProps = new HeapProperties(HeapType.Upload);
		var bufferDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = colorSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};

		ComPtr<ID3D12Resource> colorBuffer;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&uploadProps,
				HeapFlags.None,
				in bufferDesc,
				ResourceStates.GenericRead,
				null,
				out colorBuffer));

		void* mappedData = null;
		SilkMarshal.ThrowHResult(colorBuffer.Map(0, (Range*) null, &mappedData));
		try
		{
			var color = material.Color;
			Unsafe.Write((Vector4*) mappedData, color);
		}
		finally
		{
			colorBuffer.Unmap(0, (Range*) null);
		}

		// Wrap in abstraction interfaces
		var renderState = new RenderStateDescriptor(
			AbstractionFillMode.Solid,
			AbstractionCullMode.Back,
			depthTestEnabled: true,
			depthWriteEnabled: true,
			BlendMode.Opaque);

		var pipelineKey = new PipelineKey(
			PassKind.Graphics,
			vertexEntryPoint: "vertexShader",
			pixelEntryPoint: "fragmentShader",
			computeEntryPoint: null,
			renderTargets: new(new[]
			{
				TextureFormat.Bgra8Unorm,     // Albedo
				TextureFormat.Rgba16Float,    // Normal
				TextureFormat.Rgba8Unorm      // Material
			}),
			depthStencil: new AbstractionDepthStencilFormat(TextureFormat.D32Float),
			renderState: renderState);

		var shaderSet = new ShaderBytecodeSet(vertexShaderBytes, pixelShaderBytes);
		var pipeline = _gfxDevice.GetOrCreatePipeline(pipelineKey, shaderSet);

		var constantBuffer = new D3D12Buffer(
			$"{material.ShaderPath}_ColorBuffer",
			new(colorSize, BufferUsage.Constant),
			colorBuffer,
			colorSize);

		return new D3D12MaterialResources
		{
			Pipeline = pipeline,
			ConstantBuffer = constantBuffer
		};
	}

	public IGfxDevice GetGfxDevice()
	{
		return _gfxDevice;
	}

	public Int2 GetFrameBufferSize()
	{
		return _framebufferSize;
	}

	private void CreateSwapchain()
	{
		var swapChainDesc = new SwapChainDesc1
		{
			BufferCount = FrameCount,
			Format = Format.FormatB8G8R8A8Unorm,
			BufferUsage = DXGI.UsageRenderTargetOutput,
			SwapEffect = SwapEffect.FlipDiscard,
			SampleDesc = new(1, 0),
			Width = (uint) Math.Max(_framebufferSize.X, 1),
			Height = (uint) Math.Max(_framebufferSize.Y, 1)
		};

		_factory = _dxgi.CreateDXGIFactory<IDXGIFactory2>();

		var factoryPtr = _factory.Handle;
		var queuePtr = (IUnknown*) _commandQueue.Handle;
		IDXGISwapChain1* swapChain1 = null;
		var swapchainResult = factoryPtr->CreateSwapChainForHwnd(
			queuePtr,
			_windowHandle,
			&swapChainDesc,
			null,
			null,
			&swapChain1);
		SilkMarshal.ThrowHResult(swapchainResult);

		IDXGISwapChain3* swapChain3 = null;
		var swapChain3Guid = IDXGISwapChain3.Guid;
		SilkMarshal.ThrowHResult(swapChain1->QueryInterface(ref swapChain3Guid, (void**) &swapChain3));
		_swapchain = new(swapChain3);

		swapChain1->Release();

		_backbufferIndex = _swapchain.GetCurrentBackBufferIndex();
	}

	private void CreateRtvHeapAndTargets()
	{
		var rtvHeapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.Rtv,
			NumDescriptors = FrameCount,
			Flags = DescriptorHeapFlags.None,
			NodeMask = 0
		};

		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap(in rtvHeapDesc, out _rtvHeap));
		_rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);

		var rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
		for (var i = 0; i < FrameCount; i++)
		{
			_rtvCpuHandles[i] = rtvHandle;
			SilkMarshal.ThrowHResult(_swapchain.GetBuffer((uint) i, out _renderTargets[i]));
			_device.CreateRenderTargetView(_renderTargets[i], null, rtvHandle);
			rtvHandle.Ptr += _rtvDescriptorSize;
		}
	}

	private void CreateCommandAllocatorsAndList()
	{
		for (var i = 0; i < FrameCount; i++)
		{
			SilkMarshal.ThrowHResult(_device.CreateCommandAllocator(CommandListType.Direct, out _commandAllocators[i]));
		}
		// Command lists are now created per-pass by the render graph
	}

	private void CreateSyncObjects()
	{
		SilkMarshal.ThrowHResult(_device.CreateFence(0, FenceFlags.None, out _fence));
		_fenceValue = 0;
		_fenceEvent = CreateEventEx(nint.Zero, null, 0, 0x1F0003);
		if (_fenceEvent == nint.Zero)
		{
			throw new InvalidOperationException("Failed to create fence event.");
		}
	}


	private MeshResources CreateMeshResources(Mesh mesh)
	{
		var vertexCount = mesh.Vertices.Length;
		if (vertexCount == 0)
		{
			throw new InvalidOperationException("Mesh must contain vertex data.");
		}

		var vertices = new VertexData[vertexCount];
		for (var i = 0; i < vertexCount; i++)
		{
			vertices[i].Position = mesh.Vertices[i];
			vertices[i].Normal = i < mesh.Normals.Length ? mesh.Normals[i] : Vector3.UnitY;
			vertices[i].Padding = 0.0f;
		}

		var vertexStride = (uint) Unsafe.SizeOf<VertexData>();
		var vertexBufferSize = (ulong) (vertexStride * (uint) vertexCount);
		var vertexBufferDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = vertexBufferSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};

		var defaultHeapProps = new HeapProperties(HeapType.Default);
		var uploadHeapProps = new HeapProperties(HeapType.Upload);

		ComPtr<ID3D12Resource> vertexBuffer;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&defaultHeapProps,
				HeapFlags.None,
				in vertexBufferDesc,
				ResourceStates.CopyDest,
				null,
				out vertexBuffer));

		ComPtr<ID3D12Resource> vertexUpload;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&uploadHeapProps,
				HeapFlags.None,
				in vertexBufferDesc,
				ResourceStates.GenericRead,
				null,
				out vertexUpload));

		void* mappedVertices = null;
		SilkMarshal.ThrowHResult(vertexUpload.Map(0, (Range*) null, &mappedVertices));
		try
		{
			fixed (VertexData* srcVertices = vertices)
			{
				Buffer.MemoryCopy(srcVertices, mappedVertices, vertexBufferSize, vertexBufferSize);
			}
		}
		finally
		{
			vertexUpload.Unmap(0, (Range*) null);
		}

		var indices = mesh.Indices;
		if (indices.Length == 0)
		{
			throw new InvalidOperationException("Mesh must contain index data.");
		}

		var indexBufferSize = (ulong) (sizeof(uint) * indices.Length);
		var indexBufferDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = indexBufferSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};

		ComPtr<ID3D12Resource> indexBuffer;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&defaultHeapProps,
				HeapFlags.None,
				in indexBufferDesc,
				ResourceStates.CopyDest,
				null,
				out indexBuffer));

		ComPtr<ID3D12Resource> indexUpload;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&uploadHeapProps,
				HeapFlags.None,
				in indexBufferDesc,
				ResourceStates.GenericRead,
				null,
				out indexUpload));

		void* mappedIndices = null;
		SilkMarshal.ThrowHResult(indexUpload.Map(0, (Range*) null, &mappedIndices));
		try
		{
			fixed (uint* srcIndices = indices)
			{
				Buffer.MemoryCopy(srcIndices, mappedIndices, indexBufferSize, indexBufferSize);
			}
		}
		finally
		{
			indexUpload.Unmap(0, (Range*) null);
		}

		// Create a temporary command list for uploading mesh data
		SilkMarshal.ThrowHResult(_commandAllocators[0].Reset());

		ComPtr<ID3D12GraphicsCommandList> uploadCommandList;
		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				CommandListType.Direct,
				_commandAllocators[0],
				default,
				out uploadCommandList));

		uploadCommandList.CopyBufferRegion(vertexBuffer.Handle, 0, vertexUpload.Handle, 0, vertexBufferSize);
		uploadCommandList.CopyBufferRegion(indexBuffer.Handle, 0, indexUpload.Handle, 0, indexBufferSize);

		var vertexBarrier = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		vertexBarrier.Anonymous.Transition = new()
		{
			PResource = vertexBuffer.Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopyDest,
			StateAfter = ResourceStates.VertexAndConstantBuffer
		};
		uploadCommandList.ResourceBarrier(1, &vertexBarrier);

		var indexBarrier = new ResourceBarrier
			{Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		indexBarrier.Anonymous.Transition = new()
		{
			PResource = indexBuffer.Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopyDest,
			StateAfter = ResourceStates.IndexBuffer
		};
		uploadCommandList.ResourceBarrier(1, &indexBarrier);

		SilkMarshal.ThrowHResult(uploadCommandList.Close());
		ID3D12CommandList* copyLists = (ID3D12CommandList*) uploadCommandList.Handle;
		_commandQueue.ExecuteCommandLists(1, &copyLists);
		SignalAndWait();

		uploadCommandList.Dispose();

		vertexUpload.Dispose();
		indexUpload.Dispose();

		var vertexView = new D3DVertexBufferView
		{
			BufferLocation = vertexBuffer.GetGPUVirtualAddress(),
			SizeInBytes = (uint) vertexBufferSize,
			StrideInBytes = vertexStride
		};

		var indexView = new D3DIndexBufferView
		{
			BufferLocation = indexBuffer.GetGPUVirtualAddress(),
			SizeInBytes = (uint) indexBufferSize,
			Format = Format.FormatR32Uint
		};

		// Wrap in abstraction and set on mesh
		var vertexBufferAbstraction = new D3D12Buffer(
			"MeshVertexBuffer",
			new(vertexBufferSize, BufferUsage.Vertex),
			vertexBuffer,
			vertexBufferSize);

		var indexBufferAbstraction = new D3D12Buffer(
			"MeshIndexBuffer",
			new(indexBufferSize, BufferUsage.Index),
			indexBuffer,
			indexBufferSize);

		mesh.VertexBuffer = vertexBufferAbstraction;
		mesh.IndexBuffer = indexBufferAbstraction;
		mesh.StrideInBytes = vertexStride;
		mesh.IndexCount = (uint) indices.Length;

		return new(vertexBuffer, indexBuffer, vertexView, indexView, (uint) indices.Length);
	}

	public void EnsureMeshResources(Mesh mesh)
	{
		// TODO: Do we need to check for this? 
		if (_meshResources.TryGetValue(mesh, out var resources))
		{
			return;
		}

		resources = CreateMeshResources(mesh);
		_meshResources.Add(mesh, resources);
	}

	private static ulong Align(ulong size, ulong alignment)
	{
		return (size + alignment - 1) & ~(alignment - 1);
	}

	private void OnUpdate(double deltaSeconds)
	{
		// Command processing is now handled by the render graph
	}

	private void OnFramebufferResize(Int2 newSize)
	{
		if (newSize.X == 0 || newSize.Y == 0)
		{
			return;
		}

		_framebufferSize = newSize;

		WaitForGpu();

		for (var i = 0; i < FrameCount; i++)
		{
			if (_renderTargets[i].Handle is not null)
			{
				_renderTargets[i].Dispose();
			}
		}

		if (_rtvHeap.Handle is not null)
		{
			_rtvHeap.Dispose();
		}

		SilkMarshal.ThrowHResult(_swapchain.ResizeBuffers(FrameCount, (uint) newSize.X, (uint) newSize.Y,
			Format.FormatB8G8R8A8Unorm, 0));
		_backbufferIndex = _swapchain.GetCurrentBackBufferIndex();

		CreateRtvHeapAndTargets();
		if (_gfxDevice is ITexturePoolDevice poolDevice)
		{
			poolDevice.ClearTexturePool();
		}
	}

	public void BeginFrame()
	{
		var frameIdx = _backbufferIndex;

		if (_fence.GetCompletedValue() < _frameFenceValues[frameIdx])
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_frameFenceValues[frameIdx], (void*) _fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}

		if (_fence.GetCompletedValue() < _fenceValue)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*) _fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}

		// Command list creation is now handled per-pass by the render graph
	}

	public void Render(
		float deltaTime,
		RenderGraphResourceRegistry resourceRegistry,
		RenderGraphResourceHandle backBuffer,
		RenderGraphResourceHandle presentedTexture,
		UiFrameData uiFrame)
	{
		var backbufferResource = resourceRegistry.GetTexture(backBuffer);
		var backbufferTexture = backbufferResource as ID3D12BackendTexture
		                        ?? throw new InvalidOperationException(
			                        "Render graph returned a texture incompatible with the Direct3D12 backend.");

		var presentedResource = resourceRegistry.GetTexture(presentedTexture) as ID3D12BackendTexture
		                        ?? throw new InvalidOperationException(
			                        "Presented texture was not compatible with the Direct3D12 backend.");

		// Copy deferred lighting output into the backbuffer, then present
		var presentCommandList = _gfxDevice.BeginGraphics() as D3D12CommandList
		                         ?? throw new InvalidOperationException("Failed to create present command list.");
		var nativeCommandList = (ID3D12GraphicsCommandList*) presentCommandList.CommandList.Handle;

		ResourceBarrier* barriers = stackalloc ResourceBarrier[2];
		barriers[0] = new() {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		barriers[0].Anonymous.Transition = new()
		{
			PResource = presentedResource.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.UnorderedAccess,
			StateAfter = ResourceStates.CopySource
		};
		barriers[1] = new() {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		barriers[1].Anonymous.Transition = new()
		{
			PResource = backbufferTexture.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.Present,
			StateAfter = ResourceStates.CopyDest
		};
		nativeCommandList->ResourceBarrier(2, barriers);

		nativeCommandList->CopyResource(backbufferTexture.Resource, presentedResource.Resource);

		barriers[0].Anonymous.Transition.StateBefore = ResourceStates.CopySource;
		barriers[0].Anonymous.Transition.StateAfter = ResourceStates.UnorderedAccess;
		barriers[1].Anonymous.Transition.StateBefore = ResourceStates.CopyDest;
		barriers[1].Anonymous.Transition.StateAfter = ResourceStates.RenderTarget;
		nativeCommandList->ResourceBarrier(2, barriers);

		_imguiRenderer.EnsureResources(_gfxDevice, uiFrame);
		var imguiContext = new RenderGraphContext(resourceRegistry, "ImGui")
		{
			CommandList = presentCommandList
		};
		_imguiRenderer.Record(imguiContext, uiFrame, backbufferResource);

		barriers[1].Anonymous.Transition.StateBefore = ResourceStates.RenderTarget;
		barriers[1].Anonymous.Transition.StateAfter = ResourceStates.Present;
		nativeCommandList->ResourceBarrier(1, barriers + 1);

		_gfxDevice.Submit(presentCommandList);

		var presentResult = _swapchain.Present(1, 0);
		if (presentResult < 0)
		{
			var removalReason = _device.GetDeviceRemovedReason();
			var message =
				$"IDXGISwapChain::Present failed with HRESULT 0x{presentResult:X8}. DeviceRemovedReason=0x{removalReason:X8}.";

			throw new InvalidOperationException(message);
		}

		_fenceValue++;
		SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
		_frameFenceValues[_backbufferIndex] = _fenceValue;

		_backbufferIndex = _swapchain.GetCurrentBackBufferIndex();
	}

	public RenderGraphResourceHandle ImportBackbuffer(RenderGraphResourceRegistry registry,
		int width, int height)
	{
		var descriptor = new TextureDescriptor(
			Math.Max(width, 1),
			Math.Max(height, 1),
			TextureFormat.Bgra8Unorm,
			TextureUsage.RenderTarget,
			Vector4.Zero);

		var imported = _gfxDevice.ImportExternalTexture(
			descriptor,
			_renderTargets[_backbufferIndex].Handle,
			_rtvCpuHandles[_backbufferIndex],
			null);

		return registry.ImportTexture(imported, takeOwnership: false, initialState: ResourceState.Present);
	}

	private void SignalAndWait()
	{
		_fenceValue++;
		SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
		if (_fence.GetCompletedValue() < _fenceValue)
		{
			SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*) _fenceEvent));
			WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
		}
	}

	private void WaitForGpu()
	{
		_fenceValue++;
		SilkMarshal.ThrowHResult(_commandQueue.Signal(_fence, _fenceValue));
		SilkMarshal.ThrowHResult(_fence.SetEventOnCompletion(_fenceValue, (void*) _fenceEvent));
		WaitForSingleObject(_fenceEvent, 0xFFFFFFFF);
	}

	private void Dispose()
	{
		if (_commandQueue.Handle is not null && _fence.Handle is not null)
		{
			SignalAndWait();
		}

		for (var i = 0; i < FrameCount; i++)
		{
			_renderTargets[i].Dispose();
			_commandAllocators[i].Dispose();
		}

		// Command lists are now created per-pass
		_rtvHeap.Dispose();

		_factory.Dispose();
		_swapchain.Dispose();
		_commandQueue.Dispose();

		foreach (var meshResources in _meshResources.Values)
		{
			if (meshResources.VertexBuffer.Handle is not null)
			{
				meshResources.VertexBuffer.Dispose();
			}

			if (meshResources.IndexBuffer.Handle is not null)
			{
				meshResources.IndexBuffer.Dispose();
			}
		}

		_meshResources.Clear();

		// TODO: material disposal should be handled by render graph

		if (_fence.Handle is not null)
		{
			_fence.Dispose();
			_fence = default;
		}

		if (_gfxDevice is ITexturePoolDevice poolDevice)
		{
			poolDevice.ClearTexturePool();
		}

		_imguiRenderer.Dispose();

		_device.Dispose();
		_d3d12.Dispose();
		_dxgi.Dispose();

		if (_fenceEvent != nint.Zero)
		{
			CloseHandle(_fenceEvent);
			_fenceEvent = nint.Zero;
		}

		if (_inputContext is not null)
		{
			foreach (var keyboard in _keyboards)
			{
				keyboard.KeyDown -= HandleKeyDown;
				keyboard.KeyUp -= HandleKeyUp;
				keyboard.KeyChar -= HandleKeyChar;
			}

			foreach (var mouse in _mice)
			{
				mouse.MouseMove -= HandleMouseMove;
				mouse.Scroll -= HandleMouseScroll;
				mouse.MouseDown -= HandleMouseDown;
				mouse.MouseUp -= HandleMouseUp;
			}

			_keyboards.Clear();
			_mice.Clear();
			_hasMousePosition = false;

			//_inputContext.Dispose();
			_inputContext = null;
		}

		if (_window is not null)
		{
			_window.Load -= OnWindowLoad;
			_window.Update -= OnWindowUpdate;
			_window.Render -= OnWindowRender;
			_window.FramebufferResize -= OnWindowFramebufferResize;
			_window.Closing -= OnWindowClosing;
			_window.Dispose();
			_window = null;
		}

		_isInitialized = false;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern nint CreateEventEx(nint lpEventAttributes, string lpName, uint dwFlags, uint dwDesiredAccess);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint hObject);
}
