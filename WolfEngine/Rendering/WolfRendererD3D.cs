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
using WolfEngine.TestGame;
using AbstractionFillMode = WolfEngine.Rendering.Abstraction.FillMode;
using AbstractionCullMode = WolfEngine.Rendering.Abstraction.CullMode;
using AbstractionDepthStencilFormat = WolfEngine.Rendering.Abstraction.DepthStencilFormat;
using D3DVertexBufferView = Silk.NET.Direct3D12.VertexBufferView;
using D3DIndexBufferView = Silk.NET.Direct3D12.IndexBufferView;
using D3DViewport = Silk.NET.Direct3D12.Viewport;
using D3DRect = Silk.NET.Maths.Box2D<int>;
using D3DBox = Silk.NET.Direct3D12.Box;
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
	private IntPtr _imGuiContext;
	private ComPtr<ID3D12DescriptorHeap> _imguiSrvHeap;
	private GpuDescriptorHandle _imguiSrvGpuHandle;
	private ComPtr<ID3D12Resource> _imguiFontTexture;
	private ComPtr<ID3D12PipelineState> _imguiPipelineState;
	private ComPtr<ID3D12RootSignature> _imguiRootSignature;
	private ComPtr<ID3D12Resource> _imguiVertexBuffer;
	private ComPtr<ID3D12Resource> _imguiIndexBuffer;
	private int _imguiVertexBufferSize;
	private int _imguiIndexBufferSize;
	private ImDrawDataPtr? _imGuiDrawData;
	private readonly bool[] _imguiMouseButtons = new bool[5];
	private Vector2 _imguiMousePosition;
	private Vector2 _imguiMouseWheel;

	public WolfRendererD3D(IShaderCompiler shaderCompiler, IArenaAllocator arenaAllocator, IInputSystem inputSystem)
	{
		_width = 1280;
		_height = 720;
		_shaderCompiler = shaderCompiler ?? throw new ArgumentNullException(nameof(shaderCompiler));
		_arenaAllocator = arenaAllocator ?? throw new ArgumentNullException(nameof(arenaAllocator));
		_inputSystem = inputSystem ?? throw new ArgumentNullException(nameof(inputSystem));
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
		InitializeImGui();

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

		BeginImGuiFrame((float) deltaTime);
		GUI.Draw();
		ImGui.Render();
		_imGuiDrawData = ImGui.GetDrawData();

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
		UpdateImGuiKey(key, true);

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
		UpdateImGuiKey(key, false);
	}

	private void HandleKeyChar(IKeyboard keyboard, char keyChar)
	{
		if (_imGuiContext == IntPtr.Zero)
		{
			return;
		}

		ImGui.SetCurrentContext(_imGuiContext);
		ImGui.GetIO().AddInputCharacter(keyChar);
	}

	private void HandleMouseMove(IMouse mouse, Vector2 position)
	{
		var current = position;
		_inputSystem.SetAxis2D(InputActionBinding.MousePosition, current);
		_imguiMousePosition = current;

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
	}

	private void InitializeImGui()
	{
		if (_imGuiContext != IntPtr.Zero)
		{
			return;
		}

		_imGuiContext = ImGui.CreateContext();
		ImGui.SetCurrentContext(_imGuiContext);

		var io = ImGui.GetIO();
		io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
		io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

		CreateImGuiFontTexture();
		CreateImGuiPipeline();
	}

	private void BeginImGuiFrame(float deltaTime)
	{
		if (_imGuiContext == IntPtr.Zero)
		{
			return;
		}

		ImGui.SetCurrentContext(_imGuiContext);
		var io = ImGui.GetIO();
		io.DisplaySize = new System.Numerics.Vector2(_framebufferSize.X, _framebufferSize.Y);
		io.DeltaTime = Math.Max(deltaTime, 1e-6f);
		io.DisplayFramebufferScale = System.Numerics.Vector2.One;

		for (var i = 0; i < _imguiMouseButtons.Length; i++)
		{
			io.MouseDown[i] = _imguiMouseButtons[i];
		}

		if (_hasMousePosition)
		{
			io.MousePos = _imguiMousePosition;
		}
		else
		{
			io.MousePos = new System.Numerics.Vector2(-1, -1);
		}

		io.MouseWheel = _imguiMouseWheel.Y;
		io.MouseWheelH = _imguiMouseWheel.X;
		_imguiMouseWheel = Vector2.Zero;

		ImGui.NewFrame();
	}

	private void UpdateImGuiKey(Key key, bool pressed)
	{
		if (_imGuiContext == IntPtr.Zero)
		{
			return;
		}

		ImGui.SetCurrentContext(_imGuiContext);
		if (TryConvertKey(key, out var imguiKey))
		{
			ImGui.GetIO().AddKeyEvent(imguiKey, pressed);
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

	private void CreateImGuiFontTexture()
	{
		ImGui.SetCurrentContext(_imGuiContext);
		var io = ImGui.GetIO();
		byte* pixels;
		int width;
		int height;
		int bpp;
		io.Fonts.GetTexDataAsRGBA32(out pixels, out width, out height, out bpp);

		var textureDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Texture2D,
			Alignment = 0,
			Width = (ulong) width,
			Height = (uint) height,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatR8G8B8A8Unorm,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutUnknown,
			Flags = ResourceFlags.None
		};

		var defaultHeap = new HeapProperties(HeapType.Default);
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&defaultHeap,
			HeapFlags.None,
			in textureDesc,
			ResourceStates.CopyDest,
			null,
			out _imguiFontTexture));

		PlacedSubresourceFootprint layout = default;
		uint numRows = 0;
		ulong rowSize = 0;
		ulong uploadSize = 0;
		_device.GetCopyableFootprints(in textureDesc, 0, 1, 0, &layout, &numRows, &rowSize, &uploadSize);

		var uploadDesc = new ResourceDesc
		{
			Dimension = ResourceDimension.Buffer,
			Alignment = 0,
			Width = uploadSize,
			Height = 1,
			DepthOrArraySize = 1,
			MipLevels = 1,
			Format = Format.FormatUnknown,
			SampleDesc = new SampleDesc(1, 0),
			Layout = TextureLayout.LayoutRowMajor,
			Flags = ResourceFlags.None
		};

		var uploadHeap = new HeapProperties(HeapType.Upload);
		ComPtr<ID3D12Resource> uploadBuffer;
		SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
			&uploadHeap,
			HeapFlags.None,
			in uploadDesc,
			ResourceStates.GenericRead,
			null,
			out uploadBuffer));

		byte* mapped = null;
		SilkMarshal.ThrowHResult(uploadBuffer.Map(0, (Range*) null, (void**) &mapped));
		var src = (byte*) pixels;
		for (var row = 0u; row < numRows; row++)
		{
			var dest = mapped + layout.Offset + row * layout.Footprint.RowPitch;
			var srcRow = src + row * (ulong) width * 4;
			Buffer.MemoryCopy(srcRow, dest, layout.Footprint.RowPitch, (ulong) width * 4);
		}

		uploadBuffer.Unmap(0, (Range*) null);

		SilkMarshal.ThrowHResult(_commandAllocators[0].Reset());
		ComPtr<ID3D12GraphicsCommandList> uploadList;
		SilkMarshal.ThrowHResult(
			_device.CreateCommandList<ID3D12CommandAllocator, ID3D12PipelineState, ID3D12GraphicsCommandList>(
				0,
				CommandListType.Direct,
				_commandAllocators[0],
				default,
				out uploadList));

		var dstLocation = new TextureCopyLocation
		{
			PResource = _imguiFontTexture.Handle,
			Type = TextureCopyType.TextureCopyTypeSubresourceIndex
		};
		dstLocation.Anonymous.SubresourceIndex = 0;

		var srcLocation = new TextureCopyLocation
		{
			PResource = uploadBuffer.Handle,
			Type = TextureCopyType.TextureCopyTypePlacedFootprint
		};
		srcLocation.Anonymous.PlacedFootprint = layout;

		uploadList.CopyTextureRegion(&dstLocation, 0, 0, 0, &srcLocation, (D3DBox*) null);

		var barrier = new ResourceBarrier {Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None};
		barrier.Anonymous.Transition = new()
		{
			PResource = _imguiFontTexture.Handle,
			Subresource = D3D12.ResourceBarrierAllSubresources,
			StateBefore = ResourceStates.CopyDest,
			StateAfter = ResourceStates.PixelShaderResource
		};
		uploadList.ResourceBarrier(1, &barrier);
		SilkMarshal.ThrowHResult(uploadList.Close());

		ID3D12CommandList* copyLists = (ID3D12CommandList*) uploadList.Handle;
		_commandQueue.ExecuteCommandLists(1, &copyLists);
		SignalAndWait();

		uploadList.Dispose();
		uploadBuffer.Dispose();

		var heapDesc = new DescriptorHeapDesc
		{
			Type = DescriptorHeapType.CbvSrvUav,
			NumDescriptors = 1,
			Flags = DescriptorHeapFlags.ShaderVisible,
			NodeMask = 0
		};

		SilkMarshal.ThrowHResult(_device.CreateDescriptorHeap(in heapDesc, out _imguiSrvHeap));
		_imguiSrvGpuHandle = _imguiSrvHeap.GetGPUDescriptorHandleForHeapStart();
		var srvCpuHandle = _imguiSrvHeap.GetCPUDescriptorHandleForHeapStart();

		const uint DefaultShader4ComponentMapping = 5768; // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING

		var srvDesc = new ShaderResourceViewDesc
		{
			Shader4ComponentMapping = DefaultShader4ComponentMapping,
			Format = Format.FormatR8G8B8A8Unorm,
			ViewDimension = SrvDimension.Texture2D
		};
		srvDesc.Anonymous.Texture2D = new Tex2DSrv
		{
			MipLevels = 1,
			MostDetailedMip = 0,
			ResourceMinLODClamp = 0.0f
		};

		_device.CreateShaderResourceView(_imguiFontTexture, srvDesc, srvCpuHandle);
		io.Fonts.SetTexID(new IntPtr((long) _imguiSrvGpuHandle.Ptr));
	}

	private void CreateImGuiPipeline()
	{
		var sampler = stackalloc StaticSamplerDesc[1];
		sampler[0] = new StaticSamplerDesc
		{
			Filter = Filter.FilterMinMagMipLinear,
			AddressU = TextureAddressMode.TextureAddressModeClamp,
			AddressV = TextureAddressMode.TextureAddressModeClamp,
			AddressW = TextureAddressMode.TextureAddressModeClamp,
			ShaderVisibility = ShaderVisibility.Pixel,
			ComparisonFunc = ComparisonFunc.Always,
			ShaderRegister = 0,
			RegisterSpace = 0
		};

		var descriptorRanges = stackalloc DescriptorRange[1];
		descriptorRanges[0] = new DescriptorRange
		{
			RangeType = DescriptorRangeType.Srv,
			NumDescriptors = 1,
			BaseShaderRegister = 0,
			RegisterSpace = 0,
			OffsetInDescriptorsFromTableStart = 0
		};

		var rootParameters = stackalloc RootParameter[2];
		rootParameters[0] = default;
		rootParameters[0].ParameterType = RootParameterType.TypeDescriptorTable;
		rootParameters[0].Anonymous.DescriptorTable = new()
		{
			NumDescriptorRanges = 1,
			PDescriptorRanges = descriptorRanges
		};
		rootParameters[0].ShaderVisibility = ShaderVisibility.Pixel;

		rootParameters[1] = default;
		rootParameters[1].ParameterType = RootParameterType.Type32BitConstants;
		rootParameters[1].Anonymous.Constants = new()
		{
			ShaderRegister = 0,
			RegisterSpace = 0,
			Num32BitValues = 16
		};
		rootParameters[1].ShaderVisibility = ShaderVisibility.Vertex;

		var rootSignatureDesc = new RootSignatureDesc
		{
			NumParameters = 2,
			PParameters = rootParameters,
			NumStaticSamplers = 1,
			PStaticSamplers = sampler,
			Flags = RootSignatureFlags.AllowInputAssemblerInputLayout
		};

		var versionedDesc = new VersionedRootSignatureDesc
		{
			Version = D3DRootSignatureVersion.Version10
		};
		versionedDesc.Anonymous.Desc10 = rootSignatureDesc;

		ID3D10Blob* rootSignatureBlob = null;
		ID3D10Blob* rootSignatureError = null;
		var serializeResult =
			_d3d12.SerializeVersionedRootSignature(&versionedDesc, &rootSignatureBlob, &rootSignatureError);
		try
		{
			HandleRootSignatureErrors(serializeResult, rootSignatureError, "imgui");

			SilkMarshal.ThrowHResult(_device.CreateRootSignature(
				0,
				rootSignatureBlob->GetBufferPointer(),
				rootSignatureBlob->GetBufferSize(),
				out _imguiRootSignature));
		}
		finally
		{
			if (rootSignatureBlob is not null)
			{
				rootSignatureBlob->Release();
			}

			if (rootSignatureError is not null)
			{
				rootSignatureError->Release();
			}
		}

		var vertexShader = _shaderCompiler.GetDxil("imgui.slang", "vertexShader", "vs_6_0");
		var pixelShader = _shaderCompiler.GetDxil("imgui.slang", "fragmentShader", "ps_6_0");

		Span<byte> positionSemantic = stackalloc byte["POSITION".Length + 1];
		Span<byte> texcoordSemantic = stackalloc byte["TEXCOORD".Length + 1];
		Span<byte> colorSemantic = stackalloc byte["COLOR".Length + 1];
		"POSITION"u8.CopyTo(positionSemantic);
		"TEXCOORD"u8.CopyTo(texcoordSemantic);
		"COLOR"u8.CopyTo(colorSemantic);

		var inputElements = stackalloc InputElementDesc[3];
		fixed (byte* positionPtr = positionSemantic)
		fixed (byte* texcoordPtr = texcoordSemantic)
		fixed (byte* colorPtr = colorSemantic)
		{
			inputElements[0] = new InputElementDesc
			{
				SemanticName = positionPtr,
				SemanticIndex = 0,
				Format = Format.FormatR32G32Float,
				InputSlot = 0,
				AlignedByteOffset = 0,
				InputSlotClass = InputClassification.PerVertexData,
				InstanceDataStepRate = 0
			};

			inputElements[1] = new InputElementDesc
			{
				SemanticName = texcoordPtr,
				SemanticIndex = 0,
				Format = Format.FormatR32G32Float,
				InputSlot = 0,
				AlignedByteOffset = 8,
				InputSlotClass = InputClassification.PerVertexData,
				InstanceDataStepRate = 0
			};

			inputElements[2] = new InputElementDesc
			{
				SemanticName = colorPtr,
				SemanticIndex = 0,
				Format = Format.FormatR8G8B8A8Unorm,
				InputSlot = 0,
				AlignedByteOffset = 16,
				InputSlotClass = InputClassification.PerVertexData,
				InstanceDataStepRate = 0
			};

			var inputLayout = new InputLayoutDesc
			{
				PInputElementDescs = inputElements,
				NumElements = 3
			};

			var blendState = new BlendDesc
			{
				AlphaToCoverageEnable = 0,
				IndependentBlendEnable = 0
			};
			blendState.RenderTarget[0] = new RenderTargetBlendDesc
			{
				BlendEnable = 1,
				LogicOpEnable = 0,
				SrcBlend = Blend.BlendSrcAlpha,
				DestBlend = Blend.BlendInvSrcAlpha,
				BlendOp = BlendOp.BlendOpAdd,
				SrcBlendAlpha = Blend.BlendOne,
				DestBlendAlpha = Blend.BlendInvSrcAlpha,
				BlendOpAlpha = BlendOp.BlendOpAdd,
				LogicOp = LogicOp.LogicOpNoop,
				RenderTargetWriteMask = 0x0F
			};

			var rasterizerState = new RasterizerDesc
			{
				FillMode = Silk.NET.Direct3D12.FillMode.Solid,
				CullMode = Silk.NET.Direct3D12.CullMode.None,
				FrontCounterClockwise = 0,
				DepthBias = 0,
				DepthBiasClamp = 0.0f,
				SlopeScaledDepthBias = 0.0f,
				DepthClipEnable = 1,
				MultisampleEnable = 0,
				AntialiasedLineEnable = 0,
				ForcedSampleCount = 0,
				ConservativeRaster = ConservativeRasterizationMode.Off
			};

			var depthStencilState = new DepthStencilDesc
			{
				DepthEnable = 0,
				DepthWriteMask = DepthWriteMask.Zero,
				DepthFunc = ComparisonFunc.Always,
				StencilEnable = 0,
				StencilReadMask = 0,
				StencilWriteMask = 0
			};

			fixed (byte* vsPtr = vertexShader)
			fixed (byte* psPtr = pixelShader)
			{
				var shaderBytecodeVS = new ShaderBytecode {PShaderBytecode = vsPtr, BytecodeLength = (nuint) vertexShader.Length};
				var shaderBytecodePS = new ShaderBytecode {PShaderBytecode = psPtr, BytecodeLength = (nuint) pixelShader.Length};

				var psoDesc = new GraphicsPipelineStateDesc
				{
					PRootSignature = _imguiRootSignature.Handle,
					VS = shaderBytecodeVS,
					PS = shaderBytecodePS,
					BlendState = blendState,
					SampleMask = uint.MaxValue,
					RasterizerState = rasterizerState,
					DepthStencilState = depthStencilState,
					InputLayout = inputLayout,
					IBStripCutValue = IndexBufferStripCutValue.ValueDisabled,
					PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
					NumRenderTargets = 1,
					DSVFormat = Format.FormatUnknown,
					SampleDesc = new SampleDesc(1, 0),
					NodeMask = 0,
					CachedPSO = default,
					Flags = PipelineStateFlags.None
				};

				psoDesc.RTVFormats[0] = Format.FormatB8G8R8A8Unorm;

				SilkMarshal.ThrowHResult(_device.CreateGraphicsPipelineState(in psoDesc, out _imguiPipelineState));
			}
		}
	}

	private static void HandleRootSignatureErrors(int result, ID3D10Blob* errorBlob, string kind)
	{
		string? errorMessage = null;
		if (errorBlob is not null)
		{
			errorMessage = Marshal.PtrToStringAnsi((nint) errorBlob->GetBufferPointer());
			errorBlob->Release();
		}

		if (result < 0)
		{
			throw new InvalidOperationException($"Failed to serialize {kind} root signature: {errorMessage ?? "Unknown error"}");
		}
	}

	private void EnsureImGuiBuffers(ImDrawDataPtr drawData)
	{
		var vertexBytes = drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>();
		var indexBytes = drawData.TotalIdxCount * sizeof(ushort);

		if (_imguiVertexBuffer.Handle is null || _imguiVertexBufferSize < vertexBytes)
		{
			_imguiVertexBuffer.Dispose();
			_imguiVertexBufferSize = (int) Math.Max(vertexBytes, 65536);

			var desc = new ResourceDesc
			{
				Dimension = ResourceDimension.Buffer,
				Alignment = 0,
				Width = (ulong) _imguiVertexBufferSize,
				Height = 1,
				DepthOrArraySize = 1,
				MipLevels = 1,
				Format = Format.FormatUnknown,
				SampleDesc = new SampleDesc(1, 0),
				Layout = TextureLayout.LayoutRowMajor,
				Flags = ResourceFlags.None
			};

			var heap = new HeapProperties(HeapType.Upload);
			SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
				&heap,
				HeapFlags.None,
				in desc,
				ResourceStates.GenericRead,
				null,
				out _imguiVertexBuffer));
		}

		if (_imguiIndexBuffer.Handle is null || _imguiIndexBufferSize < indexBytes)
		{
			_imguiIndexBuffer.Dispose();
			_imguiIndexBufferSize = (int) Math.Max(indexBytes, 65536);

			var desc = new ResourceDesc
			{
				Dimension = ResourceDimension.Buffer,
				Alignment = 0,
				Width = (ulong) _imguiIndexBufferSize,
				Height = 1,
				DepthOrArraySize = 1,
				MipLevels = 1,
				Format = Format.FormatUnknown,
				SampleDesc = new SampleDesc(1, 0),
				Layout = TextureLayout.LayoutRowMajor,
				Flags = ResourceFlags.None
			};

			var heap = new HeapProperties(HeapType.Upload);
			SilkMarshal.ThrowHResult(_device.CreateCommittedResource(
				&heap,
				HeapFlags.None,
				in desc,
				ResourceStates.GenericRead,
				null,
				out _imguiIndexBuffer));
		}
	}

	private void RenderImGui(ID3D12GraphicsCommandList* commandList)
	{
		if (_imGuiContext == IntPtr.Zero || _imGuiDrawData is null || _imguiPipelineState.Handle is null)
		{
			return;
		}

		var drawData = _imGuiDrawData.Value;
		if (drawData.CmdListsCount == 0)
		{
			return;
		}

		EnsureImGuiBuffers(drawData);

		var vertexBytes = drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>();
		var indexBytes = drawData.TotalIdxCount * sizeof(ushort);

		byte* vertexMapped = null;
		byte* indexMapped = null;

		SilkMarshal.ThrowHResult(_imguiVertexBuffer.Map(0, (Range*) null, (void**) &vertexMapped));
		SilkMarshal.ThrowHResult(_imguiIndexBuffer.Map(0, (Range*) null, (void**) &indexMapped));

		nint vertexOffset = 0;
		nint indexOffset = 0;
		var nativeDrawData = drawData.NativePtr;
		var cmdListArray = (ImDrawList**) nativeDrawData->CmdLists.Data;
		for (var n = 0; n < nativeDrawData->CmdListsCount; n++)
		{
			var cmdList = new ImDrawListPtr(cmdListArray[n]);

			var vtxSize = cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
			Buffer.MemoryCopy((void*) cmdList.VtxBuffer.Data, vertexMapped + vertexOffset, vertexBytes - vertexOffset, vtxSize);
			vertexOffset += vtxSize;

			var idxSize = cmdList.IdxBuffer.Size * sizeof(ushort);
			Buffer.MemoryCopy((void*) cmdList.IdxBuffer.Data, indexMapped + indexOffset, indexBytes - indexOffset, idxSize);
			indexOffset += idxSize;
		}

		_imguiVertexBuffer.Unmap(0, (Range*) null);
		_imguiIndexBuffer.Unmap(0, (Range*) null);

		var rtvHandle = _rtvCpuHandles[_backbufferIndex];
		commandList->OMSetRenderTargets(1, &rtvHandle, 0, null);

		var viewport = new D3DViewport
		{
			TopLeftX = 0,
			TopLeftY = 0,
			Width = _framebufferSize.X,
			Height = _framebufferSize.Y,
			MinDepth = 0.0f,
			MaxDepth = 1.0f
		};
		commandList->RSSetViewports(1, &viewport);

		ID3D12DescriptorHeap* heaps = _imguiSrvHeap.Handle;
		commandList->SetDescriptorHeaps(1, &heaps);

		commandList->SetGraphicsRootSignature(_imguiRootSignature.Handle);
		commandList->SetPipelineState(_imguiPipelineState.Handle);

		commandList->IASetPrimitiveTopology(Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

		var vbView = new D3DVertexBufferView
		{
			BufferLocation = _imguiVertexBuffer.GetGPUVirtualAddress(),
			StrideInBytes = (uint) Unsafe.SizeOf<ImDrawVert>(),
			SizeInBytes = (uint) _imguiVertexBufferSize
		};

		var ibView = new D3DIndexBufferView
		{
			BufferLocation = _imguiIndexBuffer.GetGPUVirtualAddress(),
			SizeInBytes = (uint) _imguiIndexBufferSize,
			Format = Format.FormatR16Uint
		};

		commandList->IASetVertexBuffers(0, 1, &vbView);
		commandList->IASetIndexBuffer(&ibView);

		fixed (GpuDescriptorHandle* srvHandle = &_imguiSrvGpuHandle)
		{
			commandList->SetGraphicsRootDescriptorTable(0, *srvHandle);
		}

		var L = drawData.DisplayPos.X;
		var R = drawData.DisplayPos.X + drawData.DisplaySize.X;
		var T = drawData.DisplayPos.Y;
		var B = drawData.DisplayPos.Y + drawData.DisplaySize.Y;
		Span<float> projection = stackalloc float[16];
		projection[0] = 2.0f / (R - L);
		projection[1] = 0.0f;
		projection[2] = 0.0f;
		projection[3] = 0.0f;

		projection[4] = 0.0f;
		projection[5] = 2.0f / (T - B);
		projection[6] = 0.0f;
		projection[7] = 0.0f;

		projection[8] = 0.0f;
		projection[9] = 0.0f;
		projection[10] = 0.5f;
		projection[11] = 0.0f;

		projection[12] = (R + L) / (L - R);
		projection[13] = (T + B) / (B - T);
		projection[14] = 0.5f;
		projection[15] = 1.0f;

		fixed (float* projPtr = projection)
		{
			commandList->SetGraphicsRoot32BitConstants(1, 16, projPtr, 0);
		}

		var globalVtxOffset = 0;
		var globalIdxOffset = 0;
		for (var n = 0; n < nativeDrawData->CmdListsCount; n++)
		{
			var cmdList = new ImDrawListPtr(cmdListArray[n]);
			for (var cmdIndex = 0; cmdIndex < cmdList.CmdBuffer.Size; cmdIndex++)
			{
				var cmd = cmdList.CmdBuffer[cmdIndex];

				var clip = cmd.ClipRect;
				var clipX1 = (int) Math.Floor(clip.X - drawData.DisplayPos.X);
				var clipY1 = (int) Math.Floor(clip.Y - drawData.DisplayPos.Y);
				var clipX2 = (int) Math.Ceiling(clip.Z - drawData.DisplayPos.X);
				var clipY2 = (int) Math.Ceiling(clip.W - drawData.DisplayPos.Y);

				if (clipX1 < 0) clipX1 = 0;
				if (clipY1 < 0) clipY1 = 0;
				if (clipX2 > _framebufferSize.X) clipX2 = _framebufferSize.X;
				if (clipY2 > _framebufferSize.Y) clipY2 = _framebufferSize.Y;
				if (clipX2 <= clipX1 || clipY2 <= clipY1)
				{
					continue;
				}

				var clipRect = new D3DRect(clipX1, clipY1, clipX2, clipY2);
				commandList->RSSetScissorRects(1, &clipRect);

				commandList->DrawIndexedInstanced(cmd.ElemCount, 1, (uint) (cmd.IdxOffset + globalIdxOffset),
					(int) (cmd.VtxOffset + globalVtxOffset), 0);
			}

			globalIdxOffset += cmdList.IdxBuffer.Size;
			globalVtxOffset += cmdList.VtxBuffer.Size;
		}
	}

	private void DisposeImGui()
	{
		if (_imGuiContext != IntPtr.Zero)
		{
			ImGui.DestroyContext(_imGuiContext);
			_imGuiContext = IntPtr.Zero;
		}

		if (_imguiVertexBuffer.Handle is not null)
		{
			_imguiVertexBuffer.Dispose();
			_imguiVertexBuffer = default;
		}

		if (_imguiIndexBuffer.Handle is not null)
		{
			_imguiIndexBuffer.Dispose();
			_imguiIndexBuffer = default;
		}

		if (_imguiPipelineState.Handle is not null)
		{
			_imguiPipelineState.Dispose();
			_imguiPipelineState = default;
		}

		if (_imguiRootSignature.Handle is not null)
		{
			_imguiRootSignature.Dispose();
			_imguiRootSignature = default;
		}

		if (_imguiFontTexture.Handle is not null)
		{
			_imguiFontTexture.Dispose();
			_imguiFontTexture = default;
		}

		if (_imguiSrvHeap.Handle is not null)
		{
			_imguiSrvHeap.Dispose();
			_imguiSrvHeap = default;
		}
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
		RenderGraphResourceHandle presentedTexture)
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

		RenderImGui(nativeCommandList);

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

		DisposeImGui();

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
