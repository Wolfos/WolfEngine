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
using WolfEngine.Platform;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Backend.D3D12;
using WolfEngine.Rendering.UI;
using WolfEngine.Rendering.Shaders;
using BackendD3D12Texture = WolfEngine.Backend.D3D12.D3D12Texture;
using AbstractionFillMode = WolfEngine.Rendering.Abstraction.FillMode;
using AbstractionCullMode = WolfEngine.Rendering.Abstraction.CullMode;
using AbstractionDepthStencilFormat = WolfEngine.Rendering.Abstraction.DepthStencilFormat;
using Box = Silk.NET.Direct3D12.Box;
using Range = Silk.NET.Direct3D12.Range;

namespace WolfEngine;

public unsafe class WolfRendererD3D : IRenderer
{
private const int FrameCount = 2;
private const ulong DefaultPackedVertexBufferBytes = 256UL * 1024UL * 1024UL;
private const ulong DefaultPackedIndexBufferBytes = 128UL * 1024UL * 1024UL;

	private sealed class MeshResources
	{
		public MeshResources(
		ulong vertexOffsetBytes,
		ulong indexOffsetBytes,
		int baseVertex,
			uint indexCount)
		{
			VertexOffsetBytes = vertexOffsetBytes;
			IndexOffsetBytes = indexOffsetBytes;
			BaseVertex = baseVertex;
			IndexCount = indexCount;
		}

		public ulong VertexOffsetBytes { get; }

		public ulong IndexOffsetBytes { get; }

		public int BaseVertex { get; }

		public uint IndexCount { get; }
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	private struct VertexData
	{
		public Vector3 Position;
		public Vector3 Normal;
		public Vector2 TexCoord;
		public Vector4 Tangent;
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

	private int _width;
	private int _height;
	private readonly IShaderProvider _shaderCompiler;
	private readonly IArenaAllocator _arenaAllocator;
	private readonly IInputSystem _inputSystem;
	private readonly WindowChromeController _windowChromeController;
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
	private IGfxBuffer? _packedVertexBuffer;
	private IGfxBuffer? _packedIndexBuffer;
	private ulong _packedVertexBufferUsedBytes;
	private ulong _packedIndexBufferUsedBytes;

	private uint _backbufferIndex;
	private nint _windowHandle;
	private Int2 _framebufferSize = Int2.Zero;
	private readonly List<IKeyboard> _keyboards = new();
	private readonly List<IMouse> _mice = new();
	private Vector2 _lastMousePosition;
	private bool _hasMouseInput;
	private readonly IImGuiInputSink _imguiInputSink;
	private readonly bool[] _imguiMouseButtons = new bool[5];
	private Vector2 _imguiMousePosition;
	private Vector2 _imguiMouseWheel;
	private readonly object _frameCaptureSync = new();
	private TaskCompletionSource<FrameCapture>? _pendingFrameCapture;
	private DescriptorHandle _defaultMaterialSamplerHandle = DescriptorHandle.Invalid;
	private static readonly Guid DxgiDebugAll = new("e48ae283-da80-490b-87e6-43e9a9cfda08");

	public WolfRendererD3D(
		IShaderProvider shaderCompiler,
		IArenaAllocator arenaAllocator,
		IInputSystem inputSystem,
		IImGuiInputSink imguiSystem,
		WindowChromeController windowChromeController)
	{
		_width = 1600;
		_height = 900;
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_arenaAllocator = arenaAllocator ?? throw new ArgumentNullException(nameof(arenaAllocator));
		_inputSystem = inputSystem ?? throw new ArgumentNullException(nameof(inputSystem));
		_imguiInputSink = imguiSystem ?? throw new ArgumentNullException(nameof(imguiSystem));
		_windowChromeController = windowChromeController ?? throw new ArgumentNullException(nameof(windowChromeController));
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

	public void SetWindowSize(Int2 size)
	{
		if (size.X <= 0 || size.Y <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(size), "Window size must be positive.");
		}

		if (_isInitialized)
		{
			throw new InvalidOperationException("Window size must be configured before the renderer starts.");
		}

		_width = size.X;
		_height = size.Y;
	}

	public Task<FrameCapture> CaptureNextFrameAsync(CancellationToken cancellationToken = default)
	{
		var completion = new TaskCompletionSource<FrameCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_frameCaptureSync)
		{
			if (_pendingFrameCapture is not null)
			{
				throw new InvalidOperationException("A frame capture is already pending.");
			}

			_pendingFrameCapture = completion;
		}

		if (cancellationToken.CanBeCanceled)
		{
			cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
		}

		return completion.Task;
	}

	public void CompletePendingFrameCapture(RenderGraphResourceRegistry resourceRegistry, RenderGraphResourceHandle sceneColor)
	{
		TaskCompletionSource<FrameCapture>? completion;
		lock (_frameCaptureSync)
		{
			completion = _pendingFrameCapture;
			if (completion is null || completion.Task.IsCompleted)
			{
				return;
			}

			_pendingFrameCapture = null;
		}

		try
		{
			var texture = resourceRegistry.GetTexture(sceneColor);
			if (texture is not ID3D12BackendTexture d3dTexture)
			{
				throw new InvalidOperationException("The automation capture color target was not created by the Direct3D12 backend.");
			}

			var width = texture.Descriptor.Width;
			var height = texture.Descriptor.Height;
			if (width <= 0 || height <= 0)
			{
				throw new InvalidOperationException("The automation capture color target had an invalid size.");
			}

			if (texture.Descriptor.Format is not (TextureFormat.Rgba8Unorm or TextureFormat.Bgra8Unorm))
			{
				throw new InvalidOperationException($"Frame capture only supports RGBA8 or BGRA8 color targets, but got '{texture.Descriptor.Format}'.");
			}

			var rowPitch = Align((ulong)width * 4, D3D12.TextureDataPitchAlignment);
			using var readbackBuffer = _gfxDevice.CreateBuffer(new BufferDescriptor(rowPitch * (ulong)height, BufferUsage.Staging)) as D3D12Buffer
				?? throw new InvalidOperationException("Direct3D12 did not create a readable frame-capture buffer.");
			var priorState = resourceRegistry.GetResourceState(sceneColor);
			var commandList = _gfxDevice.BeginGraphics();
			try
			{
				commandList.Barrier(new ResourceBarrierDescription(texture, priorState, ResourceState.CopySource));
				if (commandList is not D3D12CommandList d3dCommandList)
				{
					throw new InvalidOperationException("Frame capture expected a Direct3D12 command list.");
				}

				var destination = new TextureCopyLocation
				{
					PResource = readbackBuffer.Resource.Handle,
					Type = TextureCopyType.PlacedFootprint
				};
				destination.Anonymous.PlacedFootprint = new PlacedSubresourceFootprint
				{
					Offset = 0,
					Footprint = new SubresourceFootprint
					{
						Format = texture.Descriptor.Format == TextureFormat.Bgra8Unorm ? Format.FormatB8G8R8A8Unorm : Format.FormatR8G8B8A8Unorm,
						Width = (uint)width,
						Height = (uint)height,
						Depth = 1,
						RowPitch = (uint)rowPitch
					}
				};
				var source = new TextureCopyLocation
				{
					PResource = d3dTexture.Resource,
					Type = TextureCopyType.SubresourceIndex
				};
				source.Anonymous.SubresourceIndex = 0;
				d3dCommandList.NativeCommandList->CopyTextureRegion(&destination, 0, 0, 0, &source, (Box*)null);
				commandList.Barrier(new ResourceBarrierDescription(texture, ResourceState.CopySource, priorState));
			}
			finally
			{
				_gfxDevice.Submit(commandList);
				_gfxDevice.WaitForIdle();
			}

			var raw = new byte[checked((int)(rowPitch * (ulong)height))];
			readbackBuffer.Read(raw);
			var rgba8 = new byte[checked(width * height * 4)];
			for (var y = 0; y < height; y++)
			{
				Buffer.BlockCopy(raw, checked((int)((ulong)y * rowPitch)), rgba8, y * width * 4, width * 4);
			}

			if (texture.Descriptor.Format == TextureFormat.Bgra8Unorm)
			{
				for (var index = 0; index < rgba8.Length; index += 4)
				{
					(rgba8[index], rgba8[index + 2]) = (rgba8[index + 2], rgba8[index]);
				}
			}

			completion.TrySetResult(new FrameCapture(width, height, rgba8));
		}
		catch (Exception exception)
		{
			completion.TrySetException(exception);
		}
	}

	public void RequestShutdown()
	{
		_window?.Close();
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
		if (_hasMouseInput == false)
		{
			_inputSystem.SetAxis2D(InputActionBinding.MouseDelta, Vector2.Zero);
		}

		_hasMouseInput = false;

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

		try
		{
			_renderCallback((float) deltaTime);
		}
		catch
		{
			DumpDxgiDebugMessages("OnWindowRender failure");
			throw;
		}
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
		
		var delta = current - _lastMousePosition;
		_inputSystem.SetAxis2D(InputActionBinding.MouseDelta, delta);
		_hasMouseInput = true;

		_lastMousePosition = current;
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
			Key.B => ImGuiKey.B,
			Key.C => ImGuiKey.C,
			Key.D => ImGuiKey.D,
			Key.E => ImGuiKey.E,
			Key.F => ImGuiKey.F,
			Key.G => ImGuiKey.G,
			Key.H => ImGuiKey.H,
			Key.I => ImGuiKey.I,
			Key.J => ImGuiKey.J,
			Key.K => ImGuiKey.K,
			Key.L => ImGuiKey.L,
			Key.M => ImGuiKey.M,
			Key.N => ImGuiKey.N,
			Key.O => ImGuiKey.O,
			Key.P => ImGuiKey.P,
			Key.Q => ImGuiKey.Q,
			Key.R => ImGuiKey.R,
			Key.S => ImGuiKey.S,
			Key.T => ImGuiKey.T,
			Key.U => ImGuiKey.U,
			Key.V => ImGuiKey.V,
			Key.W => ImGuiKey.W,
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
		options.WindowBorder = WindowBorder.Hidden;

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

		_windowChromeController.AttachToWindow(_windowHandle);
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

		_factory = _dxgi.CreateDXGIFactory<IDXGIFactory2>();
		CreateDeviceAndQueue();
		ConfigureDxgiDebugQueueIfRequested();
		CreateSwapchain();
		CreateRtvHeapAndTargets();
		CreateCommandAllocatorsAndList();
		CreateSyncObjects();
	}

	private void CreateDeviceAndQueue()
	{
		CreateDeviceForHardwareAdapter();
		var commandQueueDescription = new CommandQueueDesc
		{
			Type = CommandListType.Direct,
			Priority = (int)CommandQueuePriority.Normal,
			Flags = CommandQueueFlags.None,
			NodeMask = 0
		};

		SilkMarshal.ThrowHResult(_device.CreateCommandQueue(in commandQueueDescription, out _commandQueue));

		_gfxDevice = new(_device, _commandQueue);
	}

	private void CreateDeviceForHardwareAdapter()
	{
		var lastCreateDeviceResult = 0;
		var factory = (IDXGIFactory1*) _factory.Handle;
		for (uint adapterIndex = 0; ; adapterIndex++)
		{
			IDXGIAdapter* candidateAdapterPointer = null;
			var enumerateResult = factory->EnumAdapters(adapterIndex, &candidateAdapterPointer);
			if (enumerateResult < 0)
			{
				break;
			}

			ComPtr<IDXGIAdapter> candidateAdapter = new(candidateAdapterPointer);
			ComPtr<ID3D12Device> candidateDevice = default;
			var createDeviceResult = _d3d12.CreateDevice(
				candidateAdapter,
				D3DFeatureLevel.Level122,
				out candidateDevice);
			if (createDeviceResult >= 0)
			{
				_adapter = candidateAdapter;
				_device = candidateDevice;
				return;
			}

			lastCreateDeviceResult = createDeviceResult;
			candidateDevice.Dispose();
			candidateAdapter.Dispose();
		}

		throw new InvalidOperationException(
			$"Unable to create a Direct3D 12 device for any hardware adapter. " +
			$"The last D3D12CreateDevice result was 0x{lastCreateDeviceResult:X8}.");
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
				TextureFormat.Rgba8Unorm,     // Material
				TextureFormat.Rgba16Float     // Emissive
			}),
			depthStencil: new AbstractionDepthStencilFormat(TextureFormat.D32Float),
			renderState: renderState,
			layout: GraphicsLayoutKind.Material);

		var shaderSet = _shaderCompiler.GetGraphicsShaderWithReflection(
			EngineShaderPrograms.GBuffer, "vertexShader", "fragmentShader", GraphicsBackendKind.D3D12).Bytecode;
		var pipeline = _gfxDevice.GetOrCreatePipeline(pipelineKey, shaderSet);

		var albedoResources = material.AlbedoTexture?.Resources
		                      ?? throw new InvalidOperationException("Material is missing albedo texture resources.");
		var ormResources = material.OrmTexture?.Resources
		                   ?? throw new InvalidOperationException("Material is missing ORM texture resources.");
		var normalResources = material.NormalTexture?.Resources;
		var emissiveResources = material.EmissiveTexture?.Resources;

		return new D3D12MaterialResources
		{
			Pipeline = pipeline,
			AlbedoTexture = albedoResources.ShaderResourceView,
			OrmTexture = ormResources.ShaderResourceView,
			NormalTexture = normalResources?.ShaderResourceView ?? DescriptorHandle.Invalid,
			EmissiveTexture = emissiveResources?.ShaderResourceView ?? DescriptorHandle.Invalid,
			Sampler = GetOrCreateDefaultMaterialSampler()
		};
	}

	public ITextureResources CreateTextureResources(Texture texture)
	{
		if (texture is null)
		{
			throw new ArgumentNullException(nameof(texture));
		}

		if (texture.MipLevels is null || texture.MipLevels.Length == 0)
		{
			throw new ArgumentException("Texture must contain mip data.", nameof(texture));
		}

		var supportsUnorderedAccess = SupportsUnorderedAccess(texture);
		var texDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong)texture.Width,
			Height = (uint)texture.Height,
			DepthOrArraySize = 1,
			MipLevels = (ushort)texture.MipCount,
			Format = ToDxgiTextureFormat(texture.Format, texture.IsSrgb),
			SampleDesc = new(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = supportsUnorderedAccess ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None
		};

		var defaultHeap = new HeapProperties(HeapType.Default);
		ComPtr<ID3D12Resource> gpuTexture;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&defaultHeap,
				HeapFlags.None,
				in texDesc,
				ResourceStates.CopyDest,
				null,
				out gpuTexture));

		var subresourceCount = texture.MipCount;
		var layouts = new PlacedSubresourceFootprint[subresourceCount];
		var numRows = new uint[subresourceCount];
		var rowSizeInBytes = new ulong[subresourceCount];
		ulong totalSizeInBytes = 0;
		fixed (PlacedSubresourceFootprint* layoutPtr = layouts)
		fixed (uint* numRowsPtr = numRows)
		fixed (ulong* rowSizePtr = rowSizeInBytes)
		{
			_device.GetCopyableFootprints(
				texDesc,
				0,
				(uint)subresourceCount,
				0,
				layoutPtr,
				numRowsPtr,
				rowSizePtr,
				&totalSizeInBytes);
		}

		var uploadDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = totalSizeInBytes,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};

		var uploadHeap = new HeapProperties(HeapType.Upload);
		ComPtr<ID3D12Resource> uploadBuffer;
		SilkMarshal.ThrowHResult(
			_device.CreateCommittedResource(
				&uploadHeap,
				HeapFlags.None,
				in uploadDesc,
				ResourceStates.GenericRead,
				null,
				out uploadBuffer));

		byte* mapped = null;
		SilkMarshal.ThrowHResult(uploadBuffer.Map(0, (Range*)null, (void**)&mapped));
		try
		{
			for (var mipIndex = 0; mipIndex < subresourceCount; mipIndex++)
			{
				var mip = texture.MipLevels[mipIndex];
				fixed (byte* src = mip.Data)
				{
					var rowPitch = layouts[mipIndex].Footprint.RowPitch;
					var sourceRowSize = (ulong)TextureFormatUtilities.GetBytesPerRow(texture.Format, mip.Width);
					for (uint row = 0; row < numRows[mipIndex]; row++)
					{
						var destRow = mapped + layouts[mipIndex].Offset + row * rowPitch;
						var srcRow = src + row * sourceRowSize;
						Buffer.MemoryCopy(srcRow, destRow, sourceRowSize, sourceRowSize);
					}
				}
			}
		}
		finally
		{
			uploadBuffer.Unmap(0, (Range*)null);
		}

		SilkMarshal.ThrowHResult(_commandAllocators[0].Reset());
		ComPtr<ID3D12GraphicsCommandList> uploadCommandList;
		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				CommandListType.Direct,
				_commandAllocators[0],
				default,
				out uploadCommandList));

		for (var mipIndex = 0; mipIndex < subresourceCount; mipIndex++)
		{
			var destLocation = new TextureCopyLocation
			{
				PResource = gpuTexture.Handle,
				Type = TextureCopyType.SubresourceIndex
			};
			destLocation.Anonymous.SubresourceIndex = (uint)mipIndex;

			var srcLocation = new TextureCopyLocation
			{
				PResource = uploadBuffer.Handle,
				Type = TextureCopyType.PlacedFootprint
			};
			srcLocation.Anonymous.PlacedFootprint = layouts[mipIndex];

			uploadCommandList.CopyTextureRegion(&destLocation, 0, 0, 0, &srcLocation, (Box*)null);
		}

		var textureBarrier = new ResourceBarrier { Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None };
		textureBarrier.Anonymous.Transition = new()
		{
			PResource = gpuTexture.Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopyDest,
			StateAfter = ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource
		};
		uploadCommandList.ResourceBarrier(1, &textureBarrier);

		SilkMarshal.ThrowHResult(uploadCommandList.Close());
		ID3D12CommandList* uploadLists = (ID3D12CommandList*)uploadCommandList.Handle;
		_commandQueue.ExecuteCommandLists(1, &uploadLists);
		SignalAndWait();

		uploadCommandList.Dispose();
		uploadBuffer.Dispose();

		var descriptor = new TextureDescriptor(
			texture.Width,
			texture.Height,
			texture.Format,
			supportsUnorderedAccess ? TextureUsage.ShaderResource | TextureUsage.UnorderedAccess : TextureUsage.ShaderResource,
			mipLevels: texture.MipCount,
			isSrgb: texture.IsSrgb);

		var backendTexture = new BackendD3D12Texture();
		backendTexture.Initialize(texture.Name, descriptor, gpuTexture);
		var srvHandle = _gfxDevice.GlobalTable.AllocateShaderResourceView(backendTexture);
		var uavHandle = supportsUnorderedAccess
			? _gfxDevice.GlobalTable.AllocateUnorderedAccessView(backendTexture)
			: DescriptorHandle.Invalid;
		backendTexture.SetHandles(
			srvHandle,
			DescriptorHandle.Invalid,
			uavHandle,
			_gfxDevice.GlobalTable as D3D12DescriptorTable);

		return new D3D12TextureResources
		{
			Texture = backendTexture,
			ShaderResourceView = srvHandle
		};
	}

	private static bool SupportsUnorderedAccess(Texture texture)
	{
		return texture is not null &&
		       texture.IsSrgb == false &&
		       (texture.Format == TextureFormat.Rgba8Unorm || texture.Format == TextureFormat.Bgra8Unorm);
	}

	private static Format ToDxgiTextureFormat(TextureFormat format, bool isSrgb)
	{
		return format switch
		{
			TextureFormat.Bgra8Unorm => isSrgb ? Format.FormatB8G8R8A8UnormSrgb : Format.FormatB8G8R8A8Unorm,
			TextureFormat.Rgba8Unorm => isSrgb ? Format.FormatR8G8B8A8UnormSrgb : Format.FormatR8G8B8A8Unorm,
			TextureFormat.Rgba8Uint => Format.FormatR8G8B8A8Uint,
			TextureFormat.R16Unorm => Format.FormatR16Unorm,
			TextureFormat.Bc1Unorm => isSrgb ? Format.FormatBC1UnormSrgb : Format.FormatBC1Unorm,
			TextureFormat.Bc3Unorm => isSrgb ? Format.FormatBC3UnormSrgb : Format.FormatBC3Unorm,
			TextureFormat.Bc4Unorm => Format.FormatBC4Unorm,
			TextureFormat.Bc5Unorm => Format.FormatBC5Unorm,
			TextureFormat.Bc7Unorm => isSrgb ? Format.FormatBC7UnormSrgb : Format.FormatBC7Unorm,
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported runtime texture format.")
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

	public Int2 GetWindowSize()
	{
		if (_window is null)
		{
			return _framebufferSize;
		}

		var size = _window.Size;
		if (size.X <= 0 || size.Y <= 0)
		{
			return _framebufferSize;
		}

		return new Int2(size.X, size.Y);
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


	private void EnsurePackedGeometryBuffers()
	{
		_packedVertexBuffer ??= _gfxDevice.CreateBuffer(new BufferDescriptor(
			DefaultPackedVertexBufferBytes,
			BufferUsage.Vertex | BufferUsage.Structured,
			BufferFlags.AllowShaderResource));
		_packedIndexBuffer ??= _gfxDevice.CreateBuffer(new BufferDescriptor(
			DefaultPackedIndexBufferBytes,
			BufferUsage.Index | BufferUsage.Structured,
			BufferFlags.AllowShaderResource));
	}

	private MeshResources CreateMeshResources(Mesh mesh)
	{
		ArgumentNullException.ThrowIfNull(mesh);
		if (mesh.Vertices.Length == 0 || mesh.Indices.Length == 0)
		{
			throw new InvalidOperationException("Mesh must contain vertex and index data.");
		}

		EnsurePackedGeometryBuffers();
		var vertexBuffer = _packedVertexBuffer ?? throw new InvalidOperationException("Packed mesh vertex buffer was not created.");
		var indexBuffer = _packedIndexBuffer ?? throw new InvalidOperationException("Packed mesh index buffer was not created.");
		if (vertexBuffer is not IWritableGpuBuffer writableVertexBuffer ||
		    indexBuffer is not IWritableGpuBuffer writableIndexBuffer)
		{
			throw new InvalidOperationException("Direct3D12 packed mesh buffers must support CPU uploads.");
		}

		var vertexCount = mesh.Vertices.Length;
		var vertices = new VertexData[vertexCount];
		for (var i = 0; i < vertexCount; i++)
		{
			vertices[i].Position = new Vector3(mesh.Vertices[i].X, mesh.Vertices[i].Y, mesh.Vertices[i].Z);
			vertices[i].Normal = i < mesh.Normals.Length ? mesh.Normals[i] : Vector3.UnitY;
			vertices[i].TexCoord = i < mesh.UVs.Length ? mesh.UVs[i] : Vector2.Zero;
			vertices[i].Tangent = i < mesh.Tangents.Length ? mesh.Tangents[i] : new Vector4(1, 0, 0, 1);
		}

		var vertexStride = (uint)Unsafe.SizeOf<VertexData>();
		var vertexDataSize = (ulong)vertexStride * (uint)vertexCount;
		var indexDataSize = (ulong)sizeof(uint) * (uint)mesh.Indices.Length;
		var vertexOffsetBytes = Align(_packedVertexBufferUsedBytes, vertexStride);
		var indexOffsetBytes = Align(_packedIndexBufferUsedBytes, sizeof(uint));
		if (vertexOffsetBytes + vertexDataSize > vertexBuffer.Descriptor.SizeInBytes ||
		    indexOffsetBytes + indexDataSize > indexBuffer.Descriptor.SizeInBytes)
		{
			throw new InvalidOperationException(
				$"Packed geometry capacity exceeded. requiredVertexBytes={vertexOffsetBytes + vertexDataSize}, " +
				$"vertexCapacity={vertexBuffer.Descriptor.SizeInBytes}, requiredIndexBytes={indexOffsetBytes + indexDataSize}, " +
				$"indexCapacity={indexBuffer.Descriptor.SizeInBytes}.");
		}

		writableVertexBuffer.Write<VertexData>(vertices, vertexOffsetBytes / vertexStride);
		writableIndexBuffer.Write<uint>(mesh.Indices, indexOffsetBytes / sizeof(uint));

		mesh.VertexBuffer = vertexBuffer;
		mesh.IndexBuffer = indexBuffer;
		mesh.StrideInBytes = vertexStride;
		mesh.IndexCount = (uint)mesh.Indices.Length;
		mesh.PackedVertexOffsetBytes = vertexOffsetBytes;
		mesh.PackedIndexOffsetBytes = indexOffsetBytes;
		mesh.PackedBaseVertex = checked((int)(vertexOffsetBytes / vertexStride));
		_packedVertexBufferUsedBytes = vertexOffsetBytes + vertexDataSize;
		_packedIndexBufferUsedBytes = indexOffsetBytes + indexDataSize;

		return new MeshResources(vertexOffsetBytes, indexOffsetBytes, mesh.PackedBaseVertex, mesh.IndexCount);
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

	public void ReleaseMeshResources(Mesh mesh)
	{
		ArgumentNullException.ThrowIfNull(mesh);
		if (_meshResources.Remove(mesh) == false)
		{
			return;
		}

		mesh.VertexBuffer = null!;
		mesh.IndexBuffer = null!;
		mesh.StrideInBytes = 0;
		mesh.IndexCount = 0;
		mesh.PackedVertexOffsetBytes = 0;
		mesh.PackedIndexOffsetBytes = 0;
		mesh.PackedBaseVertex = 0;
	}

	public IGfxBuffer GetPackedMeshVertexBuffer() => _packedVertexBuffer!;

	public IGfxBuffer GetPackedMeshIndexBuffer() => _packedIndexBuffer!;

	public bool SupportsGpuCapture => false;

	public bool IsGpuCaptureActive => false;

	public string LastGpuCapturePath => string.Empty;

	public bool TryStartGpuCapture(string outputPath, out string error)
	{
		error = "Programmatic GPU capture is only supported on the Metal renderer.";
		return false;
	}

	public bool TryStopGpuCapture(out string error)
	{
		error = "Programmatic GPU capture is only supported on the Metal renderer.";
		return false;
	}

	private static ulong Align(ulong size, ulong alignment)
	{
		if (alignment == 0)
		{
			return size;
		}

		// The packed vertex stride is 48 bytes, which is not a power of two.
		// Bit-mask alignment silently produced offsets such as 64 for a 48-byte
		// stride. CPU uploads divided that offset by the stride, while DXR used
		// the raw byte offset, making the BLAS read a different vertex range.
		return checked(((size + alignment - 1) / alignment) * alignment);
	}

	private DescriptorHandle GetOrCreateDefaultMaterialSampler()
	{
		if (_defaultMaterialSamplerHandle.IsValid)
		{
			return _defaultMaterialSamplerHandle;
		}

		var sampler = new SamplerDescriptor(
			FilterMode.Anisotropic,
			AddressMode.Wrap,
			AddressMode.Wrap,
			AddressMode.Wrap,
			maxAnisotropy: 16.0f);
		_defaultMaterialSamplerHandle = _gfxDevice.GlobalTable.AllocateSampler(sampler);
		return _defaultMaterialSamplerHandle;
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

		Screen.CurrentResolution = newSize;
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
		_gfxDevice.ResetConstantUploadStats();

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
		RenderGraphResourceRegistry resourceRegistry,
		RenderGraphResourceHandle finalColor)
	{
		var finalColorResource = resourceRegistry.GetTexture(finalColor);
		var finalColorTexture = finalColorResource as ID3D12BackendTexture
		                        ?? throw new InvalidOperationException(
			                        "Render graph returned a texture incompatible with the Direct3D12 backend.");
		var swapchainBackbuffer = _renderTargets[_backbufferIndex].Handle;
		if (swapchainBackbuffer is null)
		{
			throw new InvalidOperationException("Active swapchain backbuffer is unavailable.");
		}

		var presentCommandList = _gfxDevice.BeginGraphics() as D3D12CommandList
		                         ?? throw new InvalidOperationException("Failed to create present command list.");
		var nativeCommandList = (ID3D12GraphicsCommandList*) presentCommandList.CommandList.Handle;

		ResourceBarrier finalColorToCopySource = new() {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		finalColorToCopySource.Anonymous.Transition = new()
		{
			PResource = finalColorTexture.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.RenderTarget,
			StateAfter = ResourceStates.CopySource
		};
		nativeCommandList->ResourceBarrier(1, &finalColorToCopySource);

		ResourceBarrier swapchainToCopyDest = new() {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		swapchainToCopyDest.Anonymous.Transition = new()
		{
			PResource = swapchainBackbuffer,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.Present,
			StateAfter = ResourceStates.CopyDest
		};
		nativeCommandList->ResourceBarrier(1, &swapchainToCopyDest);

		nativeCommandList->CopyResource(swapchainBackbuffer, finalColorTexture.Resource);

		ResourceBarrier finalColorBackToRenderTarget = new() {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		finalColorBackToRenderTarget.Anonymous.Transition = new()
		{
			PResource = finalColorTexture.Resource,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopySource,
			StateAfter = ResourceStates.RenderTarget
		};
		nativeCommandList->ResourceBarrier(1, &finalColorBackToRenderTarget);

		ResourceBarrier swapchainToPresent = new() {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		swapchainToPresent.Anonymous.Transition = new()
		{
			PResource = swapchainBackbuffer,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopyDest,
			StateAfter = ResourceStates.Present
		};
		nativeCommandList->ResourceBarrier(1, &swapchainToPresent);

		_gfxDevice.Submit(presentCommandList);

		var presentInterval = Screen.VSyncEnabled ? 1u : 0u;
		var presentResult = _swapchain.Present(presentInterval, 0);
		if (presentResult < 0)
		{
			DumpDxgiDebugMessages("Present failure");
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

	private void DumpDxgiDebugMessages(string context)
	{
		if (GraphicsConfig.EnableD3DDebugLayer == false)
		{
			return;
		}

		ComPtr<IDXGIInfoQueue> infoQueue = default;
		try
		{
			var hr = _dxgi.GetDebugInterface1(0, out infoQueue);
			if (hr < 0 || infoQueue.Handle is null)
			{
				return;
			}

			var messageCount = infoQueue.GetNumStoredMessages(DxgiDebugAll);
			if (messageCount == 0)
			{
				return;
			}

			Console.WriteLine($"DXGI debug messages ({context}): {messageCount}");
			for (ulong i = 0; i < messageCount; i++)
			{
				nuint messageLength = 0;
				hr = infoQueue.GetMessageA(DxgiDebugAll, i, (InfoQueueMessage*)null, ref messageLength);
				if (hr < 0 || messageLength == 0)
				{
					continue;
				}

				var messageMemory = NativeMemory.Alloc(messageLength);
				try
				{
					var message = (InfoQueueMessage*)messageMemory;
					hr = infoQueue.GetMessageA(DxgiDebugAll, i, message, ref messageLength);
					if (hr < 0)
					{
						continue;
					}

					var descriptionLength = checked((int)message->DescriptionByteLength);
					if (descriptionLength > 0)
					{
						descriptionLength--;
					}

					var description = Marshal.PtrToStringAnsi((nint)message->PDescription, descriptionLength) ?? string.Empty;
					Console.WriteLine($"[DXGI {message->Severity}] ({message->Category}/{message->ID}) {description}");
				}
				finally
				{
					NativeMemory.Free(messageMemory);
				}
			}

			infoQueue.ClearStoredMessages(DxgiDebugAll);
		}
		catch
		{
			// Best-effort diagnostics only.
		}
		finally
		{
			if (infoQueue.Handle is not null)
			{
				infoQueue.Dispose();
			}
		}
	}

	private void ConfigureDxgiDebugQueueIfRequested()
	{
		if (GraphicsConfig.EnableD3DDebugLayer == false)
		{
			return;
		}

		ComPtr<IDXGIInfoQueue> infoQueue = default;
		try
		{
			var hr = _dxgi.GetDebugInterface1(0, out infoQueue);
			if (hr < 0 || infoQueue.Handle is null)
			{
				return;
			}

			infoQueue.SetMuteDebugOutput(DxgiDebugAll, new Silk.NET.Core.Bool32(0));
			infoQueue.SetBreakOnSeverity(DxgiDebugAll, InfoQueueMessageSeverity.Corruption, new Silk.NET.Core.Bool32(1));
			infoQueue.SetBreakOnSeverity(DxgiDebugAll, InfoQueueMessageSeverity.Error, new Silk.NET.Core.Bool32(1));
		}
		catch
		{
			// Best-effort diagnostics only.
		}
		finally
		{
			if (infoQueue.Handle is not null)
			{
				infoQueue.Dispose();
			}
		}
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

		_swapchain.Dispose();
		_factory.Dispose();
		_commandQueue.Dispose();

		_meshResources.Clear();
		(_packedVertexBuffer as IDisposable)?.Dispose();
		(_packedIndexBuffer as IDisposable)?.Dispose();
		_packedVertexBuffer = null;
		_packedIndexBuffer = null;

		// TODO: material disposal should be handled by render graph

		if (_fence.Handle is not null)
		{
			_fence.Dispose();
			_fence = default;
		}

		if (_gfxDevice is IDisposable disposableDevice)
		{
			disposableDevice.Dispose();
		}

		_device.Dispose();
		_adapter.Dispose();
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

			//_inputContext.Dispose();
			_inputContext = null;
		}

		if (_window is not null)
		{
			_windowChromeController.DetachWindow();
			_window.Load -= OnWindowLoad;
			_window.Update -= OnWindowUpdate;
			_window.Render -= OnWindowRender;
			_window.FramebufferResize -= OnWindowFramebufferResize;
			_window.Closing -= OnWindowClosing;
			try
			{
				_window.Dispose();
			}
			catch (InvalidOperationException ex) when (ex.Message.Contains("inside of the render loop", StringComparison.Ordinal))
			{
				// Silk.NET can still report render-loop context while unwinding from callback exceptions.
			}
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
